using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ArcfinSync.Api.Household;

/// <summary>
/// For the signed-in person: download caps across their devices, who's watching on a shared
/// account, and the admin's announcements and planned maintenance.
/// </summary>
[ApiController]
[Route("ArcfinSync/Household")]
[Authorize]
public class HouseholdController : ControllerBase
{
    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;

    public HouseholdController(IAuthorizationContext authContext, IUserManager userManager)
    {
        _authContext = authContext;
        _userManager = userManager;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    [HttpGet("Downloads")]
    public async Task<ActionResult<DownloadAllowanceDto>> GetDownloads()
    {
        var (user, _) = await CallerAsync().ConfigureAwait(false);
        return user is null ? Unauthorized() : Allowance(user.Id);
    }

    /// <summary>This device's downloads: how many titles and how much space, after each change.</summary>
    [HttpPut("Downloads")]
    public async Task<ActionResult<DownloadAllowanceDto>> ReportDownloads([FromBody] DownloadReportBody body)
    {
        var (user, deviceId) = await CallerAsync().ConfigureAwait(false);
        if (user is null || string.IsNullOrEmpty(deviceId))
        {
            return Unauthorized();
        }

        DownloadInventoryStore.Current.Report(user.Id, deviceId, body?.Titles ?? new List<DownloadedTitle>(), DateTime.UtcNow, body?.Songs);
        return Allowance(user.Id);
    }

    /// <summary>Whether one more download of this size fits under the caps.</summary>
    [HttpPost("Downloads/Check")]
    public async Task<ActionResult<DownloadCheckDto>> CheckDownload([FromBody] DownloadCheckBody body)
    {
        var (user, _) = await CallerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var store = DownloadInventoryStore.Current;
        if (body?.Songs is { Count: > 0 } songs)
        {
            var musicFits = DownloadLimits.MusicFits(DownloadLimits.MusicFor(Config.Downloads, user.Id), store.Music(user.Id), songs);
            return new DownloadCheckDto
            {
                Allowed = musicFits,
                Message = musicFits ? null : "This music won't fit in the music downloads your server's admin allows across your devices. Remove some to make room."
            };
        }

        var caps = DownloadLimits.For(Config.Downloads, user.Id);
        var (titles, bytes) = store.Totals(user.Id);
        var existing = string.IsNullOrWhiteSpace(body?.ItemId) ? null : store.Existing(user.Id, body.ItemId);
        var fits = DownloadLimits.Fits(caps, titles, bytes, body?.Bytes ?? 0, existing);
        return new DownloadCheckDto
        {
            Allowed = fits,
            Message = fits ? null : caps!.MaxTitles is { } t && titles + 1 > t
                ? $"You can keep {t} downloads across your devices. Remove one to download this."
                : "This won't fit in the download space your server's admin set. Remove something to make room."
        };
    }

    [HttpGet("Shared")]
    public async Task<ActionResult<SharedAccountDto>> GetShared()
    {
        var (user, deviceId) = await CallerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var shared = Config.SharedAccounts.FirstOrDefault(a => a.UserId == user.Id);
        return new SharedAccountDto
        {
            IsShared = shared is not null,
            Members = (shared?.Members ?? new List<Guid>()).Select(_userManager.GetUserById).Where(u => u is not null)
                .Select(u => new PersonDto { Id = u!.Id, Name = u.Username }).ToList(),
            Watching = shared is null || deviceId is null ? new List<Guid>() : SharedWatchStore.Get(user.Id, deviceId, DateTime.UtcNow)
        };
    }

    /// <summary>On a shared account's device: who's watching, so their own progress follows along.</summary>
    [HttpPost("Shared/Watching")]
    public async Task<ActionResult<SharedAccountDto>> SetWatching([FromBody] WatchingBody body)
    {
        var (user, deviceId) = await CallerAsync().ConfigureAwait(false);
        if (user is null || string.IsNullOrEmpty(deviceId))
        {
            return Unauthorized();
        }

        var shared = Config.SharedAccounts.FirstOrDefault(a => a.UserId == user.Id);
        if (shared is null)
        {
            return StatusCode(403, "This account isn't set up as a shared one.");
        }

        var members = (body?.Members ?? new List<Guid>()).Where(shared.Members.Contains).Distinct().ToList();
        SharedWatchStore.Set(user.Id, deviceId, members, DateTime.UtcNow);
        return await GetShared().ConfigureAwait(false);
    }

    [HttpGet("Notices")]
    public async Task<ActionResult<NoticesDto>> GetNotices()
    {
        var (user, _) = await CallerAsync().ConfigureAwait(false);
        if (user is null)
        {
            return Unauthorized();
        }

        var now = DateTime.UtcNow;
        return new NoticesDto
        {
            Announcements = Notices.ActiveFor(Config.Announcements, user.Id, now),
            Maintenance = Notices.Upcoming(Config.Maintenance, now)
        };
    }

    private static DownloadAllowanceDto Allowance(Guid userId)
    {
        var caps = DownloadLimits.For(Config.Downloads, userId);
        var music = DownloadLimits.MusicFor(Config.Downloads, userId);
        var store = DownloadInventoryStore.Current;
        var (titles, bytes) = store.Totals(userId);
        var musicUse = store.Music(userId);
        return new DownloadAllowanceDto
        {
            Limited = caps is not null, MaxTitles = caps?.MaxTitles, MaxBytes = caps?.MaxBytes, UsedTitles = titles, UsedBytes = bytes,
            MusicLimited = music is not null, MusicMaxAlbums = music?.MaxAlbums, MusicMaxSongs = music?.MaxSongs, MusicMaxBytes = music?.MaxBytes,
            MusicUsedAlbums = musicUse.Albums, MusicUsedSongs = musicUse.Songs, MusicUsedBytes = musicUse.Bytes
        };
    }

    private async Task<(User? User, string? DeviceId)> CallerAsync()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        return (auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null, auth.DeviceId);
    }
}

/// <summary>Server administrators only: download caps, shared accounts, announcements and maintenance.</summary>
[ApiController]
[Route("ArcfinSync/Admin/Household")]
[Authorize(Policy = Policies.RequiresElevation)]
public class HouseholdAdminController : ControllerBase
{
    private readonly IUserManager _userManager;

    public HouseholdAdminController(IUserManager userManager)
    {
        _userManager = userManager;
    }

    [HttpGet]
    public ActionResult<HouseholdAdminDto> Get() => Dto();

    [HttpPut("Downloads")]
    public ActionResult<HouseholdAdminDto> PutDownloads([FromBody] DownloadLimitSettings body)
    {
        if (body is null || body.MaxTitles is < 1 or > 100000 || body.MaxGigabytes is < 0.1 or > 100000
            || body.MusicMaxAlbums is < 1 or > 100000 || body.MusicMaxSongs is < 1 or > 1000000 || body.MusicMaxGigabytes is < 0.1 or > 100000
            || body.Users.Any(u => u.Mode is not ("inherit" or "off" or "custom") || u.MusicMode is not ("inherit" or "off" or "custom")
                                   || u.MaxTitles is < 1 or > 100000 || u.MaxGigabytes is < 0.1 or > 100000
                                   || u.MusicMaxAlbums is < 1 or > 100000 || u.MusicMaxSongs is < 1 or > 1000000 || u.MusicMaxGigabytes is < 0.1 or > 100000))
        {
            return BadRequest("Caps are 1 to 100000 titles and 0.1 to 100000 GB; each person is inherit, off or custom.");
        }

        Save(c => c.Downloads = new DownloadLimitSettings
        {
            Enabled = body.Enabled,
            MaxTitles = body.MaxTitles,
            MaxGigabytes = body.MaxGigabytes,
            MusicEnabled = body.MusicEnabled,
            MusicMaxAlbums = body.MusicMaxAlbums,
            MusicMaxSongs = body.MusicMaxSongs,
            MusicMaxGigabytes = body.MusicMaxGigabytes,
            Users = body.Users.Where(u => _userManager.GetUserById(u.UserId) is not null && (u.Mode != "inherit" || u.MusicMode != "inherit"))
                .GroupBy(u => u.UserId).Select(g => g.Last()).ToList()
        });
        return Dto();
    }

    [HttpPut("SharedAccounts")]
    public ActionResult<HouseholdAdminDto> PutShared([FromBody] List<SharedAccount> body)
    {
        var accounts = (body ?? new List<SharedAccount>())
            .Where(a => _userManager.GetUserById(a.UserId) is not null)
            .GroupBy(a => a.UserId).Select(g => g.Last())
            .Select(a => new SharedAccount { UserId = a.UserId, Members = a.Members.Distinct().Where(m => m != a.UserId && _userManager.GetUserById(m) is not null).ToList() })
            .Where(a => a.Members.Count > 0).ToList();
        Save(c => c.SharedAccounts = accounts);
        return Dto();
    }

    [HttpPut("Announcements")]
    public ActionResult<HouseholdAdminDto> PutAnnouncements([FromBody] List<Announcement> body)
    {
        var list = body ?? new List<Announcement>();
        if (list.Any(a => string.IsNullOrWhiteSpace(a.Title) || a.Title.Length > 100 || (a.Message ?? string.Empty).Length > 1000
                          || a.Style is not ("info" or "warning") || (a.Starts is { } s && a.Ends is { } e && e <= s)))
        {
            return BadRequest("Each announcement needs a title (up to 100 characters), a message of up to 1000, info or warning, and an end after its start.");
        }

        Save(c => c.Announcements = list.Take(50).Select(a => new Announcement
        {
            Id = a.Id == Guid.Empty ? Guid.NewGuid() : a.Id,
            Title = a.Title.Trim(),
            Message = (a.Message ?? string.Empty).Trim(),
            Style = a.Style,
            Starts = a.Starts?.ToUniversalTime(),
            Ends = a.Ends?.ToUniversalTime(),
            Users = a.Users.Where(u => _userManager.GetUserById(u) is not null).Distinct().ToList()
        }).ToList());
        return Dto();
    }

    [HttpPut("Maintenance")]
    public ActionResult<HouseholdAdminDto> PutMaintenance([FromBody] List<MaintenanceWindow> body)
    {
        var list = body ?? new List<MaintenanceWindow>();
        if (list.Any(m => m.Ends <= m.Starts || (m.Message ?? string.Empty).Length > 500))
        {
            return BadRequest("Each maintenance window needs an end after its start, and a message of up to 500 characters.");
        }

        Save(c => c.Maintenance = list.Take(20).Select(m => new MaintenanceWindow
        {
            Id = m.Id == Guid.Empty ? Guid.NewGuid() : m.Id,
            Starts = m.Starts.ToUniversalTime(),
            Ends = m.Ends.ToUniversalTime(),
            Message = (m.Message ?? string.Empty).Trim()
        }).Where(m => m.Ends > DateTime.UtcNow).ToList());
        return Dto();
    }

    private static void Save(Action<PluginConfiguration> change)
    {
        var plugin = Plugin.Instance!;
        change(plugin.Configuration);
        plugin.SaveConfiguration();
    }

    private HouseholdAdminDto Dto()
    {
        var config = Plugin.Instance!.Configuration;
        var store = DownloadInventoryStore.Current;
        return new HouseholdAdminDto
        {
            Downloads = config.Downloads,
            DownloadUse = _userManager.GetUsers().ToDictionary(u => u.Id, u =>
            {
                var (titles, bytes) = store.Totals(u.Id);
                var music = store.Music(u.Id);
                return new DownloadUseDto { Titles = titles, Bytes = bytes, Albums = music.Albums, Songs = music.Songs, MusicBytes = music.Bytes };
            }),
            SharedAccounts = config.SharedAccounts,
            Announcements = config.Announcements.Select(Notices.InUtc).ToList(),
            Maintenance = config.Maintenance.Select(Notices.InUtc).ToList()
        };
    }
}

public sealed class DownloadAllowanceDto
{
    public bool MusicLimited { get; set; }

    public int? MusicMaxAlbums { get; set; }

    public int? MusicMaxSongs { get; set; }

    public long? MusicMaxBytes { get; set; }

    public int MusicUsedAlbums { get; set; }

    public int MusicUsedSongs { get; set; }

    public long MusicUsedBytes { get; set; }

    public bool Limited { get; set; }

    public int? MaxTitles { get; set; }

    public long? MaxBytes { get; set; }

    public int UsedTitles { get; set; }

    public long UsedBytes { get; set; }
}

public sealed class DownloadReportBody
{
    /// <summary>Every title this device keeps downloaded, with its size.</summary>
    public List<DownloadedTitle> Titles { get; set; } = new();

    /// <summary>Every song this device keeps downloaded, with its album and size; null leaves the last list.</summary>
    public List<DownloadedTitle>? Songs { get; set; }
}

public sealed class DownloadCheckBody
{
    /// <summary>The title about to download, so one already on another device isn't counted twice.</summary>
    public string? ItemId { get; set; }

    /// <summary>For music: the songs about to download, with their albums and sizes.</summary>
    public List<DownloadedTitle>? Songs { get; set; }

    public long Bytes { get; set; }
}

public sealed class DownloadCheckDto
{
    public bool Allowed { get; set; }

    public string? Message { get; set; }
}

public sealed class PersonDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class SharedAccountDto
{
    public bool IsShared { get; set; }

    public List<PersonDto> Members { get; set; } = new();

    /// <summary>Who was last picked as watching on this device.</summary>
    public List<Guid> Watching { get; set; } = new();
}

public sealed class WatchingBody
{
    public List<Guid> Members { get; set; } = new();
}

public sealed class NoticesDto
{
    public List<Announcement> Announcements { get; set; } = new();

    public List<MaintenanceWindow> Maintenance { get; set; } = new();
}

public sealed class DownloadUseDto
{
    public int Titles { get; set; }

    public long Bytes { get; set; }

    public int Albums { get; set; }

    public int Songs { get; set; }

    public long MusicBytes { get; set; }
}

public sealed class HouseholdAdminDto
{
    public DownloadLimitSettings Downloads { get; set; } = new();

    public Dictionary<Guid, DownloadUseDto> DownloadUse { get; set; } = new();

    public List<SharedAccount> SharedAccounts { get; set; } = new();

    public List<Announcement> Announcements { get; set; } = new();

    public List<MaintenanceWindow> Maintenance { get; set; } = new();
}
