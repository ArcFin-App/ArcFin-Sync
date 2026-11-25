using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArcfinSync.Api.Household;
using Jellyfin.Plugin.ArcfinSync.Api.Security;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ArcfinSync.Api.Notifications;

/// <summary>
/// News for Arcfin to show as notifications: Seerr request and issue changes, announcements and
/// planned maintenance. Arcfin asks when it opens and when the system lets it run in the background;
/// nothing is pushed.
/// </summary>
[ApiController]
[Route("ArcfinSync/Notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    public const string WebhookSecretName = "notifications.seerrWebhookSecret";

    /// <summary>Maintenance is announced once it's this close.</summary>
    public static readonly TimeSpan MaintenanceNotice = TimeSpan.FromHours(24);

    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;

    public NotificationsController(IAuthorizationContext authContext, IUserManager userManager)
    {
        _authContext = authContext;
        _userManager = userManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    /// <summary>
    /// Events after <paramref name="since"/>, and the announcements and maintenance showing now
    /// (each time, as they can start later than they were saved). Without <paramref name="since"/>,
    /// only the cursor and what's showing now, which a device records without announcing.
    /// </summary>
    [HttpGet("Events")]
    public async Task<ActionResult<EventsDto>> GetEvents([FromQuery] long? since)
    {
        if (!Config.Notifications.Enabled)
        {
            return NotFound();
        }

        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null)
        {
            return Unauthorized();
        }

        var store = EventStore.Current;
        var cursor = store.Cursor;
        var events = since is { } after ? store.After(after, user.Id) : new List<ServerEvent>();
        events.AddRange(CurrentNotices(Config, user.Id, DateTime.UtcNow));
        return new EventsDto { Cursor = cursor, Events = events.Select(EventDto.From).ToList() };
    }

    public static IEnumerable<ServerEvent> CurrentNotices(PluginConfiguration config, Guid userId, DateTime now)
    {
        foreach (var a in Notices.ActiveFor(config.Announcements, userId, now))
        {
            yield return new ServerEvent { Id = $"announcement.{a.Id:N}", Kind = "announcement", Title = a.Title, Body = a.Message, Starts = a.Starts, Ends = a.Ends };
        }

        foreach (var m in Notices.Upcoming(config.Maintenance, now).Where(m => m.Starts - now <= MaintenanceNotice))
        {
            yield return new ServerEvent { Id = $"maintenance.{m.Id:N}", Kind = "maintenance", Body = m.Message, Starts = m.Starts, Ends = m.Ends };
        }
    }

    /// <summary>
    /// For Seerr's webhook agent, whose Authorization header carries the secret shown on the
    /// dashboard. Its JSON payload names the request and what happened (<see cref="SeerrPayload"/>);
    /// the server then reads that request from Seerr before anything is recorded. Seerr's Test
    /// button gets a plain success.
    /// </summary>
    [HttpPost("Seerr")]
    [AllowAnonymous]
    [RequestSizeLimit(65536)]
    public async Task<ActionResult> SeerrWebhook()
    {
        if (!Config.Notifications.Enabled)
        {
            return NotFound();
        }

        var secret = SecretVault.Current.Get(WebhookSecretName);
        var given = Request.Headers.Authorization.ToString();
        var log = WebhookLog.Current;
        if (string.IsNullOrEmpty(secret) || !Matches(given, secret))
        {
            log.Record(string.IsNullOrWhiteSpace(given) ? "missingSecret" : "wrongSecret", null, DateTime.UtcNow);
            return Unauthorized();
        }

        using var reader = new System.IO.StreamReader(Request.Body);
        var (type, requestId, issueId) = ReadPayload(await reader.ReadToEndAsync(HttpContext.RequestAborted).ConfigureAwait(false));
        log.Record(type is null ? "unreadable" : type == "TEST_NOTIFICATION" ? "test" : "ok", type, DateTime.UtcNow);
        if (type != "TEST_NOTIFICATION")
        {
            SeerrWatchService.Wake(requestId, SeerrRequests.KindOf(type), issueId);
        }

        return NoContent();
    }

    /// <summary>The payload to paste into Seerr's JSON Payload field.</summary>
    public const string SeerrPayload = "{\n  \"notification_type\": \"{{notification_type}}\",\n  \"request_id\": \"{{request_id}}\",\n  \"issue_id\": \"{{issue_id}}\"\n}";

    /// <summary>notification_type, request_id and issue_id from a webhook; anything unreadable is null.</summary>
    public static (string? Type, int? RequestId, int? IssueId) ReadPayload(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return (null, null, null);
            }

            var type = root.TryGetProperty("notification_type", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() : null;
            return (type, Id(root, "request_id"), Id(root, "issue_id"));
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>Seerr fills its variables in as text, and leaves them empty when they don't apply.</summary>
    private static int? Id(System.Text.Json.JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var n) ? n
            : value.ValueKind == System.Text.Json.JsonValueKind.String
              && int.TryParse(value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    public static bool Matches(string given, string secret)
    {
        var a = Encoding.UTF8.GetBytes(given.Trim());
        var b = Encoding.UTF8.GetBytes(secret);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}

/// <summary>The dashboard's view: on or off, and what to paste into Seerr's webhook settings.</summary>
[ApiController]
[Route("ArcfinSync/Admin/Notifications")]
[Authorize(Policy = Policies.RequiresElevation)]
public class NotificationsAdminController : ControllerBase
{
    [HttpGet]
    public ActionResult<NotificationsAdminDto> Get() => NotificationsAdminDto.Current();

    /// <summary>A null <see cref="NotificationsAdminBody.WebhookAddress"/> keeps the saved one; empty clears it.</summary>
    [HttpPut]
    public ActionResult<NotificationsAdminDto> Put([FromBody] NotificationsAdminBody body)
    {
        var plugin = Plugin.Instance!;
        var address = body?.WebhookAddress?.Trim().TrimEnd('/');
        if (!string.IsNullOrEmpty(address) && !(Uri.TryCreate(address, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https"))
        {
            return BadRequest("The address must start with http:// or https://.");
        }

        plugin.Configuration.Notifications = new NotificationSettings
        {
            Enabled = body?.Enabled ?? false,
            WebhookAddress = address is null ? plugin.Configuration.Notifications.WebhookAddress : address.Length == 0 ? null : address
        };
        plugin.SaveConfiguration();
        if (plugin.Configuration.Notifications.Enabled && !SecretVault.Current.Has(NotificationsController.WebhookSecretName))
        {
            SecretVault.Current.Set(NotificationsController.WebhookSecretName, NotificationsController.NewSecret());
        }

        SeerrWatchService.Wake();
        return NotificationsAdminDto.Current();
    }

    /// <summary>
    /// A test notification for one person, which their Arcfin devices show on their next check:
    /// when Arcfin opens, or when the system lets it run in the background.
    /// </summary>
    [HttpPost("Test")]
    public ActionResult<NotificationsAdminDto> SendTest([FromBody] NotificationTestBody body, [FromServices] IUserManager users)
    {
        if (!Plugin.Instance!.Configuration.Notifications.Enabled)
        {
            return BadRequest("Turn notifications on first.");
        }

        if (body?.UserId is not { } id || users.GetUserById(id) is null)
        {
            return BadRequest("Pick someone to send it to.");
        }

        EventStore.Current.Add(new[] { new ServerEvent { Id = $"test.{Guid.NewGuid():N}", Kind = "test", UserId = id } }, DateTime.UtcNow);
        return NotificationsAdminDto.Current();
    }

    /// <summary>A new webhook secret; Seerr's webhook settings need the new one pasted in.</summary>
    [HttpPost("Secret/Reset")]
    public ActionResult<NotificationsAdminDto> ResetSecret()
    {
        SecretVault.Current.Set(NotificationsController.WebhookSecretName, NotificationsController.NewSecret());
        return NotificationsAdminDto.Current();
    }
}

public sealed class EventsDto
{
    public long Cursor { get; set; }

    public List<EventDto> Events { get; set; } = new();
}

public sealed class EventDto
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Body { get; set; }

    public int? RequestId { get; set; }

    public int? IssueId { get; set; }

    /// <summary>Seerr's issue type: 1 video, 2 audio, 3 subtitles, 4 other.</summary>
    public int? IssueType { get; set; }

    public int? ProblemSeason { get; set; }

    public int? ProblemEpisode { get; set; }

    /// <summary>Seerr's display name for whoever reported, commented, resolved or reopened.</summary>
    public string? Actor { get; set; }

    public DateTime? Starts { get; set; }

    public DateTime? Ends { get; set; }

    public static EventDto From(ServerEvent e) => new()
    {
        Id = e.Id, Kind = e.Kind, Title = e.Title, Body = e.Body, RequestId = e.RequestId, IssueId = e.IssueId, IssueType = e.IssueType,
        ProblemSeason = e.ProblemSeason, ProblemEpisode = e.ProblemEpisode, Actor = e.Actor, Starts = e.Starts, Ends = e.Ends
    };
}

public sealed class NotificationTestBody
{
    public Guid? UserId { get; set; }
}

public sealed class NotificationsAdminBody
{
    public bool Enabled { get; set; }

    public string? WebhookAddress { get; set; }
}

public sealed class NotificationsAdminDto
{
    public bool Enabled { get; set; }

    /// <summary>The saved address for Seerr to reach this server on; null for the dashboard's own.</summary>
    public string? WebhookAddress { get; set; }

    /// <summary>Path of the webhook on this server, for Seerr's Webhook URL.</summary>
    public string WebhookPath { get; set; } = "/ArcfinSync/Notifications/Seerr";

    /// <summary>For Seerr's Authorization Header field. Only admins see it.</summary>
    public string? WebhookSecret { get; set; }

    public bool SeerrConfigured { get; set; }

    /// <summary>For Seerr's JSON Payload field.</summary>
    public string SeerrPayload { get; set; } = NotificationsController.SeerrPayload;

    /// <summary>The last call from Seerr's webhook, or null before the first.</summary>
    public WebhookCall? LastWebhook { get; set; }

    public static NotificationsAdminDto Current() => new()
    {
        Enabled = Plugin.Instance!.Configuration.Notifications.Enabled,
        WebhookAddress = Plugin.Instance.Configuration.Notifications.WebhookAddress,
        WebhookSecret = SecretVault.Current.Get(NotificationsController.WebhookSecretName),
        SeerrConfigured = Integrations.SeerrClient.IsConfigured,
        LastWebhook = WebhookLog.Current.Last
    };
}
