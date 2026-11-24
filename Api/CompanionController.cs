using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ArcfinSync.Api.Hidden;
using Jellyfin.Plugin.ArcfinSync.Api.Limits;
using Jellyfin.Plugin.ArcfinSync.Api.Security;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ArcfinSync.Api;

/// <summary>
/// The signed-in user's own: the server's signing key and the commands for them, what they've
/// hidden from Continue Watching and Next Up, and their watch time.
/// </summary>
[ApiController]
[Route("ArcfinSync")]
[Authorize]
public class CompanionController : ControllerBase
{
    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userData;

    private readonly MediaBrowser.Controller.IServerApplicationHost _host;
    private readonly MediaBrowser.Controller.Session.ISessionManager _sessions;
    private readonly MediaBrowser.Controller.Devices.IDeviceManager _devices;

    public CompanionController(IAuthorizationContext authContext, IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userData,
                               MediaBrowser.Controller.IServerApplicationHost host, MediaBrowser.Controller.Session.ISessionManager sessions,
                               MediaBrowser.Controller.Devices.IDeviceManager devices)
    {
        _host = host;
        _sessions = sessions;
        _devices = devices;
        _authContext = authContext;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userData = userData;
    }

    /// <summary>
    /// The public key admin commands are signed with, made the first time anyone asks. With
    /// <paramref name="from"/> (the fingerprint the device holds), also every key since, each
    /// signed by the one before, so a device that missed rotations can follow them.
    /// </summary>
    [HttpGet("Security/Key")]
    public ActionResult<KeyResponse> GetKey([FromQuery] string? from)
    {
        if (!SecurityOn)
        {
            return NotFound();
        }

        var signer = CommandSigner.Current;
        var key = signer.EnsureKey();
        return new KeyResponse
        {
            PublicKey = key.PublicKey,
            Fingerprint = key.Fingerprint,
            Created = key.Created,
            PreviousPublicKey = key.PreviousPublicKey,
            RotationSignature = key.RotationSignature,
            ResetAt = key.ResetAt,
            TrustOverHttp = Plugin.Instance!.Configuration.Security.TrustOverHttp,
            Chain = signer.ChainFrom(from)
        };
    }

    /// <summary>
    /// Someone on a device compared the key it got with the fingerprint they were shown and it
    /// didn't match: the device may be talking to something pretending to be this server.
    /// </summary>
    [HttpPost("Security/Mismatch")]
    public async Task<ActionResult> ReportMismatch([FromBody] CheckInBody body)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null || string.IsNullOrEmpty(auth.DeviceId))
        {
            return Unauthorized();
        }

        var seen = (body?.Fingerprint ?? string.Empty).Trim();
        var name = auth.Device ?? string.Empty;
        DeviceKeyStore.Current.ReportMismatch(user.Id, auth.DeviceId, name.Length > 60 ? name[..60] : name, seen.Length > 64 ? seen[..64] : seen, DateTime.UtcNow);
        return NoContent();
    }

    /// <summary>The device says which key it holds now; holding the current one keeps it from being signed out as behind.</summary>
    [HttpPost("Security/CheckIn")]
    public async Task<ActionResult<TrustStateDto>> CheckIn([FromBody] CheckInBody body)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null || string.IsNullOrEmpty(auth.DeviceId))
        {
            return Unauthorized();
        }

        if (!SecurityOn)
        {
            return new TrustStateDto();
        }

        var current = CommandSigner.Current.PublicKey;
        var fingerprint = (body?.Fingerprint ?? string.Empty).Trim();
        if (fingerprint.Length is 0 or > 64)
        {
            return BadRequest("No fingerprint.");
        }

        var name = auth.Device ?? string.Empty;
        var now = DateTime.UtcNow;
        var deviceName = name.Length > 60 ? name[..60] : name;
        var existing = DeviceKeyStore.Current.All().FirstOrDefault(r => r.UserId == user.Id && r.DeviceId == auth.DeviceId);
        DeviceKeyStore.Current.CheckIn(user.Id, auth.DeviceId, deviceName, fingerprint,
                                       current is not null && current.Fingerprint == fingerprint, now,
                                       Short(body!.AppVersion ?? auth.Version), Short(body.Build));
        OlderAppStore.Current.Forget(user.Id, auth.DeviceId);
        return Trust(user.Id, auth.DeviceId, deviceName, existing?.LastCheckIn, now);
    }

    /// <summary>
    /// With trusted devices on: a device already known when it was turned on, or the person's
    /// first, is trusted by itself; any other waits, locked, with a code to check on a trusted one.
    /// </summary>
    private TrustStateDto Trust(Guid userId, string deviceId, string deviceName, DateTime? knownSince, DateTime now)
    {
        var settings = Plugin.Instance!.Configuration.Security;
        if (!TrustStore.Required(settings, userId))
        {
            return new TrustStateDto { Trusted = true };
        }

        var signer = CommandSigner.Current;
        var request = TrustStore.Current.Change(file =>
        {
            if (file.Trusted.Any(t => t.UserId == userId && t.DeviceId == deviceId))
            {
                return null;
            }

            var since = settings.TrustedDevicesSince ?? now;
            if (!file.Trusted.Any(t => t.UserId == userId) || (knownSince is { } known && known < since))
            {
                file.Trusted.Add(new TrustedDevice { UserId = userId, DeviceId = deviceId, Trusted = now, By = "automatic" });
                return null;
            }

            return TrustStore.RequestFor(file, userId, deviceId, deviceName, now);
        });

        if (request is null)
        {
            return new TrustStateDto { Trusted = true };
        }

        if (!signer.IsLocked(userId, deviceId, now))
        {
            signer.Issue(CommandKind.Lock, userId, _host.SystemId, now, deviceId, new Dictionary<string, string> { ["reason"] = "trust" });
        }

        return new TrustStateDto { Trusted = false, Code = request.Code };
    }

    /// <summary>Devices of the caller's waiting to be trusted, for one of their trusted devices to approve.</summary>
    [HttpGet("Security/TrustRequests")]
    public async Task<ActionResult<List<TrustRequestDto>>> GetTrustRequests()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null || string.IsNullOrEmpty(auth.DeviceId))
        {
            return Unauthorized();
        }

        var store = TrustStore.Current;
        if (!SecurityOn || !store.IsTrusted(user.Id, auth.DeviceId))
        {
            return new List<TrustRequestDto>();
        }

        return store.Load().Requests.Where(r => r.UserId == user.Id && r.DeviceId != auth.DeviceId)
            .Select(r => new TrustRequestDto { Id = r.Id, DeviceName = r.DeviceName, Code = r.Code, Created = r.Created }).ToList();
    }

    /// <summary>One of the person's trusted devices trusts the waiting one, or doesn't (which signs it out).</summary>
    [HttpPost("Security/TrustRequests/{id}")]
    public async Task<ActionResult> AnswerTrustRequest([FromRoute] Guid id, [FromBody] TrustAnswerBody body)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null || string.IsNullOrEmpty(auth.DeviceId) || !SecurityOn || !TrustStore.Current.IsTrusted(user.Id, auth.DeviceId))
        {
            return StatusCode(403, "Only one of your trusted devices can do that.");
        }

        var request = TrustStore.Current.Load().Requests.FirstOrDefault(r => r.Id == id && r.UserId == user.Id);
        if (request is null)
        {
            return NotFound("That device was already answered.");
        }

        await TrustAnswer.ApplyAsync(request, body?.Trust == true, auth.Device ?? "a trusted device", _host.SystemId, _sessions, _devices).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>A device has carried out its wipe: it's now signed out.</summary>
    [HttpPost("Security/Wiped")]
    public async Task<ActionResult> Wiped()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (!auth.IsAuthenticated || string.IsNullOrEmpty(auth.DeviceId))
        {
            return Unauthorized();
        }

        TrustStore.Current.Change(file => file.Wipes.RemoveAll(w => w.UserId == auth.UserId && w.DeviceId == auth.DeviceId));
        if (!string.IsNullOrEmpty(auth.Token))
        {
            await _sessions.Logout(auth.Token).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>Too many wrong PINs on this device under the admin's rules: Arcfin there stays locked until an admin unlocks it.</summary>
    [HttpPost("Security/PinLockout")]
    public async Task<ActionResult> PinLockout()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null || string.IsNullOrEmpty(auth.DeviceId) || !SecurityOn)
        {
            return Unauthorized();
        }

        CommandSigner.Current.Issue(CommandKind.Lock, user.Id, _host.SystemId, DateTime.UtcNow, auth.DeviceId,
                                    new Dictionary<string, string> { ["reason"] = "pinTries" });
        return NoContent();
    }

    [HttpGet("Security/Commands")]
    public async Task<ActionResult<List<SignedCommand>>> GetCommands()
    {
        var user = await CallerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        if (!SecurityOn)
        {
            return new List<SignedCommand>();
        }

        // The person's rules, signed again whenever the admin changed them.
        var signer = CommandSigner.Current;
        var now = DateTime.UtcNow;
        var policy = UserPolicy.For(Plugin.Instance!.Configuration.Security, user.Id);
        var current = signer.CommandsFor(user.Id, now).LastOrDefault(c => c.Kind == CommandKind.Policy);
        if (!UserPolicy.Same(current?.Data, policy))
        {
            signer.Issue(CommandKind.Policy, user.Id, _host.SystemId, now, data: policy);
        }

        return signer.CommandsFor(user.Id, now);
    }

    [HttpGet("Hidden")]
    public async Task<ActionResult<HiddenLists>> GetHidden()
    {
        var user = await CallerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        if (!HiddenOn)
        {
            return new HiddenLists();
        }

        return HiddenStore.Current.DropReplayed(user.Id, id =>
        {
            var item = _libraryManager.GetItemById(id);
            return item is null || item.IsFolder ? null : _userData.GetUserData(user, item)?.LastPlayedDate;
        });
    }

    [HttpPut("Hidden/{row}/{itemId}")]
    public async Task<ActionResult<HiddenLists>> Hide([FromRoute] string row, [FromRoute] Guid itemId)
    {
        var user = await CallerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        if (!HiddenOn)
        {
            return StatusCode(403, "Your server's admin hasn't turned on hiding titles.");
        }

        if (!HiddenStore.IsRow(row))
        {
            return BadRequest("Row is continueWatching or nextUp.");
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null || !item.IsVisible(user))
        {
            return NotFound();
        }

        return HiddenStore.Current.Hide(user.Id, row, itemId, DateTime.UtcNow);
    }

    [HttpDelete("Hidden/{row}/{itemId}")]
    public async Task<ActionResult<HiddenLists>> Unhide([FromRoute] string row, [FromRoute] Guid itemId)
    {
        var user = await CallerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        return HiddenStore.IsRow(row) ? HiddenStore.Current.Unhide(user.Id, row, itemId) : BadRequest("Row is continueWatching or nextUp.");
    }

    /// <summary>The caller's watch time today and whether they can watch now.</summary>
    [HttpGet("Limits/Me")]
    public async Task<ActionResult<LimitStatusDto>> GetMyLimits()
    {
        var user = await CallerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        return LimitStatusDto.For(user.Id);
    }

    private static string? Short(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, 32)];

    private static bool SecurityOn => Plugin.Instance?.Configuration.Security.Enabled == true;

    private static bool HiddenOn => Plugin.Instance?.Configuration.HiddenEnabled == true;

    private async Task<User?> CallerAsync()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
    }
}

public sealed class KeyResponse
{
    public string PublicKey { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public DateTime Created { get; set; }

    public string? PreviousPublicKey { get; set; }

    public string? RotationSignature { get; set; }

    /// <summary>When an admin replaced the key completely; devices can't follow it from the old one.</summary>
    public DateTime? ResetAt { get; set; }

    /// <summary>The admin lets devices on plain HTTP trust this key without a fingerprint check.</summary>
    public bool TrustOverHttp { get; set; }

    /// <summary>The keys after the one asked about, oldest first, ending with this one.</summary>
    public List<PublicKeyInfo> Chain { get; set; } = new();
}

public sealed class TrustStateDto
{
    public bool Trusted { get; set; } = true;

    /// <summary>While waiting: the code to check on the trusted device.</summary>
    public string? Code { get; set; }
}

public sealed class TrustRequestDto
{
    public Guid Id { get; set; }

    public string DeviceName { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public DateTime Created { get; set; }
}

public sealed class TrustAnswerBody
{
    public bool Trust { get; set; }
}

public sealed class CheckInBody
{
    public string Fingerprint { get; set; } = string.Empty;

    public string? AppVersion { get; set; }

    public string? Build { get; set; }
}

public sealed class LimitStatusDto
{
    public bool HasLimits { get; set; }

    public bool Allowed { get; set; } = true;

    /// <summary>"bedtime" or "dailyLimit" when not allowed.</summary>
    public string? Reason { get; set; }

    public int? MinutesLeft { get; set; }

    /// <summary>When watching is allowed again, in UTC.</summary>
    public DateTime? Until { get; set; }

    public int UsedMinutes { get; set; }

    public int? DailyMinutes { get; set; }

    public string? BedtimeStart { get; set; }

    public string? BedtimeEnd { get; set; }

    public static LimitStatusDto For(Guid userId)
    {
        var rule = WatchLimitService.RuleFor(userId);
        if (rule is null)
        {
            return new LimitStatusDto();
        }

        var now = WatchLimitService.LocalNow(rule);
        var used = UsageStore.Current.Used(userId, now.Date);
        var state = WatchLimits.Evaluate(rule, used, now);
        return new LimitStatusDto
        {
            HasLimits = true,
            Allowed = state.Allowed,
            Reason = state.Reason,
            MinutesLeft = state.MinutesLeft,
            Until = state.Until is { } until ? DateTime.UtcNow + (until - now) : null,
            UsedMinutes = (int)Math.Floor(used),
            DailyMinutes = WatchLimits.Allowance(rule, now) is > 0 and var a ? a : null,
            BedtimeStart = rule.BedtimeStart,
            BedtimeEnd = rule.BedtimeEnd
        };
    }
}
