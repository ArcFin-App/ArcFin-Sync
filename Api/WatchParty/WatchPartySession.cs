using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ArcfinSync.Api.WatchParty;

/// <summary>
/// A watch party's lobby: the host picks a title, others join with the code, say they're
/// ready, and the host starts it. Starting hands over to SyncPlay: the host's app makes the
/// group and posts it here, and everyone else's app joins it. Jellyfin-free so it can be tested.
/// </summary>
public sealed class WatchPartySession
{
    public const int MaxPeople = 20;
    public static readonly TimeSpan AwayAfter = TimeSpan.FromSeconds(45);

    /// <summary>How long before a scheduled start the lobby opens.</summary>
    public static readonly TimeSpan LobbyOpensBefore = TimeSpan.FromMinutes(15);

    /// <summary>How long after a scheduled start a party nobody started is kept.</summary>
    public static readonly TimeSpan KeptAfterStart = TimeSpan.FromHours(1);

    /// <summary>How far ahead a party can be scheduled.</summary>
    public static readonly TimeSpan LongestAhead = TimeSpan.FromDays(30);

    public WatchPartySession(string code, string hostKey, Guid itemId, DateTime now, DateTime? scheduledFor = null)
    {
        Code = code;
        HostKey = hostKey;
        ItemId = itemId;
        Created = now;
        LastActivity = now;
        ScheduledFor = scheduledFor;
    }

    /// <summary>Everyone who was ever in it, by key, for its history.</summary>
    public Dictionary<string, string> Participants { get; } = new(StringComparer.Ordinal);

    /// <summary>When the host's SyncPlay group went up and everyone started watching.</summary>
    public DateTime? StartedAt { get; private set; }

    /// <summary>The start that was planned, kept for its history after the party starts.</summary>
    public DateTime? PlannedFor { get; private set; }

    /// <summary>When the host plans to start, in UTC, or null for a party that's on now.</summary>
    public DateTime? ScheduledFor { get; private set; }

    /// <summary>Scheduled and the lobby isn't open yet: people can join and wait, but not get ready.</summary>
    public bool IsWaiting(DateTime now) => Phase == PartyPhase.Lobby && ScheduledFor is { } at && now < at - LobbyOpensBefore;

    /// <summary>A party to forget: started or not, nobody has been around for six hours, or a scheduled one an hour after its time.</summary>
    public bool IsOver(DateTime now)
    {
        if (ScheduledFor is { } at && Phase == PartyPhase.Lobby)
        {
            return now > at + KeptAfterStart;
        }

        return now - LastActivity > TimeSpan.FromHours(6) && People.Values.All(p => now - p.LastSeen > TimeSpan.FromHours(6));
    }

    /// <summary>The host moves the start, or clears it to make the party one that's on now.</summary>
    public void Schedule(string hostKey, DateTime? at, DateTime now)
    {
        RequireHost(hostKey);
        if (Phase != PartyPhase.Lobby)
        {
            throw new InvalidOperationException("The watch party has already started.");
        }

        CheckSchedule(at, now);
        ScheduledFor = at;
        LastActivity = now;
    }

    public static void CheckSchedule(DateTime? at, DateTime now)
    {
        if (at is { } when && (when < now - TimeSpan.FromMinutes(1) || when > now + LongestAhead))
        {
            throw new InvalidOperationException("Pick a time in the next 30 days.");
        }
    }

    /// <summary>Puts back a party saved before the server restarted.</summary>
    public static WatchPartySession Restore(SavedParty saved)
    {
        var party = new WatchPartySession(saved.Code, saved.HostKey, saved.ItemId, saved.Created, saved.ScheduledFor)
        {
            Locked = saved.Locked
        };
        foreach (var p in saved.People.OrderBy(p => p.Order))
        {
            party.People[p.Key] = new PartyPerson(p.Key, p.UserId, p.Name, p.DeviceName, p.Order) { LastSeen = p.LastSeen, Ready = p.Key == saved.HostKey, TimeZone = p.TimeZone };
            party.Participants[p.Key] = p.Name;
        }

        party.Removed.UnionWith(saved.Removed);
        foreach (var (key, name) in saved.Participants)
        {
            party.Participants[key] = name;
        }

        party.LastActivity = saved.LastActivity;
        return party;
    }

    public SavedParty Save() => new()
    {
        Code = Code,
        HostKey = HostKey,
        ItemId = ItemId,
        Created = Created,
        LastActivity = LastActivity,
        ScheduledFor = ScheduledFor,
        Locked = Locked,
        Removed = Removed.ToList(),
        Participants = new Dictionary<string, string>(Participants),
        People = People.Values.Select(p => new SavedPerson { Key = p.Key, UserId = p.UserId, Name = p.Name, DeviceName = p.DeviceName, Order = p.Order, LastSeen = p.LastSeen, TimeZone = p.TimeZone }).ToList()
    };

    public string Code { get; }

    public string HostKey { get; private set; }

    public Guid ItemId { get; }

    public DateTime Created { get; }

    public DateTime LastActivity { get; private set; }

    /// <summary>Scheduled parties outlive a quiet lobby, so they're written to disk.</summary>
    public bool IsScheduled => ScheduledFor is not null && Phase == PartyPhase.Lobby;

    /// <summary>lobby, starting (the host is making the SyncPlay group) or started.</summary>
    public string Phase { get; private set; } = PartyPhase.Lobby;

    public string? GroupId { get; private set; }

    public long StartTicks { get; private set; }

    public bool Locked { get; private set; }

    public Dictionary<string, PartyPerson> People { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Removed { get; } = new(StringComparer.Ordinal);

    public PartyPerson Join(string key, Guid userId, string name, string deviceName, DateTime now, string? timeZone = null)
    {
        if (People.TryGetValue(key, out var existing))
        {
            existing.LastSeen = now;
            existing.TimeZone = CleanZone(timeZone) ?? existing.TimeZone;
            return existing;
        }

        if (Removed.Contains(key))
        {
            throw new InvalidOperationException("The host removed you from this watch party.");
        }

        if (Locked && key != HostKey)
        {
            throw new InvalidOperationException("The host has closed this watch party to new people.");
        }

        if (People.Count >= MaxPeople)
        {
            throw new InvalidOperationException("This watch party is full.");
        }

        var person = new PartyPerson(key, userId, name, deviceName, People.Count) { LastSeen = now, Ready = key == HostKey, TimeZone = CleanZone(timeZone) };
        People[key] = person;
        Participants[key] = name;
        LastActivity = now;
        return person;
    }

    public void Touch(string key, DateTime now, string? timeZone = null)
    {
        if (People.TryGetValue(key, out var person))
        {
            person.LastSeen = now;
            person.TimeZone = CleanZone(timeZone) ?? person.TimeZone;
        }
    }

    /// <summary>An IANA time zone id as a device sent it, or null when it isn't one.</summary>
    public static string? CleanZone(string? zone)
    {
        var trimmed = zone?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 64 || trimmed.Any(c => !(char.IsLetterOrDigit(c) || c is '/' or '_' or '-' or '+'))
            ? null
            : trimmed;
    }

    public void SetReady(string key, bool ready, DateTime now)
    {
        if (IsWaiting(now))
        {
            throw new InvalidOperationException("The lobby opens 15 minutes before the start.");
        }

        if (People.TryGetValue(key, out var person))
        {
            person.Ready = ready;
            person.LastSeen = now;
            LastActivity = now;
        }
    }

    public void SetLocked(string hostKey, bool locked)
    {
        RequireHost(hostKey);
        Locked = locked;
    }

    public void Remove(string hostKey, string key, DateTime now)
    {
        RequireHost(hostKey);
        if (key == HostKey)
        {
            throw new InvalidOperationException("The host can't remove themselves; they can leave instead.");
        }

        if (People.Remove(key))
        {
            Removed.Add(key);
            LastActivity = now;
        }
    }

    /// <summary>The host leaving hands the party to whoever joined next, so it doesn't end for everyone.</summary>
    public void Leave(string key, DateTime now)
    {
        if (!People.Remove(key))
        {
            return;
        }

        LastActivity = now;
        if (key == HostKey && People.Count > 0)
        {
            HostKey = People.Values.OrderBy(p => p.Order).First().Key;
            People[HostKey].Ready = true;
        }
    }

    public bool IsAway(PartyPerson person, DateTime now) => now - person.LastSeen > AwayAfter;

    /// <summary>The host says go: their app now makes the SyncPlay group.</summary>
    public void Start(string hostKey, long startTicks, DateTime now)
    {
        RequireHost(hostKey);
        if (Phase != PartyPhase.Lobby)
        {
            return;
        }

        Phase = PartyPhase.Starting;
        PlannedFor ??= ScheduledFor;
        ScheduledFor = null;
        StartTicks = Math.Max(0, startTicks);
        LastActivity = now;
    }

    /// <summary>The SyncPlay group's name, for people joining from another app.</summary>
    public string? GroupName { get; private set; }

    public void SetGroup(string hostKey, string groupId, DateTime now, string? groupName = null)
    {
        RequireHost(hostKey);
        GroupId = groupId;
        GroupName = string.IsNullOrWhiteSpace(groupName) ? null : groupName.Trim()[..Math.Min(groupName.Trim().Length, 80)];
        StartedAt ??= now;
        Phase = PartyPhase.Started;
        Locked = false;
        LastActivity = now;
    }

    /// <summary>Back to the lobby, e.g. when making the group failed or everyone wants another go.</summary>
    public void Reset(string hostKey, DateTime now)
    {
        RequireHost(hostKey);
        Phase = PartyPhase.Lobby;
        GroupId = null;
        GroupName = null;
        foreach (var person in People.Values)
        {
            person.Ready = person.Key == HostKey;
        }

        LastActivity = now;
    }

    private void RequireHost(string key)
    {
        if (key != HostKey)
        {
            throw new UnauthorizedAccessException("Only the host can do that.");
        }
    }
}

public static class PartyPhase
{
    public const string Lobby = "lobby";
    public const string Starting = "starting";
    public const string Started = "started";
}

public sealed class PartyPerson
{
    public PartyPerson(string key, Guid userId, string name, string deviceName, int order)
    {
        Key = key;
        UserId = userId;
        Name = name;
        DeviceName = deviceName;
        Order = order;
    }

    public string Key { get; }

    public Guid UserId { get; }

    public string Name { get; }

    public string DeviceName { get; }

    public int Order { get; }

    public bool Ready { get; set; }

    public DateTime LastSeen { get; set; }

    /// <summary>The device's time zone, so the host sees what a planned start is for each person.</summary>
    public string? TimeZone { get; set; }
}

public sealed class SavedParty
{
    public string Code { get; set; } = string.Empty;

    public string HostKey { get; set; } = string.Empty;

    public Guid ItemId { get; set; }

    public DateTime Created { get; set; }

    public DateTime LastActivity { get; set; }

    public DateTime? ScheduledFor { get; set; }

    public bool Locked { get; set; }

    public List<string> Removed { get; set; } = new();

    public Dictionary<string, string> Participants { get; set; } = new();

    public List<SavedPerson> People { get; set; } = new();
}

public sealed class SavedPerson
{
    public string Key { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public int Order { get; set; }

    public DateTime LastSeen { get; set; }

    public string? TimeZone { get; set; }
}

public static class PartyHistory
{
    /// <summary>The party as a history entry: watched once it started, otherwise <paramref name="outcome"/>.</summary>
    public static History.HistoryEntry Entry(WatchPartySession party, string outcome, DateTime now) => new()
    {
        Kind = History.HistoryStore.WatchParty,
        Code = party.Code,
        Created = party.Created,
        ScheduledFor = party.PlannedFor ?? party.ScheduledFor,
        StartedWatching = party.StartedAt,
        Ended = now,
        HostName = party.Participants.TryGetValue(party.HostKey, out var host) ? host : string.Empty,
        People = party.Participants.Values.Distinct().ToList(),
        ItemId = party.ItemId,
        Outcome = party.StartedAt is not null ? "watched" : outcome
    };
}
