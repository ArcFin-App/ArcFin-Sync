using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Jellyfin.Plugin.ArcfinSync.Api.PickTogether;
using Jellyfin.Plugin.ArcfinSync.Api.WatchParty;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.History;

/// <summary>
/// Server administrators only: every watch party and Pick Together session on now or
/// scheduled, with the host's own controls over any of them (move a party's time, lock, remove
/// someone, end it), and the history of past ones when it's kept.
/// </summary>
[ApiController]
[Route("ArcfinSync/Admin")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LiveAdminController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LiveAdminController> _logger;

    public LiveAdminController(ILibraryManager libraryManager, ILogger<LiveAdminController> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    [HttpGet("Live")]
    public ActionResult<LiveDto> GetLive()
    {
        var now = DateTime.UtcNow;
        var parties = new List<LivePartyDto>();
        foreach (var party in WatchPartyController.All)
        {
            lock (party)
            {
                parties.Add(new LivePartyDto
                {
                    Code = party.Code,
                    Phase = party.IsWaiting(now) ? "scheduled" : party.Phase,
                    ItemId = party.ItemId,
                    ItemName = NameOf(party.ItemId),
                    Created = party.Created,
                    ScheduledFor = party.ScheduledFor,
                    Locked = party.Locked,
                    People = party.People.Values.OrderBy(p => p.Order).Select(p => new LivePersonDto
                    {
                        Id = PersonId(party.Code, p.Key),
                        Name = p.Name,
                        DeviceName = p.DeviceName,
                        IsHost = p.Key == party.HostKey,
                        Ready = p.Ready,
                        IsAway = party.IsAway(p, now)
                    }).ToList()
                });
            }
        }

        var sessions = new List<LivePickDto>();
        foreach (var session in PickTogetherController.All)
        {
            lock (session)
            {
                sessions.Add(new LivePickDto
                {
                    Code = session.Code,
                    Round = session.Round,
                    Created = session.Created,
                    Locked = session.Locked,
                    Matches = session.Matches.Select(m => m.Id).Distinct().Select(id => new LiveTitleDto { Id = id, Name = NameOf(id) }).ToList(),
                    WatchingTogether = session.Together?.GroupId is not null ? NameOf(session.Together.PlayItemId ?? session.Together.ItemId) : null,
                    People = session.People.Values.Select(p => new LivePersonDto
                    {
                        Id = PersonId(session.Code, p.Key),
                        Name = p.Name,
                        DeviceName = p.DeviceName,
                        IsHost = p.Key == session.HostKey,
                        Swiped = p.Swiped.Count,
                        IsAway = now - p.LastSeen > PickSession.AwayAfter
                    }).ToList()
                });
            }
        }

        return new LiveDto
        {
            WatchParties = parties.OrderBy(p => p.ScheduledFor ?? DateTime.MinValue).ToList(),
            PickSessions = sessions.OrderByDescending(s => s.Created).ToList()
        };
    }

    // MARK: Watch parties

    [HttpPost("WatchParties/{code}/Schedule")]
    public ActionResult<LiveDto> ScheduleParty([FromRoute] string code, [FromBody] PartyScheduleBody body)
        => WithParty(code, party =>
        {
            party.Schedule(party.HostKey, body?.ScheduledFor?.ToUniversalTime(), DateTime.UtcNow);
            WatchPartyController.Saved();
        });

    [HttpPost("WatchParties/{code}/Lock")]
    public ActionResult<LiveDto> LockParty([FromRoute] string code, [FromBody] LockBody body)
        => WithParty(code, party =>
        {
            party.SetLocked(party.HostKey, body?.Locked ?? true);
            WatchPartyController.Saved();
        });

    [HttpDelete("WatchParties/{code}/People/{personId}")]
    public ActionResult<LiveDto> RemoveFromParty([FromRoute] string code, [FromRoute] string personId)
        => WithParty(code, party =>
        {
            var key = party.People.Keys.FirstOrDefault(k => PersonId(party.Code, k) == personId)
                      ?? throw new InvalidOperationException("They've already left.");
            party.Remove(party.HostKey, key, DateTime.UtcNow);
            WatchPartyController.Saved();
        });

    [HttpPost("WatchParties/{code}/End")]
    public ActionResult<LiveDto> EndParty([FromRoute] string code)
        => WithParty(code, party =>
        {
            WatchPartyController.Forget(party, "cancelled");
            _logger.LogInformation("An admin ended watch party {Code}", party.Code);
        });

    // MARK: Pick Together

    [HttpPost("PickSessions/{code}/Lock")]
    public ActionResult<LiveDto> LockPick([FromRoute] string code, [FromBody] LockBody body)
        => WithSession(code, session => session.Locked = body?.Locked ?? true);

    [HttpDelete("PickSessions/{code}/People/{personId}")]
    public ActionResult<LiveDto> RemoveFromPick([FromRoute] string code, [FromRoute] string personId)
        => WithSession(code, session =>
        {
            var key = session.People.Keys.FirstOrDefault(k => PersonId(session.Code, k) == personId)
                      ?? throw new InvalidOperationException("They've already left.");
            if (key == session.HostKey)
            {
                throw new InvalidOperationException("The person who started the session can't be removed; end it instead.");
            }

            session.Remove(session.HostKey, key, DateTime.UtcNow);
        });

    [HttpPost("PickSessions/{code}/End")]
    public ActionResult<LiveDto> EndPick([FromRoute] string code)
        => WithSession(code, session =>
        {
            PickTogetherController.Forget(session, "cancelled");
            _logger.LogInformation("An admin ended Pick Together session {Code}", session.Code);
        });

    // MARK: History

    [HttpGet("History/Settings")]
    public ActionResult<HistorySettings> GetHistorySettings() => Plugin.Instance!.Configuration.History;

    /// <summary>KeepDays null keeps everything; otherwise 1 to 3650 days.</summary>
    [HttpPut("History/Settings")]
    public ActionResult<HistorySettings> PutHistorySettings([FromBody] HistorySettings body)
    {
        if (body is null || body.KeepDays is < 1 or > 3650)
        {
            return BadRequest("Keep history for 1 to 3650 days, or for ever.");
        }

        var plugin = Plugin.Instance!;
        plugin.Configuration.History = new HistorySettings { Enabled = body.Enabled, KeepDays = body.KeepDays };
        plugin.SaveConfiguration();
        HistoryStore.Current.All(body.KeepDays, DateTime.UtcNow);
        return plugin.Configuration.History;
    }

    [HttpGet("History")]
    public ActionResult<List<HistoryDto>> GetHistory([FromQuery] string? kind)
    {
        var entries = HistoryStore.Current.All(Plugin.Instance!.Configuration.History.KeepDays, DateTime.UtcNow);
        return entries.Where(e => string.IsNullOrEmpty(kind) || e.Kind == kind).Take(1000).Select(e => new HistoryDto
        {
            Id = e.Id,
            Kind = e.Kind,
            Code = e.Code,
            Created = e.Created,
            ScheduledFor = e.ScheduledFor,
            StartedWatching = e.StartedWatching,
            Ended = e.Ended,
            HostName = e.HostName,
            People = e.People,
            ItemName = e.ItemId is { } id ? NameOf(id) : null,
            Matches = e.Matches.Select(NameOf).ToList(),
            Outcome = e.Outcome
        }).ToList();
    }

    [HttpDelete("History")]
    public ActionResult ClearHistory()
    {
        HistoryStore.Current.Clear();
        _logger.LogInformation("An admin cleared the watch party and Pick Together history");
        return NoContent();
    }

    private ActionResult<LiveDto> WithParty(string code, Action<WatchPartySession> change)
    {
        var party = WatchPartyController.Find(code);
        if (party is null)
        {
            return NotFound("That watch party has ended.");
        }

        lock (party)
        {
            try
            {
                change(party);
            }
            catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException)
            {
                return Conflict(e.Message);
            }
        }

        return GetLive();
    }

    private ActionResult<LiveDto> WithSession(string code, Action<PickSession> change)
    {
        var session = PickTogetherController.Find(code);
        if (session is null)
        {
            return NotFound("That session has ended.");
        }

        lock (session)
        {
            try
            {
                change(session);
            }
            catch (InvalidOperationException e)
            {
                return Conflict(e.Message);
            }
        }

        return GetLive();
    }

    private string NameOf(Guid id) => _libraryManager.GetItemById(id)?.Name ?? "A title that's no longer on the server";

    private static string PersonId(string code, string key)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code + "|" + key));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }
}

public sealed class LockBody
{
    public bool Locked { get; set; } = true;
}

public sealed class LiveDto
{
    public List<LivePartyDto> WatchParties { get; set; } = new();

    public List<LivePickDto> PickSessions { get; set; } = new();
}

public sealed class LivePartyDto
{
    public string Code { get; set; } = string.Empty;

    /// <summary>scheduled, lobby, starting or started.</summary>
    public string Phase { get; set; } = string.Empty;

    public Guid ItemId { get; set; }

    public string ItemName { get; set; } = string.Empty;

    public DateTime Created { get; set; }

    public DateTime? ScheduledFor { get; set; }

    public bool Locked { get; set; }

    public List<LivePersonDto> People { get; set; } = new();
}

public sealed class LivePickDto
{
    public string Code { get; set; } = string.Empty;

    public int Round { get; set; }

    public DateTime Created { get; set; }

    public bool Locked { get; set; }

    public List<LiveTitleDto> Matches { get; set; } = new();

    /// <summary>What they're watching together, once they are.</summary>
    public string? WatchingTogether { get; set; }

    public List<LivePersonDto> People { get; set; } = new();
}

public sealed class LivePersonDto
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public bool IsHost { get; set; }

    public bool Ready { get; set; }

    public int Swiped { get; set; }

    public bool IsAway { get; set; }
}

public sealed class LiveTitleDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class HistoryDto
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public DateTime Created { get; set; }

    public DateTime? ScheduledFor { get; set; }

    public DateTime? StartedWatching { get; set; }

    public DateTime Ended { get; set; }

    public string HostName { get; set; } = string.Empty;

    public List<string> People { get; set; } = new();

    public string? ItemName { get; set; }

    public List<string> Matches { get; set; } = new();

    public string Outcome { get; set; } = string.Empty;
}
