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
/// News for Arcfin to show as notifications: Seerr request changes, announcements and planned
/// maintenance. Arcfin asks when it opens and when the system lets it run in the background;
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
    /// dashboard. The body isn't read: a call only makes the server check Seerr now.
    /// </summary>
    [HttpPost("Seerr")]
    [AllowAnonymous]
    public ActionResult SeerrWebhook()
    {
        if (!Config.Notifications.Enabled)
        {
            return NotFound();
        }

        var secret = SecretVault.Current.Get(WebhookSecretName);
        if (string.IsNullOrEmpty(secret) || !Matches(Request.Headers.Authorization.ToString(), secret))
        {
            return Unauthorized();
        }

        SeerrWatchService.Wake();
        return NoContent();
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

    [HttpPut]
    public ActionResult<NotificationsAdminDto> Put([FromBody] ToggleDto body)
    {
        var plugin = Plugin.Instance!;
        plugin.Configuration.Notifications = new NotificationSettings { Enabled = body?.Enabled ?? false };
        plugin.SaveConfiguration();
        if (plugin.Configuration.Notifications.Enabled && !SecretVault.Current.Has(NotificationsController.WebhookSecretName))
        {
            SecretVault.Current.Set(NotificationsController.WebhookSecretName, NotificationsController.NewSecret());
        }

        SeerrWatchService.Wake();
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

    public DateTime? Starts { get; set; }

    public DateTime? Ends { get; set; }

    public static EventDto From(ServerEvent e) => new()
    {
        Id = e.Id, Kind = e.Kind, Title = e.Title, Body = e.Body, RequestId = e.RequestId, Starts = e.Starts, Ends = e.Ends
    };
}

public sealed class NotificationsAdminDto
{
    public bool Enabled { get; set; }

    /// <summary>Path of the webhook on this server, for Seerr's Webhook URL.</summary>
    public string WebhookPath { get; set; } = "/ArcfinSync/Notifications/Seerr";

    /// <summary>For Seerr's Authorization Header field. Only admins see it.</summary>
    public string? WebhookSecret { get; set; }

    public bool SeerrConfigured { get; set; }

    public static NotificationsAdminDto Current() => new()
    {
        Enabled = Plugin.Instance!.Configuration.Notifications.Enabled,
        WebhookSecret = SecretVault.Current.Get(NotificationsController.WebhookSecretName),
        SeerrConfigured = Integrations.SeerrClient.IsConfigured
    };
}
