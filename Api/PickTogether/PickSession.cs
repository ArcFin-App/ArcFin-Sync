using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ArcfinSync.Api.PickTogether;

public enum PickOrder
{
    TopRated,
    Random,
    RecentlyAdded
}

public enum PickRule
{
    Everyone,
    Most
}

public sealed record PickOptions(bool Movies, bool Shows, IReadOnlyList<string> Genres, bool IncludeWatched, PickOrder Order, PickRule Rule, bool SameOrder);

/// <summary>A title in a session's pool, with what ordering needs.</summary>
public sealed record PickTitle(Guid Id, double Rating, DateTime Added);

public sealed class PickPerson
{
    public PickPerson(string key, Guid userId, string name)
    {
        Key = key;
        UserId = userId;
        Name = name;
    }

    /// <summary>User and device: two people sharing one Jellyfin account are still two people.</summary>
    public string Key { get; }

    public Guid UserId { get; }

    public string Name { get; set; }

    /// <summary>The device's name, to tell two devices on one shared profile apart.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Titles this person can see, and hasn't watched unless watched titles are included.</summary>
    public HashSet<Guid> Eligible { get; set; } = new();

    public List<Guid> Order { get; set; } = new();

    public HashSet<Guid> Liked { get; } = new();

    public HashSet<Guid> Swiped { get; } = new();

    public DateTime LastSeen { get; set; }
}

/// <summary>
/// One swiping session: a pool of titles, the people in it, their swipes and the matches. Holds
/// no Jellyfin types, so the rules can be tested on their own. Not thread-safe: callers lock it.
/// </summary>
public sealed class PickSession
{
    /// <summary>Titles dealt per round: small enough that people agree on something soon.</summary>
    public const int DeckSize = 60;

    /// <summary>Someone who hasn't checked in for this long is away and no longer counted.</summary>
    public static readonly TimeSpan AwayAfter = TimeSpan.FromSeconds(45);

    public const int MaxPeople = 20;

    private readonly Random _random;
    private readonly int _sharedSeed;

    public PickSession(string code, string hostKey, PickOptions options, IReadOnlyList<PickTitle> pool, DateTime now, Random? random = null)
    {
        Code = code;
        HostKey = hostKey;
        Options = options;
        Pool = pool.GroupBy(t => t.Id).Select(g => g.First()).ToList();
        _random = random ?? new Random();
        _sharedSeed = _random.Next();
        LastActivity = now;
        Deck = DealDeck(new HashSet<Guid>());
    }

    public string Code { get; }

    /// <summary>
    /// The device that started the session (user and device, so on a shared profile only that
    /// one device). Only it deals new titles, removes people, or locks the session.
    /// </summary>
    public string HostKey { get; }

    /// <summary>No one new can join, even with the code.</summary>
    public bool Locked { get; set; }

    /// <summary>Devices the host removed; they can't join this session again.</summary>
    public HashSet<string> Removed { get; } = new();

    public PickOptions Options { get; }

    public IReadOnlyList<PickTitle> Pool { get; }

    /// <summary>This round's titles. Each person sees them in their own order (or the same one).</summary>
    public List<Guid> Deck { get; private set; }

    public int Round { get; private set; } = 1;

    public Dictionary<string, PickPerson> People { get; } = new();

    /// <summary>Every title dealt in this session, so a new round never repeats one.</summary>
    public HashSet<Guid> Dealt { get; } = new();

    public List<(Guid Id, int Round, DateTime At)> Matches { get; } = new();

    public DateTime LastActivity { get; private set; }

    /// <summary>Watching a match together over SyncPlay: the title, who's in, and once set up, the group.</summary>
    public PickTogetherPlan? Together { get; private set; }

    public IEnumerable<PickPerson> Present(DateTime now) => People.Values.Where(p => now - p.LastSeen < AwayAfter);

    public PickPerson Join(string key, Guid userId, string name, HashSet<Guid> eligible, DateTime now)
    {
        if (!People.TryGetValue(key, out var person))
        {
            if (Removed.Contains(key))
            {
                throw new InvalidOperationException("You were removed from this session.");
            }

            if (Locked)
            {
                throw new InvalidOperationException("This session is locked. Ask the person who started it to unlock it.");
            }

            if (People.Count >= MaxPeople)
            {
                throw new InvalidOperationException("This session is full.");
            }

            person = new PickPerson(key, userId, name) { Eligible = eligible };
            person.Order = OrderFor(person);
            People[key] = person;
        }

        person.LastSeen = now;
        LastActivity = now;
        CheckMatches(now);
        return person;
    }

    /// <summary>The host takes someone out; they can't come back with the code.</summary>
    public bool Remove(string hostKey, string key, DateTime now)
    {
        if (hostKey != HostKey || key == HostKey || !People.ContainsKey(key))
        {
            return false;
        }

        Removed.Add(key);
        LeaveTogether(key, now);
        Leave(key, now);
        return true;
    }

    public void Leave(string key, DateTime now)
    {
        People.Remove(key);
        LastActivity = now;
        CheckMatches(now);
    }

    public void Touch(string key, DateTime now)
    {
        if (People.TryGetValue(key, out var person))
        {
            person.LastSeen = now;
        }

        LastActivity = now;
    }

    /// <summary>Titles everyone present can see (and, unless watched titles count, nobody has watched).</summary>
    public bool Playable(Guid id, DateTime now) => Present(now).All(p => p.Eligible.Contains(id));

    /// <summary>The caller's next titles, in their order, skipping ones they've swiped or not everyone can see.</summary>
    public List<Guid> Next(string key, int count, DateTime now)
    {
        if (!People.TryGetValue(key, out var person))
        {
            return new List<Guid>();
        }

        return person.Order.Where(id => !person.Swiped.Contains(id) && Playable(id, now)).Take(count).ToList();
    }

    public int Remaining(string key, DateTime now)
    {
        if (!People.TryGetValue(key, out var person))
        {
            return 0;
        }

        return person.Order.Count(id => !person.Swiped.Contains(id) && Playable(id, now));
    }

    public void Swipe(string key, Guid id, bool like, DateTime now)
    {
        if (!People.TryGetValue(key, out var person) || !Deck.Contains(id))
        {
            return;
        }

        person.Swiped.Add(id);
        if (like)
        {
            person.Liked.Add(id);
        }
        else
        {
            person.Liked.Remove(id);
        }

        person.LastSeen = now;
        LastActivity = now;
        CheckMatches(now);
    }

    /// <summary>
    /// The caller wants to watch a match together. Choosing a different title starts over with
    /// just them; the first person in leads, and their device sets up the SyncPlay group.
    /// </summary>
    public void JoinTogether(string key, Guid itemId, DateTime now)
    {
        if (!People.ContainsKey(key) || !Matches.Any(m => m.Id == itemId))
        {
            return;
        }

        if (Together is null || Together.ItemId != itemId || Together.GroupId is not null)
        {
            Together = new PickTogetherPlan(itemId, key);
        }

        Together.In.Add(key);
        Touch(key, now);
    }

    public void LeaveTogether(string key, DateTime now)
    {
        if (Together is null || Together.GroupId is not null)
        {
            return;
        }

        Together.In.Remove(key);
        if (Together.In.Count == 0)
        {
            Together = null;
        }
        else if (Together.Leader == key)
        {
            Together.Leader = Together.In.First();
        }

        Touch(key, now);
    }

    /// <summary>Everyone here (at least two) wants to watch it together.</summary>
    public bool TogetherReady(DateTime now)
    {
        if (Together is null)
        {
            return false;
        }

        var present = Present(now).ToList();
        return present.Count >= 2 && present.All(p => Together.In.Contains(p.Key));
    }

    /// <summary>The leader's device made the SyncPlay group and chose what plays, from where.</summary>
    public bool SetTogetherGroup(string key, string groupId, Guid playItemId, long startTicks, DateTime now)
    {
        if (Together is null || Together.Leader != key || !TogetherReady(now))
        {
            return false;
        }

        Together.GroupId = groupId;
        Together.PlayItemId = playItemId;
        Together.StartTicks = Math.Max(0, startTicks);
        Touch(key, now);
        return true;
    }

    /// <summary>Deals a new round from titles not dealt before; everyone starts it fresh.</summary>
    public void Restart(DateTime now)
    {
        Round++;
        Deck = DealDeck(Dealt);
        foreach (var person in People.Values)
        {
            person.Liked.Clear();
            person.Swiped.Clear();
            person.Order = OrderFor(person);
        }

        LastActivity = now;
    }

    /// <summary>A title is a match once enough of the people present like it: all of them, or more than half.</summary>
    public void CheckMatches(DateTime now)
    {
        var present = Present(now).ToList();
        if (present.Count < 2)
        {
            return;
        }

        var needed = Options.Rule == PickRule.Everyone ? present.Count : (present.Count / 2) + 1;
        foreach (var id in Deck)
        {
            if (Matches.Any(m => m.Id == id && m.Round == Round) || !Playable(id, now))
            {
                continue;
            }

            if (present.Count(p => p.Liked.Contains(id)) >= needed)
            {
                Matches.Add((id, Round, now));
            }
        }
    }

    private List<Guid> DealDeck(HashSet<Guid> exclude)
    {
        var fresh = Pool.Where(t => !exclude.Contains(t.Id)).ToList();
        var deck = Rank(fresh, new Random(_random.Next())).Take(DeckSize).Select(t => t.Id).ToList();
        foreach (var id in deck)
        {
            Dealt.Add(id);
        }

        return deck;
    }

    private List<Guid> OrderFor(PickPerson person)
    {
        var titles = Deck.Select(id => Pool.First(t => t.Id == id)).ToList();
        var random = Options.SameOrder ? new Random(_sharedSeed + Round) : new Random(_random.Next());
        return Rank(titles, random).Select(t => t.Id).ToList();
    }

    /// <summary>
    /// Leans toward the chosen order without being a fixed list: top rated sorts by rating with
    /// a random nudge of up to two points, so a 7.5 can come before an 8.5 but rarely before a 9.
    /// </summary>
    private IEnumerable<PickTitle> Rank(List<PickTitle> titles, Random random)
    {
        return Options.Order switch
        {
            PickOrder.TopRated => titles.Select(t => (t, Key: t.Rating + (random.NextDouble() * 2))).OrderByDescending(x => x.Key).Select(x => x.t),
            PickOrder.RecentlyAdded => titles.Select(t => (t, Key: t.Added.AddDays(random.NextDouble() * 30))).OrderByDescending(x => x.Key).Select(x => x.t),
            _ => titles.Select(t => (t, Key: random.Next())).OrderBy(x => x.Key).Select(x => x.t)
        };
    }
}

public sealed class PickTogetherPlan
{
    public PickTogetherPlan(Guid itemId, string leader)
    {
        ItemId = itemId;
        Leader = leader;
    }

    /// <summary>The match they want to watch together.</summary>
    public Guid ItemId { get; }

    public string Leader { get; set; }

    public HashSet<string> In { get; } = new();

    public string? GroupId { get; set; }

    /// <summary>What actually plays: the movie, or the leader's next episode of a show.</summary>
    public Guid? PlayItemId { get; set; }

    public long StartTicks { get; set; }
}
