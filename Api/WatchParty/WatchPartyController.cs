using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ArcfinSync.Api.WatchParty;

/// <summary>
/// Watch parties on this server: a lobby joined with a code (typed, or from a QR code in the
/// app), never a link. Everyone must be able to see the title. A party can be on now or
/// scheduled up to 30 days ahead: people join early with the code, the lobby opens 15 minutes
/// before, and an unstarted party ends an hour after its time. Scheduled parties are saved so
/// they survive a restart; others live in memory and end after six hours without anyone.
/// An admin can turn them off for everyone or chosen users.
/// </summary>
[ApiController]
[Route("ArcfinSync/WatchParty")]
[Authorize]
public class WatchPartyController : ControllerBase
{
    private const string CodeLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly ConcurrentDictionary<string, WatchPartySession> Parties = Load();
    private static readonly object SaveGate = new();
    private static readonly ConcurrentDictionary<Guid, List<DateTime>> WrongCodes = new();
    private const int WrongCodeLimit = 10;
    private static readonly TimeSpan WrongCodeWindow = TimeSpan.FromMinutes(10);

    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private string _deviceName = string.Empty;

    public WatchPartyController(IAuthorizationContext authContext, IUserManager userManager, ILibraryManager libraryManager)
    {
        _authContext = authContext;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    [HttpGet]
    public async Task<ActionResult<PartyAvailabilityDto>> GetAvailability()
    {
        var (user, _, error) = await CallerAsync(checkAccess: false).ConfigureAwait(false);
        return error ?? (ActionResult<PartyAvailabilityDto>)new PartyAvailabilityDto { Enabled = IsAllowed(user!.Id) };
    }

    [HttpPost("Parties")]
    public async Task<ActionResult<PartyDto>> Create([FromBody] PartyCreateBody body)
    {
        var (user, key, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var item = body is null ? null : _libraryManager.GetItemById(body.ItemId);
        if (item is null || !item.IsVisible(user!))
        {
            return NotFound("That title isn't on this server.");
        }

        Sweep();
        var now = DateTime.UtcNow;
        var at = body!.ScheduledFor?.ToUniversalTime();
        try
        {
            WatchPartySession.CheckSchedule(at, now);
        }
        catch (InvalidOperationException e)
        {
            return BadRequest(e.Message);
        }

        if (at is null)
        {
            LeaveOthers(key!, null, now);
        }

        WatchPartySession party;
        do
        {
            party = new WatchPartySession(NewCode(), key!, item.Id, now, at);
        }
        while (!Parties.TryAdd(party.Code, party));

        lock (party)
        {
            party.Join(key!, user!.Id, user.Username, DeviceName(), now, body.TimeZone);
            Persist();
            return ToDto(party, key!, now);
        }
    }

    [HttpPost("Parties/{code}/Join")]
    public async Task<ActionResult<PartyDto>> Join([FromRoute] string code, [FromBody] PartyJoinBody? body)
    {
        var (user, key, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var now = DateTime.UtcNow;
        if (TooManyWrongCodes(user!.Id, now))
        {
            return StatusCode(429, "Too many codes that didn't work. Wait a few minutes, then try again.");
        }

        if (!Parties.TryGetValue(Normalize(code), out var party))
        {
            WrongCodes.AddOrUpdate(user.Id, _ => new List<DateTime> { now }, (_, list) => { lock (list) { list.Add(now); } return list; });
            return NotFound("There's no watch party with that code. It may have ended.");
        }

        var item = _libraryManager.GetItemById(party.ItemId);
        if (item is null || !item.IsVisible(user))
        {
            return StatusCode(403, "You can't watch this title on this server, so you can't join this watch party.");
        }

        if (!party.IsScheduled)
        {
            LeaveOthers(key!, party.Code, now);
        }

        lock (party)
        {
            try
            {
                party.Join(key!, user.Id, user.Username, DeviceName(), now, body?.TimeZone);
            }
            catch (InvalidOperationException e)
            {
                return Conflict(e.Message);
            }

            Persist();
            return ToDto(party, key!, now);
        }
    }

    /// <summary>The parties the caller is in, soonest first: scheduled ones to come back to, and any on now.</summary>
    [HttpGet("Parties")]
    public async Task<ActionResult<List<PartyDto>>> Mine()
    {
        var (_, key, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        Sweep();
        var now = DateTime.UtcNow;
        var mine = new List<PartyDto>();
        foreach (var party in Parties.Values)
        {
            lock (party)
            {
                if (party.People.ContainsKey(key!))
                {
                    mine.Add(ToDto(party, key!, now));
                }
            }
        }

        return mine.OrderBy(p => p.ScheduledFor ?? DateTime.MinValue).ToList();
    }

    [HttpPost("Parties/{code}/Schedule")]
    public Task<ActionResult<PartyDto>> Schedule([FromRoute] string code, [FromBody] PartyScheduleBody body)
        => WithParty(code, (party, key, now) => party.Schedule(key, body?.ScheduledFor?.ToUniversalTime(), now));

    /// <summary>The host calls the party off, for everyone.</summary>
    [HttpPost("Parties/{code}/End")]
    public async Task<ActionResult> End([FromRoute] string code)
    {
        var (_, key, error) = await CallerAsync(checkAccess: false).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!Parties.TryGetValue(Normalize(code), out var party))
        {
            return NoContent();
        }

        lock (party)
        {
            if (party.HostKey != key)
            {
                return StatusCode(403, "Only the host can end the watch party.");
            }

            Forget(party, "ended");
        }

        Persist();
        return NoContent();
    }

    /// <summary>The caller's view of the party. Polling it also marks the caller as present.</summary>
    [HttpGet("Parties/{code}")]
    public Task<ActionResult<PartyDto>> Get([FromRoute] string code, [FromQuery] string? tz)
        => WithParty(code, (party, key, now) => party.Touch(key, now, tz));

    [HttpPost("Parties/{code}/Ready")]
    public Task<ActionResult<PartyDto>> Ready([FromRoute] string code, [FromBody] PartyReadyBody body)
        => WithParty(code, (party, key, now) => party.SetReady(key, body?.Ready ?? true, now));

    [HttpPost("Parties/{code}/Lock")]
    public Task<ActionResult<PartyDto>> Lock([FromRoute] string code, [FromBody] PartyReadyBody body)
        => WithParty(code, (party, key, _) => party.SetLocked(key, body?.Ready ?? true));

    [HttpPost("Parties/{code}/Start")]
    public Task<ActionResult<PartyDto>> Start([FromRoute] string code, [FromBody] PartyStartBody? body)
        => WithParty(code, (party, key, now) => party.Start(key, body?.StartTicks ?? 0, now));

    [HttpPost("Parties/{code}/Group")]
    public Task<ActionResult<PartyDto>> Group([FromRoute] string code, [FromBody] PartyGroupBody body)
        => WithParty(code, (party, key, now) =>
        {
            if (string.IsNullOrWhiteSpace(body?.GroupId) || body.GroupId.Length > 64)
            {
                throw new InvalidOperationException("No SyncPlay group.");
            }

            party.SetGroup(key, body.GroupId, now, body.GroupName);
        });

    [HttpPost("Parties/{code}/Reset")]
    public Task<ActionResult<PartyDto>> Reset([FromRoute] string code) => WithParty(code, (party, key, now) => party.Reset(key, now));

    [HttpDelete("Parties/{code}/People/{personId}")]
    public Task<ActionResult<PartyDto>> Remove([FromRoute] string code, [FromRoute] string personId)
        => WithParty(code, (party, key, now) =>
        {
            var target = party.People.Keys.FirstOrDefault(k => PersonId(party, k) == personId)
                         ?? throw new InvalidOperationException("They've already left.");
            party.Remove(key, target, now);
        });

    [HttpDelete("Parties/{code}/Me")]
    public async Task<ActionResult> Leave([FromRoute] string code)
    {
        var (_, key, error) = await CallerAsync(checkAccess: false).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (Parties.TryGetValue(Normalize(code), out var party))
        {
            lock (party)
            {
                party.Leave(key!, DateTime.UtcNow);
                if (party.People.Count == 0)
                {
                    Forget(party, "left");
                }
            }

            Persist();
        }

        return NoContent();
    }

    private async Task<ActionResult<PartyDto>> WithParty(string code, Action<WatchPartySession, string, DateTime> change)
    {
        var (_, key, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!Parties.TryGetValue(Normalize(code), out var party))
        {
            return NotFound("This watch party has ended.");
        }

        var now = DateTime.UtcNow;
        lock (party)
        {
            if (!party.People.ContainsKey(key!))
            {
                return NotFound(party.Removed.Contains(key!) ? "The host removed you from this watch party." : "You're not in this watch party.");
            }

            try
            {
                var wasScheduled = party.IsScheduled;
                change(party, key!, now);
                if (wasScheduled || party.IsScheduled)
                {
                    Persist();
                }
            }
            catch (UnauthorizedAccessException e)
            {
                return StatusCode(403, e.Message);
            }
            catch (InvalidOperationException e)
            {
                return Conflict(e.Message);
            }

            return ToDto(party, key!, now);
        }
    }

    private PartyDto ToDto(WatchPartySession party, string key, DateTime now)
    {
        var item = _libraryManager.GetItemById(party.ItemId);
        return new PartyDto
        {
            Code = party.Code,
            Phase = party.IsWaiting(now) ? "scheduled" : party.Phase,
            ScheduledFor = party.ScheduledFor,
            LobbyOpens = party.ScheduledFor - WatchPartySession.LobbyOpensBefore,
            IsHost = party.HostKey == key,
            Locked = party.Locked,
            ItemId = party.ItemId.ToString("N"),
            ItemName = item?.Name ?? string.Empty,
            ItemType = item?.GetBaseItemKind().ToString() ?? string.Empty,
            GroupId = party.Phase == PartyPhase.Started ? party.GroupId : null,
            GroupName = party.Phase == PartyPhase.Started ? party.GroupName : null,
            StartTicks = party.StartTicks,
            People = party.People.Values.OrderBy(p => p.Order).Select(p => new PartyPersonDto
            {
                Id = PersonId(party, p.Key),
                Name = p.Name,
                DeviceName = p.DeviceName,
                IsHost = p.Key == party.HostKey,
                IsMe = p.Key == key,
                Ready = p.Ready,
                IsAway = party.IsAway(p, now),
                TimeZone = p.TimeZone
            }).ToList()
        };
    }

    private async Task<(User? User, string? Key, ActionResult? Error)> CallerAsync(bool checkAccess = true)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null)
        {
            return (null, null, Unauthorized());
        }

        if (checkAccess && !IsAllowed(user.Id))
        {
            return (null, null, StatusCode(403, "Your server's admin has turned off watch parties for you."));
        }

        _deviceName = auth.Device ?? string.Empty;
        return (user, $"{user.Id:N}:{auth.DeviceId}", null);
    }

    private string DeviceName() => _deviceName.Length > 40 ? _deviceName[..40] : _deviceName;

    private static bool IsAllowed(Guid userId)
    {
        var settings = Plugin.Instance!.Configuration.WatchParty;
        return settings.Enabled && !settings.DisabledUsers.Contains(userId);
    }

    /// <summary>Joining a party that's on now leaves any other on now; scheduled ones are kept.</summary>
    private static void LeaveOthers(string key, string? keep, DateTime now)
    {
        foreach (var other in Parties.Values.Where(p => p.Code != keep && !p.IsScheduled))
        {
            lock (other)
            {
                if (other.People.ContainsKey(key))
                {
                    other.Leave(key, now);
                    if (other.People.Count == 0)
                    {
                        Forget(other, "left");
                    }
                }
            }
        }
    }

    private static void Sweep()
    {
        var now = DateTime.UtcNow;
        var removed = false;
        foreach (var party in Parties.Values.Where(p => p.IsOver(now)))
        {
            lock (party)
            {
                removed |= Forget(party, party.ScheduledFor is not null ? "notStarted" : "left");
            }
        }

        if (removed)
        {
            Persist();
        }
    }

    /// <summary>The parties on now and scheduled, for administrators.</summary>
    internal static IReadOnlyCollection<WatchPartySession> All
    {
        get
        {
            Sweep();
            return Parties.Values.ToList();
        }
    }

    internal static WatchPartySession? Find(string code) => Parties.TryGetValue(Normalize(code), out var party) ? party : null;

    /// <summary>Ends a party for everyone, keeping it in the history when that's on. Call with the party locked.</summary>
    internal static bool Forget(WatchPartySession party, string outcome)
    {
        if (!Parties.TryRemove(party.Code, out _))
        {
            return false;
        }

        History.HistoryStore.Record(PartyHistory.Entry(party, outcome, DateTime.UtcNow));
        Persist();
        return true;
    }

    /// <summary>Saves the scheduled parties after an admin changed one.</summary>
    internal static void Saved() => Persist();

    private static string SavePath => System.IO.Path.Combine(Plugin.Instance!.DataFolderPath, "watch-parties.json");

    private static ConcurrentDictionary<string, WatchPartySession> Load()
    {
        var parties = new ConcurrentDictionary<string, WatchPartySession>();
        if (Plugin.Instance is null)
        {
            return parties;
        }

        var now = DateTime.UtcNow;
        foreach (var saved in Storage.JsonFile.Read<List<SavedParty>>(SavePath) ?? new List<SavedParty>())
        {
            var party = WatchPartySession.Restore(saved);
            if (!party.IsOver(now))
            {
                parties[party.Code] = party;
            }
        }

        return parties;
    }

    /// <summary>Writes the scheduled parties, the ones that must outlive a restart.</summary>
    private static void Persist()
    {
        if (Plugin.Instance is null)
        {
            return;
        }

        lock (SaveGate)
        {
            var scheduled = new List<SavedParty>();
            foreach (var party in Parties.Values)
            {
                lock (party)
                {
                    if (party.IsScheduled)
                    {
                        scheduled.Add(party.Save());
                    }
                }
            }

            Storage.JsonFile.Write(SavePath, scheduled);
        }
    }

    private static bool TooManyWrongCodes(Guid userId, DateTime now)
    {
        if (!WrongCodes.TryGetValue(userId, out var list))
        {
            return false;
        }

        lock (list)
        {
            list.RemoveAll(t => now - t > WrongCodeWindow);
            return list.Count >= WrongCodeLimit;
        }
    }

    private static string PersonId(WatchPartySession party, string key)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(party.Code + "|" + key));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static string NewCode() => new(Enumerable.Range(0, 6).Select(_ => CodeLetters[RandomNumberGenerator.GetInt32(CodeLetters.Length)]).ToArray());

    private static string Normalize(string code) => (code ?? string.Empty).Trim().ToUpperInvariant();
}

public sealed class PartyAvailabilityDto
{
    public bool Enabled { get; set; }
}

public sealed class PartyJoinBody
{
    public string? TimeZone { get; set; }
}

public sealed class PartyCreateBody
{
    public Guid ItemId { get; set; }

    public string? TimeZone { get; set; }

    /// <summary>When to start, or null for now.</summary>
    public DateTime? ScheduledFor { get; set; }
}

public sealed class PartyScheduleBody
{
    /// <summary>The new start, or null to make it a party that's on now.</summary>
    public DateTime? ScheduledFor { get; set; }
}

public sealed class PartyReadyBody
{
    public bool Ready { get; set; } = true;
}

public sealed class PartyStartBody
{
    public long StartTicks { get; set; }
}

public sealed class PartyGroupBody
{
    public string GroupId { get; set; } = string.Empty;

    public string? GroupName { get; set; }
}

public sealed class PartyDto
{
    public string Code { get; set; } = string.Empty;

    /// <summary>scheduled (waiting for the lobby to open), lobby, starting or started.</summary>
    public string Phase { get; set; } = PartyPhase.Lobby;

    /// <summary>The planned start in UTC, for a scheduled party.</summary>
    public DateTime? ScheduledFor { get; set; }

    /// <summary>When the lobby opens, 15 minutes before the planned start.</summary>
    public DateTime? LobbyOpens { get; set; }

    public bool IsHost { get; set; }

    public bool Locked { get; set; }

    public string ItemId { get; set; } = string.Empty;

    public string ItemName { get; set; } = string.Empty;

    public string ItemType { get; set; } = string.Empty;

    /// <summary>The SyncPlay group to join, once the host has made it.</summary>
    public string? GroupId { get; set; }

    /// <summary>Its name, for anyone joining from another app with SyncPlay.</summary>
    public string? GroupName { get; set; }

    public long StartTicks { get; set; }

    public List<PartyPersonDto> People { get; set; } = new();
}

public sealed class PartyPersonDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public bool IsHost { get; set; }

    public bool IsMe { get; set; }

    public bool Ready { get; set; }

    public bool IsAway { get; set; }

    public string? TimeZone { get; set; }
}
