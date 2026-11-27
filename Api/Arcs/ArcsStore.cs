using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>A clip id: "{itemId:N}-{startTicks}".</summary>
public readonly record struct ClipKey(Guid ItemId, long StartTicks)
{
    public override string ToString() => $"{ItemId:N}-{StartTicks.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? value, out ClipKey key)
    {
        key = default;
        if (string.IsNullOrEmpty(value) || value.Length < 34 || value[32] != '-')
        {
            return false;
        }

        if (!Guid.TryParseExact(value[..32], "N", out var item)
            || !long.TryParse(value[33..], NumberStyles.None, CultureInfo.InvariantCulture, out var start))
        {
            return false;
        }

        key = new ClipKey(item, start);
        return true;
    }
}

/// <summary>A request clip's id: "req-{movie|tv}-{tmdbId}".</summary>
public readonly record struct RequestKey(string MediaType, int TmdbId)
{
    public const string Prefix = "req-";

    public override string ToString() => $"{Prefix}{MediaType}-{TmdbId.ToString(CultureInfo.InvariantCulture)}";

    public static bool IsRequest(string? value) => value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool TryParse(string? value, out RequestKey key)
    {
        key = default;
        if (!IsRequest(value))
        {
            return false;
        }

        var rest = value![Prefix.Length..];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        if (dash <= 0 || rest[..dash] is not ("movie" or "tv") || !int.TryParse(rest[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return false;
        }

        key = new RequestKey(rest[..dash], id);
        return true;
    }
}

public static class ArcEventKinds
{
    public const string View = "view";
    public const string Like = "like";
    public const string Unlike = "unlike";
    public const string Dislike = "dislike";
    public const string Undislike = "undislike";
    public const string Share = "share";
    public const string WatchFromHere = "watchFromHere";
    public const string OpenTitle = "openTitle";
    public const string Rewatch = "rewatch";
    public const string FewerFromSeries = "fewerFromSeries";
    public const string Hide = "hide";
    public const string Unhide = "unhide";
    public const string Report = "report";
    public const string NotNow = "notNow";
    public const string React = "react";
    public const string Request = "request";
    public const string StartEpisode = "startEpisode";

    /// <summary>Kinds about a whole film or show, which may come without a clip.</summary>
    public static readonly HashSet<string> TitleOnly = new(StringComparer.Ordinal) { OpenTitle, FewerFromSeries, Hide, Unhide, NotNow };

    public static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        View, Like, Unlike, Dislike, Undislike, Share, WatchFromHere, OpenTitle, Rewatch, FewerFromSeries, Hide, Unhide, Report, NotNow,
        React, Request, StartEpisode
    };

    /// <summary>Going from a clip to the full title: from the clip's point, or the episode from its start.</summary>
    public static bool IsWatchOn(string kind) => kind is WatchFromHere or StartEpisode;

    /// <summary>What on a request clip means "don't show me this one again".</summary>
    public static readonly HashSet<string> DismissRequest = new(StringComparer.Ordinal) { Hide, Dislike, NotNow, FewerFromSeries, Report };
}

/// <summary>One thing a person did with a clip. TitleId is the show for an episode, the film otherwise.</summary>
public sealed class ArcEventRecord
{
    public string ClipId { get; set; } = string.Empty;

    public Guid ItemId { get; set; }

    public Guid TitleId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public double? WatchedSeconds { get; set; }

    public double? ClipSeconds { get; set; }

    public string? Feed { get; set; }

    /// <summary>For react: funny, wow, sad or none.</summary>
    public string? Reaction { get; set; }

    public DateTime At { get; set; }
}

public sealed class ArcReaction
{
    public string ClipId { get; set; } = string.Empty;

    public Guid TitleId { get; set; }

    /// <summary>funny, wow or sad.</summary>
    public string Reaction { get; set; } = string.Empty;

    public DateTime At { get; set; }
}

public sealed class ArcLike
{
    public string ClipId { get; set; } = string.Empty;

    public Guid ItemId { get; set; }

    public Guid TitleId { get; set; }

    public DateTime At { get; set; }
}

public static class ArcHideKinds
{
    public const string Hide = "hide";
    public const string Fewer = "fewer";
    public const string NotNow = "notNow";
}

public sealed class ArcHidden
{
    public Guid TitleId { get; set; }

    /// <summary>hide, fewer or notNow.</summary>
    public string Kind { get; set; } = string.Empty;

    public DateTime At { get; set; }

    public DateTime? Until { get; set; }
}

public sealed class ArcsUserData
{
    public List<ArcEventRecord> Events { get; set; } = new();

    public List<ArcLike> Likes { get; set; } = new();

    public List<ArcLike> Dislikes { get; set; } = new();

    public List<ArcHidden> Hidden { get; set; } = new();

    /// <summary>Clips this person reported; never shown to them again.</summary>
    public List<string> Reported { get; set; } = new();

    public string? ClipOfTheDayDate { get; set; }

    public string? ClipOfTheDayId { get; set; }

    /// <summary>One reaction per clip at most.</summary>
    public List<ArcReaction> Reactions { get; set; } = new();

    /// <summary>Request clips the person asked Seerr for.</summary>
    public List<string> Requested { get; set; } = new();

    /// <summary>Request clips the person hid, disliked or put off: not shown again.</summary>
    public List<string> DismissedRequests { get; set; } = new();

    /// <summary>The titles hidden, held back for now or shown less, as they stand at <paramref name="now"/>.</summary>
    public List<ArcHidden> ActiveHidden(DateTime now) => Hidden.Where(h => h.Until is not { } until || until > now).ToList();
}

/// <summary>Everyone's Arcs added up, with no one's own activity in it.</summary>
public sealed class ArcsAggregate
{
    /// <summary>Who liked each clip.</summary>
    public Dictionary<string, HashSet<Guid>> ClipLikers { get; } = new(StringComparer.Ordinal);

    /// <summary>Who went from each clip to the full title.</summary>
    public Dictionary<string, HashSet<Guid>> ClipWatchFromHere { get; } = new(StringComparer.Ordinal);

    /// <summary>Per person, the titles they responded well to in Arcs; read only through the two-person rule.</summary>
    public Dictionary<Guid, HashSet<Guid>> PositiveTitles { get; } = new();

    /// <summary>Per title, how many people liked a clip of it.</summary>
    public Dictionary<Guid, HashSet<Guid>> TitleLikers { get; } = new();

    /// <summary>Who shared each clip.</summary>
    public Dictionary<string, HashSet<Guid>> ClipSharers { get; } = new(StringComparer.Ordinal);

    /// <summary>The film or show each clip people did something with belongs to.</summary>
    public Dictionary<string, Guid> ClipTitles { get; } = new(StringComparer.Ordinal);

    /// <summary>Per clip, who chose each reaction.</summary>
    public Dictionary<string, Dictionary<string, HashSet<Guid>>> ClipReactions { get; } = new(StringComparer.Ordinal);

    /// <summary>Per title, who chose each reaction on any of its clips.</summary>
    public Dictionary<Guid, Dictionary<string, HashSet<Guid>>> TitleReactions { get; } = new();

    public int Likes { get; set; }

    public int Shares { get; set; }

    public int WatchFromHere { get; set; }

    public int DistinctClips { get; set; }

    public static ArcsAggregate From(IEnumerable<(Guid UserId, ArcsUserData Data)> users)
    {
        var result = new ArcsAggregate();
        var clips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (userId, data) in users)
        {
            var positive = new HashSet<Guid>();
            foreach (var like in data.Likes)
            {
                result.ClipTitles.TryAdd(like.ClipId, like.TitleId);
                Add(result.ClipLikers, like.ClipId, userId);
                Add(result.TitleLikers, like.TitleId, userId);
                positive.Add(like.TitleId);
                result.Likes++;
            }

            foreach (var e in data.Events)
            {
                if (e.ClipId.Length == 0 || RequestKey.IsRequest(e.ClipId))
                {
                    continue;
                }

                clips.Add(e.ClipId);
                switch (e.Kind)
                {
                    case ArcEventKinds.WatchFromHere or ArcEventKinds.StartEpisode:
                        result.ClipTitles.TryAdd(e.ClipId, e.TitleId);
                        Add(result.ClipWatchFromHere, e.ClipId, userId);
                        positive.Add(e.TitleId);
                        result.WatchFromHere++;
                        break;
                    case ArcEventKinds.Share:
                        result.ClipTitles.TryAdd(e.ClipId, e.TitleId);
                        Add(result.ClipSharers, e.ClipId, userId);
                        positive.Add(e.TitleId);
                        result.Shares++;
                        break;
                }
            }

            foreach (var reaction in data.Reactions)
            {
                result.ClipTitles.TryAdd(reaction.ClipId, reaction.TitleId);
                Add(Inner(result.ClipReactions, reaction.ClipId), reaction.Reaction, userId);
                if (reaction.TitleId != Guid.Empty)
                {
                    Add(Inner(result.TitleReactions, reaction.TitleId), reaction.Reaction, userId);
                }

                positive.Add(reaction.TitleId);
            }

            var liked = data.Likes.Select(l => l.TitleId).ToHashSet();
            positive.ExceptWith(data.Dislikes.Select(d => d.TitleId).Where(t => !liked.Contains(t)));
            positive.Remove(Guid.Empty);
            if (positive.Count > 0)
            {
                result.PositiveTitles[userId] = positive;
            }
        }

        result.DistinctClips = clips.Count;
        return result;
    }

    /// <summary>Likes on a clip from everyone, or 0 when fewer than two people liked it.</summary>
    public int LikeCount(string clipId) => ClipLikers.TryGetValue(clipId, out var who) && who.Count >= 2 ? who.Count : 0;

    /// <summary>Each reaction's count on a clip, from everyone; 0 for any fewer than two people chose.</summary>
    public Dictionary<string, int> ReactionCounts(string clipId)
    {
        var counts = ArcReactionKinds.Empty();
        if (ClipReactions.TryGetValue(clipId, out var byKind))
        {
            foreach (var (kind, who) in byKind)
            {
                if (counts.ContainsKey(kind) && who.Count >= 2)
                {
                    counts[kind] = who.Count;
                }
            }
        }

        return counts;
    }

    /// <summary>
    /// People other than <paramref name="userId"/> who chose <paramref name="reaction"/> on a title's
    /// clips, or 0 when fewer than two did.
    /// </summary>
    public int TitleReactionCount(Guid titleId, string reaction, Guid userId)
    {
        if (!TitleReactions.TryGetValue(titleId, out var byKind) || !byKind.TryGetValue(reaction, out var who))
        {
            return 0;
        }

        var others = who.Count(u => u != userId);
        return others >= 2 ? others : 0;
    }

    /// <summary>
    /// How strongly people responded to each clip of a title: likes, going on to watch and shares,
    /// counted only for clips at least two different people responded to.
    /// </summary>
    public List<(string ClipId, double Score)> TopClips(Guid titleId)
    {
        var result = new List<(string, double)>();
        foreach (var (clipId, title) in ClipTitles)
        {
            if (title != titleId)
            {
                continue;
            }

            var likers = ClipLikers.GetValueOrDefault(clipId) ?? new HashSet<Guid>();
            var watchers = ClipWatchFromHere.GetValueOrDefault(clipId) ?? new HashSet<Guid>();
            var sharers = ClipSharers.GetValueOrDefault(clipId) ?? new HashSet<Guid>();
            var people = new HashSet<Guid>(likers);
            people.UnionWith(watchers);
            people.UnionWith(sharers);
            if (people.Count < 2)
            {
                continue;
            }

            result.Add((clipId, likers.Count + (1.5 * watchers.Count) + (1.2 * sharers.Count)));
        }

        return result.OrderByDescending(r => r.Item2).ThenBy(r => r.Item1, StringComparer.Ordinal).ToList();
    }

    private static Dictionary<string, HashSet<Guid>> Inner<TKey>(Dictionary<TKey, Dictionary<string, HashSet<Guid>>> map, TKey key)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var inner))
        {
            map[key] = inner = new Dictionary<string, HashSet<Guid>>(StringComparer.Ordinal);
        }

        return inner;
    }

    private static void Add<TKey>(Dictionary<TKey, HashSet<Guid>> map, TKey key, Guid user)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var set))
        {
            map[key] = set = new HashSet<Guid>();
        }

        set.Add(user);
    }
}

/// <summary>
/// Arcs activity per person (events, likes, hidden titles) in the plugin's data folder, and the
/// clips people reported, kept apart so resetting one person's history doesn't bring back a clip
/// others reported. Each person keeps their last 2000 events.
/// </summary>
public sealed class ArcsStore
{
    public const int EventLimit = 2000;
    public const int ReportsToDrop = 2;
    private const int LikeLimit = 2000;

    private static readonly object Gate = new();
    private static int _version;
    private static CachedAggregate? _aggregate;
    private readonly string _root;

    public ArcsStore(string folder)
    {
        _root = Path.Combine(folder, "arcs");
    }

    public static ArcsStore Current => new(Plugin.Instance!.DataFolderPath);

    /// <summary>Goes up on every write, so anything worked out from the files knows to start again.</summary>
    public static int Version => Volatile.Read(ref _version);

    public string Root => _root;

    public ArcsUserData Load(Guid userId)
    {
        lock (Gate)
        {
            return JsonFile.Read<ArcsUserData>(UserPath(userId)) ?? new ArcsUserData();
        }
    }

    public ArcsUserData Change(Guid userId, Action<ArcsUserData> change)
    {
        lock (Gate)
        {
            var path = UserPath(userId);
            var data = JsonFile.Read<ArcsUserData>(path) ?? new ArcsUserData();
            change(data);
            JsonFile.Write(path, data);
            Interlocked.Increment(ref _version);
            return data;
        }
    }

    /// <summary>Resets a person's Arcs: events, likes and hidden titles. Clips they reported stay dropped for everyone else.</summary>
    public void Reset(Guid userId)
    {
        lock (Gate)
        {
            var path = UserPath(userId);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Applies a batch of events to a person's data.</summary>
    public static void Apply(ArcsUserData data, IEnumerable<ArcEventRecord> events, DateTime now)
    {
        foreach (var e in events)
        {
            // The video request and the app's own event can both report one share, and a player
            // can ask for the file more than once; one share of a clip per ten minutes counts.
            if (e.Kind == ArcEventKinds.Share
                && data.Events.Any(x => x.Kind == ArcEventKinds.Share && x.ClipId == e.ClipId && Math.Abs((x.At - e.At).TotalMinutes) < 10))
            {
                continue;
            }

            if (RequestKey.IsRequest(e.ClipId))
            {
                ApplyToRequest(data, e);
                data.Events.Add(e);
                continue;
            }

            var like = new ArcLike { ClipId = e.ClipId, ItemId = e.ItemId, TitleId = e.TitleId, At = e.At };
            switch (e.Kind)
            {
                case ArcEventKinds.Like:
                    data.Dislikes.RemoveAll(l => l.ClipId == e.ClipId);
                    data.Likes.RemoveAll(l => l.ClipId == e.ClipId);
                    data.Likes.Add(like);
                    break;
                case ArcEventKinds.Unlike:
                    data.Likes.RemoveAll(l => l.ClipId == e.ClipId);
                    break;
                case ArcEventKinds.Dislike:
                    data.Likes.RemoveAll(l => l.ClipId == e.ClipId);
                    data.Dislikes.RemoveAll(l => l.ClipId == e.ClipId);
                    data.Dislikes.Add(like);
                    break;
                case ArcEventKinds.Undislike:
                    data.Dislikes.RemoveAll(l => l.ClipId == e.ClipId);
                    break;
                case ArcEventKinds.Hide:
                    SetHidden(data, e.TitleId, ArcHideKinds.Hide, e.At, null);
                    break;
                case ArcEventKinds.Unhide:
                    data.Hidden.RemoveAll(h => h.TitleId == e.TitleId && h.Kind == ArcHideKinds.Hide);
                    break;
                case ArcEventKinds.FewerFromSeries:
                    SetHidden(data, e.TitleId, ArcHideKinds.Fewer, e.At, e.At.AddDays(30));
                    break;
                case ArcEventKinds.NotNow:
                    SetHidden(data, e.TitleId, ArcHideKinds.NotNow, e.At, e.At.AddDays(7));
                    break;
                case ArcEventKinds.Report:
                    if (!data.Reported.Contains(e.ClipId))
                    {
                        data.Reported.Add(e.ClipId);
                    }

                    break;
                case ArcEventKinds.React:
                    data.Reactions.RemoveAll(r => r.ClipId == e.ClipId);
                    if (e.Reaction is { } reaction && ArcReactionKinds.All.Contains(reaction))
                    {
                        data.Reactions.Add(new ArcReaction { ClipId = e.ClipId, TitleId = e.TitleId, Reaction = reaction, At = e.At });
                    }

                    break;
            }

            data.Events.Add(e);
        }

        if (data.Events.Count > EventLimit)
        {
            data.Events.RemoveRange(0, data.Events.Count - EventLimit);
        }

        Trim(data.Likes);
        Trim(data.Dislikes);
        Trim(data.Reported);
        Trim(data.Reactions);
        Trim(data.Requested);
        Trim(data.DismissedRequests);
        data.Hidden.RemoveAll(h => h.Until is { } until && until <= now);
    }

    private static void ApplyToRequest(ArcsUserData data, ArcEventRecord e)
    {
        if (e.Kind == ArcEventKinds.Request && !data.Requested.Contains(e.ClipId))
        {
            data.Requested.Add(e.ClipId);
        }
        else if (ArcEventKinds.DismissRequest.Contains(e.Kind) && !data.DismissedRequests.Contains(e.ClipId))
        {
            data.DismissedRequests.Add(e.ClipId);
        }
    }

    public void AddReports(Guid userId, IEnumerable<string> clipIds)
    {
        lock (Gate)
        {
            var path = ReportsPath;
            var reports = JsonFile.Read<List<ArcReport>>(path) ?? new List<ArcReport>();
            foreach (var clip in clipIds)
            {
                var entry = reports.FirstOrDefault(r => r.ClipId == clip);
                if (entry is null)
                {
                    reports.Add(entry = new ArcReport { ClipId = clip });
                }

                if (!entry.Users.Contains(userId))
                {
                    entry.Users.Add(userId);
                }
            }

            JsonFile.Write(path, reports);
            Interlocked.Increment(ref _version);
        }
    }

    /// <summary>Clips reported by at least two people: never used again.</summary>
    public HashSet<string> DroppedClips()
    {
        lock (Gate)
        {
            var reports = JsonFile.Read<List<ArcReport>>(ReportsPath) ?? new List<ArcReport>();
            return reports.Where(r => r.Users.Count >= ReportsToDrop).Select(r => r.ClipId).ToHashSet(StringComparer.Ordinal);
        }
    }

    /// <summary>Everyone's activity added up, worked out again only after something changed.</summary>
    public ArcsAggregate Aggregate()
    {
        var version = Version;
        if (Volatile.Read(ref _aggregate) is { } cached && cached.Version == version && cached.Root == _root)
        {
            return cached.Value;
        }

        var users = new List<(Guid, ArcsUserData)>();
        lock (Gate)
        {
            var folder = Path.Combine(_root, "users");
            if (Directory.Exists(folder))
            {
                foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
                {
                    if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id) && JsonFile.Read<ArcsUserData>(file) is { } data)
                    {
                        users.Add((id, data));
                    }
                }
            }
        }

        var value = ArcsAggregate.From(users);
        Volatile.Write(ref _aggregate, new CachedAggregate(version, _root, value));
        return value;
    }

    private static void SetHidden(ArcsUserData data, Guid titleId, string kind, DateTime at, DateTime? until)
    {
        data.Hidden.RemoveAll(h => h.TitleId == titleId && h.Kind == kind);
        data.Hidden.Add(new ArcHidden { TitleId = titleId, Kind = kind, At = at, Until = until });
    }

    private static void Trim<T>(List<T> list)
    {
        if (list.Count > LikeLimit)
        {
            list.RemoveRange(0, list.Count - LikeLimit);
        }
    }

    private string UserPath(Guid userId) => Path.Combine(_root, "users", $"{userId:N}.json");

    private string ReportsPath => Path.Combine(_root, "reports.json");
}

internal sealed record CachedAggregate(int Version, string Root, ArcsAggregate Value);

public sealed class ArcReport
{
    public string ClipId { get; set; } = string.Empty;

    public List<Guid> Users { get; set; } = new();
}
