using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArcfinSync.Api.Integrations;
using Jellyfin.Plugin.ArcfinSync.Api.Limits;
using Jellyfin.Plugin.ArcfinSync.Api.Security;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api;

/// <summary>
/// Server administrators only: Seerr and TMDB keys, the signing key and commands, watch-time
/// limits, and who may use watch parties and recommendations. No endpoint here returns a key.
/// </summary>
[ApiController]
[Route("ArcfinSync/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
public class CompanionAdminController : ControllerBase
{
    private readonly IUserManager _userManager;
    private readonly IServerApplicationHost _host;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<CompanionAdminController> _logger;

    private readonly ISessionManager _sessions;
    private readonly IAuthorizationContext _authContext;
    private readonly MediaBrowser.Controller.Devices.IDeviceManager _devices;

    public CompanionAdminController(IUserManager userManager, IServerApplicationHost host, IHttpClientFactory http, ILogger<CompanionAdminController> logger,
                                    ISessionManager sessions, IAuthorizationContext authContext, MediaBrowser.Controller.Devices.IDeviceManager devices)
    {
        _devices = devices;
        _sessions = sessions;
        _authContext = authContext;
        _userManager = userManager;
        _host = host;
        _http = http;
        _logger = logger;
    }

    // MARK: Integrations

    [HttpGet("Integrations")]
    public ActionResult<IntegrationsAdminDto> GetIntegrations() => IntegrationsAdminDto.Current();

    /// <summary>A null key keeps the saved one; an empty key removes it.</summary>
    [HttpPut("Integrations")]
    public ActionResult<IntegrationsAdminDto> PutIntegrations([FromBody] IntegrationsAdminBody body)
    {
        if (body is null)
        {
            return BadRequest("No settings.");
        }

        var url = CleanUrl(body.SeerrUrl);
        var publicUrl = CleanUrl(body.SeerrPublicUrl);
        if (url == string.Empty || publicUrl == string.Empty)
        {
            return BadRequest("Seerr's addresses must start with http:// or https://.");
        }

        var plugin = Plugin.Instance!;
        plugin.Configuration.Integrations = new IntegrationSettings { SeerrUrl = url, SeerrPublicUrl = publicUrl, AddSeerrUsers = body.AddSeerrUsers };
        plugin.SaveConfiguration();
        var vault = SecretVault.Current;
        if (body.SeerrApiKey is not null)
        {
            vault.Set(SeerrClient.ApiKeyName, body.SeerrApiKey.Trim());
        }

        if (body.TmdbKey is not null)
        {
            vault.Set(SeerrClient.TmdbKeyName, body.TmdbKey.Trim());
        }

        SeerrClient.ForgetUsers();
        _logger.LogInformation("Arcfin Sync integrations changed (Seerr key {Seerr}, TMDB key {Tmdb})",
                               body.SeerrApiKey is null ? "kept" : "replaced", body.TmdbKey is null ? "kept" : "replaced");
        return IntegrationsAdminDto.Current();
    }

    /// <summary>Null for none, empty for not a web address, otherwise the address without a trailing slash.</summary>
    private static string? CleanUrl(string? raw)
    {
        var url = raw?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" ? url : string.Empty;
    }

    /// <summary>
    /// Checks Seerr with the address and key given, or the saved ones where they're left out, so
    /// they can be tried before saving. Nothing is saved.
    /// </summary>
    [HttpPost("Integrations/Test")]
    public async Task<ActionResult<IntegrationsAdminDto>> TestIntegrations([FromBody] IntegrationsTestBody? body)
    {
        var url = CleanUrl(body?.SeerrUrl) ?? SeerrClient.BaseUrl;
        if (url == string.Empty)
        {
            return BadRequest("Seerr's addresses must start with http:// or https://.");
        }

        var key = string.IsNullOrWhiteSpace(body?.SeerrApiKey) ? SecretVault.Current.Get(SeerrClient.ApiKeyName) : body.SeerrApiKey.Trim();
        if (!string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(key))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await new SeerrClient(_http).TestAsync(url, key, timeout.Token).ConfigureAwait(false);
        }

        return IntegrationsAdminDto.Current();
    }

    [HttpPost("Integrations/Queue/Send")]
    public async Task<ActionResult<IntegrationsAdminDto>> SendQueue([FromServices] ILogger<SeerrQueueService> logger)
    {
        await new SeerrQueueService(_http, logger).SendQueuedAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        return IntegrationsAdminDto.Current();
    }

    [HttpDelete("Integrations/Queue/{id}")]
    public ActionResult<IntegrationsAdminDto> DropQueued([FromRoute] Guid id)
    {
        SeerrQueue.Current.Update(list => list.Where(e => e.Id != id).ToList());
        return IntegrationsAdminDto.Current();
    }

    // MARK: Security

    [HttpGet("Security")]
    public ActionResult<SecurityAdminDto> GetSecurity()
    {
        if (Plugin.Instance!.Configuration.Security.Enabled)
        {
            CommandSigner.Current.EnsureKey();
        }

        return SecurityDto();
    }

    [HttpPost("Security/Key")]
    public ActionResult<SecurityAdminDto> MakeKey([FromBody] KeyBody? body)
    {
        var signer = CommandSigner.Current;
        try
        {
            if (string.IsNullOrWhiteSpace(body?.Pem))
            {
                signer.Generate();
            }
            else
            {
                signer.Import(body.Pem);
                // The plugin can't make the next key in a chain from the admin's own, so rotating stops.
                var plugin = Plugin.Instance!;
                if (plugin.Configuration.Security.AutoRotate)
                {
                    plugin.Configuration.Security.AutoRotate = false;
                    plugin.SaveConfiguration();
                }
            }
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }

        _logger.LogInformation("Arcfin Sync signing key {How}", string.IsNullOrWhiteSpace(body?.Pem) ? "generated" : "imported");
        return SecurityDto();
    }

    /// <summary>Replaces the key with one the old key doesn't vouch for. Destructive: every device has to trust the new key again.</summary>
    [HttpPost("Security/Key/Reset")]
    public ActionResult<SecurityAdminDto> ResetKey()
    {
        var key = CommandSigner.Current.Reset();
        _logger.LogWarning("Arcfin Sync signing key replaced completely; the new fingerprint is {Fingerprint}", key.Fingerprint);
        return SecurityDto();
    }

    [HttpPost("Security/Commands")]
    public async Task<ActionResult<SecurityAdminDto>> SendCommand([FromBody] CommandBody body)
    {
        var target = body is null ? null : _userManager.GetUserById(body.UserId);
        if (body is null || !CommandKind.IsKnown(body.Kind) || target is null)
        {
            return BadRequest("Pick a user and resetPin, lock or unlock.");
        }

        var now = DateTime.UtcNow;
        if (body.Kind == CommandKind.Lock && body.DisableAccount)
        {
            var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
            if (target.Id == auth.UserId)
            {
                return BadRequest("You can't disable your own account.");
            }

            var policy = _userManager.GetUserDto(target, null).Policy;
            if (policy.IsAdministrator && _userManager.GetUsers().Count(u => u.Id != target.Id && IsEnabledAdmin(u)) == 0)
            {
                return BadRequest("That's the only other administrator; disabling them would leave the server without one.");
            }
        }

        if (CommandSigner.Current.Issue(body.Kind, body.UserId, _host.SystemId, now) is null)
        {
            return BadRequest("Make or add a signing key first.");
        }

        if (body.Kind == CommandKind.Lock && body.DisableAccount)
        {
            await SetDisabledAsync(target, true).ConfigureAwait(false);
            DisabledByArcfin.Change(set => set.Add(target.Id));
            // A disabled account can't sign in again; signing it out everywhere ends what's open now.
            await _sessions.RevokeUserTokens(target.Id, null).ConfigureAwait(false);
        }
        else if (body.Kind == CommandKind.Unlock && DisabledByArcfin.Load().Contains(target.Id))
        {
            await SetDisabledAsync(target, false).ConfigureAwait(false);
            DisabledByArcfin.Change(set => set.Remove(target.Id));
        }

        _logger.LogInformation("Arcfin Sync command {Kind} for user {User}{Disabled}", body.Kind, body.UserId,
                               body.Kind == CommandKind.Lock && body.DisableAccount ? ", Jellyfin account disabled" : string.Empty);
        return SecurityDto();
    }

    /// <summary>Automatic rotation, and the stale-device rule with its per-user overrides.</summary>
    [HttpPut("Security/Settings")]
    public ActionResult<SecurityAdminDto> PutSecuritySettings([FromBody] SecuritySettings body)
    {
        if (body is null || body.RotateEveryDays is < 1 or > 365 || body.StaleAfterDays is < 1 or > 3650
            || !StaleActions.IsKnown(body.StaleAction, false)
            || body.Users.Any(u => !StaleActions.IsKnown(u.StaleAction, true) || u.StaleAfterDays is < 1 or > 3650))
        {
            return BadRequest("Rotate every 1 to 365 days; devices may fall behind for 1 to 3650 days; the rule is off, signOut or lock.");
        }

        var plugin = Plugin.Instance!;
        var old = plugin.Configuration.Security;
        if (body.AutoRotate && CommandSigner.Current.PublicKey?.Imported == true)
        {
            return BadRequest("Your own key is in use, and the plugin can't rotate it. Make a key here first, then turn on rotation.");
        }

        if (body.OlderApps is not (OlderAppActions.Allow or OlderAppActions.SignOut))
        {
            return BadRequest("Older Arcfin versions are allow or signOut.");
        }

        var pins = body.Pins ?? new PinRules();
        if (pins.RequiredFor is not ("none" or "everyone" or "chosen") || pins.MinLength is < 4 or > PinRules.LongestPin || pins.MaxTries is < 3 or > 10
            || body.Users.Any(u => u.TrustedDevices is not ("inherit" or "on" or "off")))
        {
            return BadRequest("PINs are 4 to 8 digits, with 3 to 10 tries; PINs are needed by none, everyone or chosen people; trusted devices per person are inherit, on or off.");
        }

        var turnedOn = old.StaleAction == StaleActions.Off && body.StaleAction != StaleActions.Off;
        var olderOn = old.OlderApps != OlderAppActions.SignOut && body.OlderApps == OlderAppActions.SignOut;
        plugin.Configuration.Security = new SecuritySettings
        {
            Enabled = body.Enabled,
            TrustOverHttp = body.TrustOverHttp,
            AutoRotate = body.AutoRotate,
            RotateEveryDays = body.RotateEveryDays,
            StaleAction = body.StaleAction,
            StaleAfterDays = body.StaleAfterDays,
            StaleSince = turnedOn || old.StaleSince is null ? DateTime.UtcNow : old.StaleSince,
            OlderApps = body.OlderApps,
            RequireTrustedDevices = body.RequireTrustedDevices,
            TrustedDevicesSince = body.RequireTrustedDevices && !old.RequireTrustedDevices ? DateTime.UtcNow : old.TrustedDevicesSince,
            RequireHttpsAway = body.RequireHttpsAway,
            Pins = new PinRules
            {
                RequiredFor = pins.RequiredFor,
                Users = pins.Users.Where(u => _userManager.GetUserById(u) is not null).Distinct().ToList(),
                MinLength = pins.MinLength,
                MaxTries = pins.MaxTries
            },
            OlderAppsSince = olderOn || old.OlderAppsSince is null ? DateTime.UtcNow : old.OlderAppsSince,
            Users = body.Users.Where(u => _userManager.GetUserById(u.UserId) is not null
                                          && (u.StaleAction != StaleActions.Inherit || u.StaleAfterDays is not null || u.TrustedDevices != StaleActions.Inherit))
                .GroupBy(u => u.UserId).Select(g => g.Last()).ToList()
        };
        plugin.SaveConfiguration();
        return SecurityDto();
    }

    /// <summary>
    /// Signs out one Arcfin sign-in too old for the security features (an old install's
    /// leftover sign-in, often), or every one of a user's when no device is given.
    /// </summary>
    [HttpPost("Security/Devices/SignOut")]
    public async Task<ActionResult<SecurityAdminDto>> SignOutOlder([FromBody] DeviceRef body)
    {
        if (body is null || _userManager.GetUserById(body.UserId) is null)
        {
            return BadRequest("Pick a user.");
        }

        var older = OlderAppStore.Current;
        foreach (var device in SecurityService.OlderArcfinDevices(_devices, DeviceKeyStore.Current)
                     .Where(d => d.LastUserId == body.UserId && (string.IsNullOrEmpty(body.DeviceId) || d.Id == body.DeviceId)))
        {
            if (device.AccessToken is { } token)
            {
                await _sessions.Logout(token).ConfigureAwait(false);
            }

            older.Forget(body.UserId, device.Id!);
        }

        _logger.LogInformation("An admin signed out older Arcfin sign-ins of user {User}", body.UserId);
        return SecurityDto();
    }

    /// <summary>The admin trusts a waiting device, or doesn't (which signs it out).</summary>
    [HttpPost("Security/TrustRequests/{id}")]
    public async Task<ActionResult<SecurityAdminDto>> AnswerTrust([FromRoute] Guid id, [FromBody] TrustAnswerBody body)
    {
        var request = TrustStore.Current.Load().Requests.FirstOrDefault(r => r.Id == id);
        if (request is null)
        {
            return NotFound("That device was already answered.");
        }

        await TrustAnswer.ApplyAsync(request, body?.Trust == true, "admin", _host.SystemId, _sessions, _devices).ConfigureAwait(false);
        return SecurityDto();
    }

    /// <summary>
    /// Wipes a lost device: its downloads are deleted, Arcfin is locked on it, and it's signed
    /// out once it has done it (or after a week if it never comes back online).
    /// </summary>
    [HttpPost("Security/Devices/Wipe")]
    public ActionResult<SecurityAdminDto> WipeDevice([FromBody] DeviceRef body)
    {
        if (body is null || _userManager.GetUserById(body.UserId) is null || string.IsNullOrEmpty(body.DeviceId))
        {
            return BadRequest("Pick a device.");
        }

        if (!Plugin.Instance!.Configuration.Security.Enabled)
        {
            return BadRequest("Turn on signed commands in Security first.");
        }

        var now = DateTime.UtcNow;
        var signer = CommandSigner.Current;
        signer.Issue(CommandKind.Wipe, body.UserId, _host.SystemId, now, body.DeviceId);
        signer.Issue(CommandKind.Lock, body.UserId, _host.SystemId, now, body.DeviceId, new Dictionary<string, string> { ["reason"] = "wipe" });
        TrustStore.Current.Change(file =>
        {
            file.Wipes.RemoveAll(w => w.UserId == body.UserId && w.DeviceId == body.DeviceId);
            file.Wipes.Add(new PendingWipe { UserId = body.UserId, DeviceId = body.DeviceId, Sent = now });
            file.Trusted.RemoveAll(t => t.UserId == body.UserId && t.DeviceId == body.DeviceId);
            return 0;
        });
        _logger.LogWarning("An admin wiped device {Device} of user {User}", body.DeviceId, body.UserId);
        return SecurityDto();
    }

    /// <summary>Lets a device the stale-device rule locked open Arcfin again.</summary>
    [HttpPost("Security/Devices/Unlock")]
    public ActionResult<SecurityAdminDto> UnlockDevice([FromBody] DeviceRef body)
    {
        if (body is null || _userManager.GetUserById(body.UserId) is null || string.IsNullOrEmpty(body.DeviceId))
        {
            return BadRequest("Pick a device.");
        }

        CommandSigner.Current.Issue(CommandKind.Unlock, body.UserId, _host.SystemId, DateTime.UtcNow, body.DeviceId);
        DeviceKeyStore.Current.Forget(body.UserId, body.DeviceId);
        return SecurityDto();
    }

    private bool IsEnabledAdmin(Jellyfin.Database.Implementations.Entities.User user)
    {
        var policy = _userManager.GetUserDto(user, null).Policy;
        return policy.IsAdministrator && !policy.IsDisabled;
    }

    private async Task SetDisabledAsync(Jellyfin.Database.Implementations.Entities.User user, bool disabled)
    {
        var policy = _userManager.GetUserDto(user, null).Policy;
        policy.IsDisabled = disabled;
        await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
    }

    // MARK: Limits

    [HttpGet("Limits")]
    public ActionResult<LimitsAdminDto> GetLimits() => LimitsDto();

    [HttpPut("Limits")]
    public ActionResult<LimitsAdminDto> PutLimits([FromBody] LimitSettings body)
    {
        if (body is null)
        {
            return BadRequest("No settings.");
        }

        var zone = string.IsNullOrWhiteSpace(body.TimeZone) ? null : body.TimeZone.Trim();
        if (zone is not null && WatchLimitService.Zone(zone) is null)
        {
            return BadRequest($"\"{zone}\" isn't a time zone this server knows. Use a name like Europe/Dublin.");
        }

        if ((body.Rules ?? new List<WatchLimitRule>()).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.TimeZone) && WatchLimitService.Zone(r.TimeZone) is null) is { } badZone)
        {
            return BadRequest($"\"{badZone.TimeZone}\" isn't a time zone this server knows. Use a name like Europe/Dublin.");
        }

        var rules = (body.Rules ?? new List<WatchLimitRule>())
            .Where(r => _userManager.GetUserById(r.UserId) is not null)
            .GroupBy(r => r.UserId).Select(g => g.Last())
            .Select(r => new WatchLimitRule
            {
                UserId = r.UserId,
                DailyMinutes = r.DailyMinutes,
                WeekendMinutes = r.WeekendMinutes,
                BedtimeStart = string.IsNullOrWhiteSpace(r.BedtimeStart) ? null : r.BedtimeStart,
                BedtimeEnd = string.IsNullOrWhiteSpace(r.BedtimeEnd) ? null : r.BedtimeEnd,
                TimeZone = string.IsNullOrWhiteSpace(r.TimeZone) ? null : r.TimeZone.Trim()
            }).ToList();
        if (rules.FirstOrDefault(r => !WatchLimits.IsValid(r)) is { } bad)
        {
            return BadRequest($"The limit for {_userManager.GetUserById(bad.UserId)?.Username} isn't valid: minutes are 0 to 1440, and a bedtime needs a start and an end (HH:mm).");
        }

        var plugin = Plugin.Instance!;
        plugin.Configuration.Limits = new LimitSettings { TimeZone = zone, Rules = rules, WarnBeforeEnd = body.WarnBeforeEnd };
        plugin.SaveConfiguration();
        return LimitsDto();
    }

    // MARK: Watch party and recommendations

    [HttpGet("WatchParty")]
    public ActionResult<PickTogetherSettings> GetWatchParty() => Plugin.Instance!.Configuration.WatchParty;

    [HttpPut("WatchParty")]
    public ActionResult<PickTogetherSettings> PutWatchParty([FromBody] PickTogetherSettings settings)
    {
        if (settings is null)
        {
            return BadRequest("No settings.");
        }

        var plugin = Plugin.Instance!;
        plugin.Configuration.WatchParty = new PickTogetherSettings
        {
            Enabled = settings.Enabled,
            DisabledUsers = (settings.DisabledUsers ?? new List<Guid>()).Distinct().Where(id => _userManager.GetUserById(id) is not null).ToList()
        };
        plugin.SaveConfiguration();
        return plugin.Configuration.WatchParty;
    }

    [HttpGet("Hidden")]
    public ActionResult<ToggleDto> GetHidden() => new ToggleDto { Enabled = Plugin.Instance!.Configuration.HiddenEnabled };

    [HttpPut("Hidden")]
    public ActionResult<ToggleDto> PutHidden([FromBody] ToggleDto body)
    {
        var plugin = Plugin.Instance!;
        plugin.Configuration.HiddenEnabled = body?.Enabled ?? false;
        plugin.SaveConfiguration();
        return new ToggleDto { Enabled = plugin.Configuration.HiddenEnabled };
    }

    [HttpGet("Recommendations")]
    public ActionResult<ToggleDto> GetRecommendations() => new ToggleDto { Enabled = Plugin.Instance!.Configuration.RecommendationsEnabled };

    [HttpPut("Recommendations")]
    public ActionResult<ToggleDto> PutRecommendations([FromBody] ToggleDto body)
    {
        var plugin = Plugin.Instance!;
        plugin.Configuration.RecommendationsEnabled = body?.Enabled ?? false;
        plugin.SaveConfiguration();
        return new ToggleDto { Enabled = plugin.Configuration.RecommendationsEnabled };
    }

    private SecurityAdminDto SecurityDto()
    {
        var signer = CommandSigner.Current;
        var now = DateTime.UtcNow;
        var settings = Plugin.Instance!.Configuration.Security;
        var key = signer.PublicKey;
        var disabled = DisabledByArcfin.Load();
        return new SecurityAdminDto
        {
            Key = key,
            VaultKeySource = SecretVault.Current.KeySource,
            Settings = settings,
            NextRotation = SecurityService.NextRotation(settings, key),
            LockedUsers = _userManager.GetUsers().Where(u => signer.IsLocked(u.Id, now)).Select(u => u.Id).ToList(),
            DisabledUsers = disabled.ToList(),
            TrustRequests = TrustStore.Current.Load().Requests.Select(r => new AdminTrustRequestDto { Id = r.Id, UserId = r.UserId, DeviceId = r.DeviceId, DeviceName = r.DeviceName, Code = r.Code, Created = r.Created }).ToList(),
            TrustedDevices = TrustStore.Current.Load().Trusted.Select(t => new DeviceRef { UserId = t.UserId, DeviceId = t.DeviceId }).ToList(),
            PendingWipes = TrustStore.Current.Load().Wipes.Select(w => new DeviceRef { UserId = w.UserId, DeviceId = w.DeviceId }).ToList(),
            Commands = _userManager.GetUsers().SelectMany(u => signer.CommandsFor(u.Id, now))
                .OrderByDescending(c => c.Counter).Take(50)
                .Select(c => new CommandSummary { UserId = c.UserId, Kind = c.Kind, Counter = c.Counter, Expires = c.Expires, DeviceId = c.DeviceId }).ToList(),
            OlderDevices = SecurityService.OlderArcfinDevices(_devices, DeviceKeyStore.Current).Select(d => new OlderDeviceDto
            {
                UserId = d.LastUserId ?? Guid.Empty,
                DeviceId = d.Id ?? string.Empty,
                DeviceName = d.CustomName ?? d.Name ?? string.Empty,
                AppVersion = d.AppVersion ?? string.Empty,
                LastActivity = d.DateLastActivity
            }).OrderByDescending(d => d.LastActivity).ToList(),
            Devices = DeviceKeyStore.Current.All().Select(r =>
            {
                var rule = StalePolicy.For(settings, r.UserId);
                var from = new[] { r.LastCurrent ?? r.LastCheckIn, settings.StaleSince ?? DateTime.MinValue }.Max();
                return new DeviceKeyDto
                {
                    UserId = r.UserId,
                    DeviceId = r.DeviceId,
                    DeviceName = r.DeviceName,
                    LastCheckIn = r.LastCheckIn,
                    UpToDate = key is not null && r.Fingerprint == key.Fingerprint,
                    AppVersion = r.AppVersion,
                    Build = r.Build,
                    MismatchAt = r.MismatchAt,
                    MismatchFingerprint = r.MismatchFingerprint,
                    SignedOut = r.SignedOut,
                    Action = r.Action,
                    Locked = signer.IsLocked(r.UserId, r.DeviceId, now),
                    LockReason = signer.CommandsFor(r.UserId, now).LastOrDefault(c => c.DeviceId == r.DeviceId && c.Kind == CommandKind.Lock)?.Data?.GetValueOrDefault("reason"),
                    Rule = rule.Action,
                    SignOutAfter = rule.Action == StaleActions.Off || r.SignedOut is not null ? null : from + TimeSpan.FromDays(rule.Days)
                };
            }).OrderBy(d => d.SignOutAfter ?? DateTime.MaxValue).ToList()
        };
    }

    private LimitsAdminDto LimitsDto()
    {
        var settings = Plugin.Instance!.Configuration.Limits;
        var now = WatchLimitService.LocalNow();
        return new LimitsAdminDto
        {
            TimeZone = settings.TimeZone,
            WarnBeforeEnd = settings.WarnBeforeEnd,
            ServerTime = now.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            ServerTimeZone = TimeZoneInfo.Local.Id,
            Rules = settings.Rules,
            UsedToday = settings.Rules.ToDictionary(r => r.UserId, r => (int)Math.Floor(UsageStore.Current.Used(r.UserId, WatchLimitService.LocalNow(r).Date)))
        };
    }
}

public sealed class IntegrationsAdminDto
{
    public string? SeerrUrl { get; set; }

    public string? SeerrPublicUrl { get; set; }

    public bool SeerrKeySet { get; set; }

    public bool TmdbKeySet { get; set; }

    public bool AddSeerrUsers { get; set; }

    /// <summary>ok, keyRejected, unreachable or error, from the last call to Seerr.</summary>
    public string? SeerrState { get; set; }

    public string? SeerrMessage { get; set; }

    public DateTime? SeerrChecked { get; set; }

    public string VaultKeySource { get; set; } = string.Empty;

    public List<QueuedSummary> Queue { get; set; } = new();

    public static IntegrationsAdminDto Current()
    {
        var settings = Plugin.Instance!.Configuration.Integrations;
        var vault = SecretVault.Current;
        var health = SeerrClient.Health;
        return new IntegrationsAdminDto
        {
            SeerrUrl = settings.SeerrUrl,
            SeerrPublicUrl = settings.SeerrPublicUrl,
            AddSeerrUsers = settings.AddSeerrUsers,
            SeerrKeySet = vault.Has(SeerrClient.ApiKeyName),
            TmdbKeySet = vault.Has(SeerrClient.TmdbKeyName),
            SeerrState = health.State,
            SeerrMessage = health.Message,
            SeerrChecked = health.Checked,
            VaultKeySource = vault.KeySource,
            Queue = SeerrQueue.Current.All().Select(q => new QueuedSummary { Id = q.Id, UserId = q.UserId, Created = q.Created, Attempts = q.Attempts, LastError = q.LastError }).ToList()
        };
    }
}

public sealed class QueuedSummary
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public DateTime Created { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

public sealed class IntegrationsAdminBody
{
    public string? SeerrUrl { get; set; }

    public string? SeerrPublicUrl { get; set; }

    public string? SeerrApiKey { get; set; }

    public string? TmdbKey { get; set; }

    public bool AddSeerrUsers { get; set; }
}

public sealed class IntegrationsTestBody
{
    public string? SeerrUrl { get; set; }

    public string? SeerrApiKey { get; set; }
}

public sealed class KeyBody
{
    public string? Pem { get; set; }
}

public sealed class CommandBody
{
    public Guid UserId { get; set; }

    public string Kind { get; set; } = string.Empty;

    /// <summary>With a lock: also disable the Jellyfin account, so it's locked out of the whole server.</summary>
    public bool DisableAccount { get; set; }
}

public sealed class AdminTrustRequestDto
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public DateTime Created { get; set; }
}

public sealed class OlderDeviceDto
{
    public Guid UserId { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string AppVersion { get; set; } = string.Empty;

    public DateTime? LastActivity { get; set; }
}

public sealed class DeviceRef
{
    public Guid UserId { get; set; }

    public string DeviceId { get; set; } = string.Empty;
}

public sealed class DeviceKeyDto
{
    public Guid UserId { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public DateTime LastCheckIn { get; set; }

    /// <summary>Holds the current key.</summary>
    public bool UpToDate { get; set; }

    public string? AppVersion { get; set; }

    public string? Build { get; set; }

    /// <summary>Someone on the device said the key didn't match: possibly something pretending to be this server.</summary>
    public DateTime? MismatchAt { get; set; }

    public string? MismatchFingerprint { get; set; }

    public DateTime? SignedOut { get; set; }

    public string? Action { get; set; }

    /// <summary>Arcfin is locked on this one device until an admin unlocks it.</summary>
    public bool Locked { get; set; }

    /// <summary>Why: trust (waiting for approval), pinTries, wipe, or none for falling behind.</summary>
    public string? LockReason { get; set; }

    /// <summary>The rule for this user: off, signOut or lock.</summary>
    public string Rule { get; set; } = string.Empty;

    /// <summary>When it'll be signed out if it doesn't pick up the current key.</summary>
    public DateTime? SignOutAfter { get; set; }
}

/// <summary>Accounts Arcfin Sync disabled with a lock, so an unlock only enables what it disabled.</summary>
internal static class DisabledByArcfin
{
    private static readonly object Gate = new();

    private static string Path => System.IO.Path.Combine(Plugin.Instance!.DataFolderPath, "disabled-accounts.json");

    public static HashSet<Guid> Load()
    {
        lock (Gate)
        {
            return Storage.JsonFile.Read<HashSet<Guid>>(Path) ?? new HashSet<Guid>();
        }
    }

    public static void Change(Func<HashSet<Guid>, bool> change)
    {
        lock (Gate)
        {
            var set = Storage.JsonFile.Read<HashSet<Guid>>(Path) ?? new HashSet<Guid>();
            change(set);
            Storage.JsonFile.Write(Path, set);
        }
    }
}

public sealed class SecurityAdminDto
{
    public PublicKeyInfo? Key { get; set; }

    public string VaultKeySource { get; set; } = string.Empty;

    public SecuritySettings Settings { get; set; } = new();

    public DateTime? NextRotation { get; set; }

    /// <summary>Accounts disabled in Jellyfin by an Arcfin Sync lock.</summary>
    public List<Guid> DisabledUsers { get; set; } = new();

    /// <summary>Arcfin devices that have checked in with their key, soonest to be signed out first.</summary>
    public List<DeviceKeyDto> Devices { get; set; } = new();

    /// <summary>Arcfin devices signed in that have never checked in: too old for key changes and locks.</summary>
    public List<OlderDeviceDto> OlderDevices { get; set; } = new();

    /// <summary>New devices waiting for a trusted device or an admin to approve them.</summary>
    public List<AdminTrustRequestDto> TrustRequests { get; set; } = new();

    public List<DeviceRef> TrustedDevices { get; set; } = new();

    /// <summary>Wipes sent that the device hasn't carried out yet.</summary>
    public List<DeviceRef> PendingWipes { get; set; } = new();

    public List<Guid> LockedUsers { get; set; } = new();

    public List<CommandSummary> Commands { get; set; } = new();
}

public sealed class CommandSummary
{
    public Guid UserId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public long Counter { get; set; }

    public DateTime Expires { get; set; }

    public string? DeviceId { get; set; }
}

public sealed class LimitsAdminDto
{
    public string? TimeZone { get; set; }

    public bool WarnBeforeEnd { get; set; } = true;

    public string ServerTime { get; set; } = string.Empty;

    public string ServerTimeZone { get; set; } = string.Empty;

    public List<WatchLimitRule> Rules { get; set; } = new();

    public Dictionary<Guid, int> UsedToday { get; set; } = new();
}

public sealed class ToggleDto
{
    public bool Enabled { get; set; }
}
