using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Api.Recommendations;
using Jellyfin.Plugin.ArcfinSync.Configuration;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

public static class ArcFeeds
{
    public const string ForYou = "forYou";
    public const string Continue = "continue";
    public const string Discover = "discover";
    public const string Series = "series";
    public const string Liked = "liked";

    public static bool IsKnown(string? feed) => feed is ForYou or Continue or Discover or Series or Liked;
}

/// <summary>The feed's moods: each keeps to some genres and lifts titles people reacted to in its way.</summary>
public static class ArcMoods
{
    public const string Funny = "funny";
    public const string Action = "action";
    public const string FeelGood = "feelgood";
    public const string Scary = "scary";
    public const string Drama = "drama";
    public const string SciFi = "scifi";

    private static readonly Dictionary<string, string[]> GenresByMood = new(StringComparer.Ordinal)
    {
        [Funny] = new[] { "Comedy", "Animation" },
        [Action] = new[] { "Action", "Adventure", "Thriller", "Crime", "War" },
        [FeelGood] = new[] { "Romance", "Family", "Music", "Comedy" },
        [Scary] = new[] { "Horror", "Thriller", "Mystery" },
        [Drama] = new[] { "Drama", "History" },
        [SciFi] = new[] { "Science Fiction", "Sci-Fi & Fantasy", "Fantasy" },
    };

    public static bool IsKnown(string? mood) => mood is not null && GenresByMood.ContainsKey(mood);

    /// <summary>Whether a title with these genres belongs in the mood.</summary>
    public static bool Matches(string mood, IEnumerable<string> genres)
    {
        var wanted = GenresByMood[mood];
        return genres.Any(g => wanted.Any(w => w.Equals(g, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The reaction a mood looks for, if any: funny for funny, wow for action and sci-fi, sad for drama.</summary>
    public static string? Reaction(string mood) => mood switch
    {
        Funny => ArcReactionKinds.Funny,
        Action or SciFi => ArcReactionKinds.Wow,
        Drama => ArcReactionKinds.Sad,
        _ => null,
    };
}

public static class ArcReasons
{
    public const string Because = "because";
    public const string Continue = "continue";
    public const string Discover = "discover";
    public const string Popular = "popular";
    public const string New = "new";
    public const string Explore = "explore";
    public const string Series = "series";
    public const string Liked = "liked";
    public const string Top = "top";
    public const string Request = "request";
}

/// <summary>A film or show clips can come from, and where the person is with it.</summary>
public sealed record ArcTitle(TitleFeatures Features, bool Started, bool InProgress, DateTime? LastPlayed, int PlayedByOthers)
{
    public Guid Id => Features.Id;
}

public sealed record ScoredTitle(ArcTitle Title, double Score, string Reason, string? SeedName);

/// <summary>Everything the ranking reads, gathered by the caller.</summary>
public sealed class RankInput
{
    public List<ArcTitle> Titles { get; init; } = new();

    /// <summary>Jellyfin history, likes and favourites (see <see cref="Recommender.Profile(IEnumerable{Signal}, DateTime, double, double, double)"/>), with Arcs events added.</summary>
    public Dictionary<string, double> Profile { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The person's own Arcs response to each title, from <see cref="ArcsRanker.Affinity"/>.</summary>
    public Dictionary<Guid, double> Affinity { get; init; } = new();

    public Dictionary<Guid, double> Collaborative { get; init; } = new();

    /// <summary>Clips of each title viewed in the last three days, so the feed doesn't keep coming back to one.</summary>
    public Dictionary<Guid, int> RecentViews { get; init; } = new();

    /// <summary>Clip likes per title from at least two people.</summary>
    public Dictionary<Guid, int> ArcsLikers { get; init; } = new();

    public List<ArcHidden> Hidden { get; init; } = new();

    /// <summary>Titles the person likes most, for "Because you liked".</summary>
    public List<TitleFeatures> Seeds { get; init; } = new();

    public ArcsWeights Weights { get; init; } = new();

    public DateTime Now { get; init; }

    public string Feed { get; init; } = ArcFeeds.ForYou;

    /// <summary>One of <see cref="ArcMoods"/>, or null.</summary>
    public string? Mood { get; init; }

    /// <summary>The lift for each title from reactions that fit the mood, from <see cref="ArcsRanker.MoodBoosts"/>.</summary>
    public Dictionary<Guid, double> MoodBoost { get; init; } = new();
}

/// <summary>
/// Orders the titles in someone's library for their Arcs feed. Each title's score adds up:
/// how well it fits their taste (genres, people, tags and studios from what they've watched,
/// liked and favourited, plus what they did in Arcs), what people with similar taste on the
/// server responded to (only where at least two others did), their own Arcs response to it,
/// something new to the library, and what's popular. Then the person's own choices: hidden
/// titles never come back, "not now" waits a week, and "fewer" halves a title for a month.
/// </summary>
public static class ArcsRanker
{
    private const double AffinityHalfLifeDays = 60;
    private const double FreshDays = 30;
    private const int ReasonDepth = 200;
    private const int MinCoUsers = 2;

    /// <summary>
    /// The person's own response per title. Going from a clip to the full title counts most,
    /// then likes, shares, rewatches and watching to the end; skipping within a few seconds and
    /// dislikes count against. Older events fade with a two-month half-life.
    /// </summary>
    public static Dictionary<Guid, double> Affinity(ArcsUserData data, ArcsWeights w, DateTime now)
    {
        var result = new Dictionary<Guid, double>();
        void Add(Guid title, double value, DateTime at)
        {
            if (title == Guid.Empty || value == 0)
            {
                return;
            }

            var age = Math.Max(0, (now - at).TotalDays);
            result[title] = result.GetValueOrDefault(title) + value * Math.Pow(0.5, age / AffinityHalfLifeDays);
        }

        foreach (var e in data.Events)
        {
            Add(e.TitleId, EventValue(e, w), e.At);
        }

        foreach (var like in data.Likes)
        {
            Add(like.TitleId, 2.5 * w.Likes, like.At);
        }

        foreach (var dislike in data.Dislikes)
        {
            Add(dislike.TitleId, -3 * w.Likes, dislike.At);
        }

        foreach (var reaction in data.Reactions)
        {
            Add(reaction.TitleId, 1.5 * w.Likes, reaction.At);
        }

        return result;
    }

    public static double EventValue(ArcEventRecord e, ArcsWeights w) => e.Kind switch
    {
        ArcEventKinds.View => ViewValue(e.WatchedSeconds, e.ClipSeconds) * w.WatchTime,
        ArcEventKinds.Rewatch => 0.8 * w.WatchTime,
        ArcEventKinds.WatchFromHere or ArcEventKinds.StartEpisode => 3.5 * w.WatchFromHere,
        ArcEventKinds.Request => 2 * w.Likes,
        ArcEventKinds.Like when RequestKey.IsRequest(e.ClipId) => 1 * w.Likes,
        ArcEventKinds.OpenTitle => 1.2 * w.WatchFromHere,
        ArcEventKinds.Share => 1.5 * w.Shares,
        ArcEventKinds.Report => -1,
        _ => 0,
    };

    /// <summary>Watched through is good; gone within three seconds or a sixth of the clip is a skip.</summary>
    public static double ViewValue(double? watched, double? clip)
    {
        if (watched is not { } seen)
        {
            return 0;
        }

        var length = clip is > 0 ? clip.Value : 30;
        var ratio = Math.Clamp(seen / length, 0, 1);
        if (seen < 3 || ratio < 0.15)
        {
            return -0.6;
        }

        return ratio >= 0.85 ? 0.5 : (ratio - 0.35) * 0.6;
    }

    /// <summary>
    /// The lift for titles people reacted to the mood's way: the person's own reactions count
    /// most, and other people's only where at least two of them chose it.
    /// </summary>
    public static Dictionary<Guid, double> MoodBoosts(string? mood, ArcsUserData data, ArcsAggregate aggregate, Guid userId, IEnumerable<Guid> titles)
    {
        var result = new Dictionary<Guid, double>();
        if (mood is null || ArcMoods.Reaction(mood) is not { } reaction)
        {
            return result;
        }

        var own = data.Reactions.Where(r => r.Reaction == reaction && r.TitleId != Guid.Empty)
            .GroupBy(r => r.TitleId).ToDictionary(g => g.Key, g => g.Count());
        foreach (var title in titles)
        {
            var boost = (0.6 * Math.Min(3, own.GetValueOrDefault(title))) + (0.4 * Math.Log(1 + aggregate.TitleReactionCount(title, reaction, userId)));
            if (boost > 0)
            {
                result[title] = Math.Min(2, boost);
            }
        }

        return result;
    }

    /// <summary>Clips of each title viewed in the last three days.</summary>
    public static Dictionary<Guid, int> RecentViews(ArcsUserData data, DateTime now)
    {
        return data.Events.Where(e => e.Kind == ArcEventKinds.View && now - e.At < TimeSpan.FromDays(3) && e.TitleId != Guid.Empty)
            .GroupBy(e => e.TitleId).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Item-to-item: titles other people responded to alongside the ones this person did. A pair
    /// only counts when at least two other people share it, so no single person's activity can be
    /// read back from the feed. <paramref name="others"/> must not hold the person themselves.
    /// </summary>
    public static Dictionary<Guid, double> Collaborative(ISet<Guid> mine, IEnumerable<HashSet<Guid>> others)
    {
        var result = new Dictionary<Guid, double>();
        if (mine.Count == 0)
        {
            return result;
        }

        var people = others.Where(o => o.Count > 0).ToList();
        var fans = new Dictionary<Guid, int>();
        foreach (var titles in people)
        {
            foreach (var t in titles)
            {
                fans[t] = fans.GetValueOrDefault(t) + 1;
            }
        }

        var pairs = new Dictionary<(Guid Seed, Guid Candidate), int>();
        foreach (var titles in people)
        {
            foreach (var seed in titles.Where(mine.Contains))
            {
                foreach (var candidate in titles.Where(t => !mine.Contains(t)))
                {
                    pairs[(seed, candidate)] = pairs.GetValueOrDefault((seed, candidate)) + 1;
                }
            }
        }

        foreach (var ((seed, candidate), together) in pairs)
        {
            if (together < MinCoUsers)
            {
                continue;
            }

            result[candidate] = result.GetValueOrDefault(candidate) + together / Math.Sqrt(fans[seed] * (double)fans[candidate]);
        }

        return result;
    }

    /// <summary>The feed's titles, best first, without anything hidden or held back.</summary>
    public static List<ScoredTitle> Score(RankInput input)
    {
        var w = input.Weights;
        var now = input.Now;
        var active = input.Hidden.Where(h => h.Until is not { } until || until > now).ToList();
        var blocked = active.Where(h => h.Kind is ArcHideKinds.Hide or ArcHideKinds.NotNow).Select(h => h.TitleId).ToHashSet();
        var fewer = active.Where(h => h.Kind == ArcHideKinds.Fewer).Select(h => h.TitleId).ToHashSet();

        var parts = new List<(ArcTitle Title, double Score, double Content, double Collab, double Fresh, double Popular, double Continue)>();
        foreach (var title in input.Titles)
        {
            if (blocked.Contains(title.Id) || !InFeed(input.Feed, title) || (input.Mood is { } mood && !ArcMoods.Matches(mood, title.Features.Genres)))
            {
                continue;
            }

            var affinity = input.Affinity.GetValueOrDefault(title.Id);
            if (affinity <= -6)
            {
                continue;
            }

            var content = Recommender.Score(input.Profile, title.Features) * w.Similar;
            var collab = Math.Min(2.5, input.Collaborative.GetValueOrDefault(title.Id) * 1.5) * w.Similar;
            var own = Math.Tanh(affinity / 3) * 1.2;
            var ageDays = Math.Max(0, (now - title.Features.Added).TotalDays);
            var fresh = ageDays < FreshDays ? (1 - ageDays / FreshDays) * 0.8 * w.Fresh : 0;
            var others = title.PlayedByOthers >= MinCoUsers ? title.PlayedByOthers : 0;
            var likers = input.ArcsLikers.GetValueOrDefault(title.Id);
            var popular = (Math.Log(1 + others) * 0.4 + (likers >= MinCoUsers ? Math.Log(1 + likers) * 0.3 : 0)) * w.Popular;
            var keepGoing = title.InProgress ? (input.Feed == ArcFeeds.Continue ? Recency(title.LastPlayed, now) * 2 : 0.5) : 0;
            var tired = -Math.Min(1.5, input.RecentViews.GetValueOrDefault(title.Id) * 0.35);

            var score = content + collab + own + fresh + popular + keepGoing + tired + input.MoodBoost.GetValueOrDefault(title.Id);
            if (fewer.Contains(title.Id))
            {
                score = score > 0 ? score / 2 : score - 0.5;
            }

            parts.Add((title, score, content, collab, fresh, popular, keepGoing));
        }

        var ordered = parts.OrderByDescending(p => p.Score).ThenBy(p => p.Title.Id).ToList();
        var result = new List<ScoredTitle>(ordered.Count);
        var seeds = input.Seeds.Take(8).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            string reason;
            string? seedName = null;
            if (input.Feed == ArcFeeds.Continue)
            {
                reason = ArcReasons.Continue;
            }
            else if (i >= ReasonDepth)
            {
                reason = input.Feed == ArcFeeds.Discover ? ArcReasons.Discover : ArcReasons.Top;
            }
            else
            {
                (reason, seedName) = Reason(input.Feed, p.Title, seeds, p.Content + p.Collab, p.Fresh, p.Popular, p.Continue);
            }

            result.Add(new ScoredTitle(p.Title, p.Score, reason, seedName));
        }

        return result;
    }

    /// <summary>
    /// Titles outside the person's usual taste for the exploration slots: not in their three
    /// leading genres and in the lower half for fit. With no history yet, anything.
    /// </summary>
    public static List<ScoredTitle> ExplorePool(RankInput input, List<ScoredTitle> scored)
    {
        if (scored.Count == 0)
        {
            return new List<ScoredTitle>();
        }

        var leading = Recommender.TopGenres(input.Profile, 3).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lowerHalf = scored.Skip(scored.Count / 2);
        var pool = lowerHalf.Where(s => !s.Title.Features.Genres.Any(leading.Contains)).ToList();
        if (pool.Count == 0)
        {
            pool = lowerHalf.ToList();
        }

        return pool.Where(s => input.Affinity.GetValueOrDefault(s.Title.Id) > -1)
            .Select(s => s with { Reason = ArcReasons.Explore, SeedName = null }).ToList();
    }

    private static bool InFeed(string feed, ArcTitle title) => feed switch
    {
        ArcFeeds.Continue => title.InProgress,
        ArcFeeds.Discover => !title.Started,
        _ => true,
    };

    private static double Recency(DateTime? lastPlayed, DateTime now) =>
        lastPlayed is { } at ? Math.Pow(0.5, Math.Max(0, (now - at).TotalDays) / 14) : 0;

    private static (string Reason, string? Seed) Reason(string feed, ArcTitle title, List<TitleFeatures> seeds, double taste, double fresh, double popular, double keepGoing)
    {
        TitleFeatures? bestSeed = null;
        var bestSimilarity = 0.0;
        foreach (var seed in seeds)
        {
            if (seed.Id == title.Id)
            {
                continue;
            }

            var similarity = Recommender.Similarity(seed, title.Features);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestSeed = seed;
            }
        }

        var largest = new[] { taste, fresh, popular, keepGoing }.Max();
        if (largest <= 0)
        {
            return (feed == ArcFeeds.Discover ? ArcReasons.Discover : ArcReasons.Top, null);
        }

        if (keepGoing == largest && feed == ArcFeeds.ForYou)
        {
            return (ArcReasons.Continue, null);
        }

        if (taste == largest && bestSeed is not null && bestSimilarity >= 0.3)
        {
            return (ArcReasons.Because, bestSeed.Name);
        }

        if (popular == largest)
        {
            return (ArcReasons.Popular, null);
        }

        if (fresh == largest)
        {
            return (ArcReasons.New, null);
        }

        return (feed == ArcFeeds.Discover ? ArcReasons.Discover : ArcReasons.Top, null);
    }
}

/// <summary>
/// Turns ranked titles into one page of clips. No title twice in a row (across pages too), at
/// most two from one title per page of ten, no more than a third of a page from one lead genre,
/// and about one slot in six (the admin's exploration share) for something outside the person's
/// usual taste. A title already shown in this session counts for less each time. When too few
/// titles are left to keep those rules (a Continue feed with one show, say), they give way
/// rather than ending the feed.
/// </summary>
public static class ArcsPageBuilder
{
    public const int PerTitleCap = 2;
    private const double ShownAgain = 0.6;
    private const double Jitter = 0.15;

    public static List<(ScoredTitle Title, TClip Clip)> Build<TClip>(
        IReadOnlyList<ScoredTitle> ranked,
        IReadOnlyList<ScoredTitle> explore,
        int count,
        Random random,
        double exploration,
        Guid? lastTitle,
        IReadOnlyDictionary<Guid, int> shownBefore,
        Func<ScoredTitle, TClip?> resolve)
        where TClip : class
    {
        var page = new List<(ScoredTitle, TClip)>();
        if (count <= 0 || ranked.Count == 0)
        {
            return page;
        }

        var lowest = ranked.Min(r => r.Score);
        var order = ranked
            .Select(r => (Title: r, Effective: (r.Score - lowest + 0.1) * Math.Pow(ShownAgain, shownBefore.GetValueOrDefault(r.Title.Id)) * (1 + Jitter * random.NextDouble())))
            .OrderByDescending(r => r.Effective).ThenBy(r => r.Title.Title.Id).Select(r => r.Title).ToList();
        var perTitle = new Dictionary<Guid, int>();
        var perGenre = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var genreCap = Math.Max(3, (int)Math.Ceiling(count / 3.0));
        var titleCap = Math.Max(PerTitleCap, (int)Math.Ceiling(count * PerTitleCap / 10.0));
        var exhausted = new HashSet<Guid>();
        var previous = lastTitle;
        var exploreShuffled = explore.OrderBy(_ => random.Next()).ToList();

        for (var slot = 0; slot < count; slot++)
        {
            var wantExplore = exploreShuffled.Count > 0 && random.NextDouble() < exploration;
            (ScoredTitle, TClip)? chosen = null;
            if (wantExplore)
            {
                chosen = Pick(exploreShuffled, 0) ?? Pick(exploreShuffled, 1);
            }

            // The rules give way one at a time only when too few titles are left to keep them.
            for (var relax = 0; chosen is null && relax <= 3; relax++)
            {
                chosen = Pick(order, relax);
            }

            if (chosen is not { } pick)
            {
                break;
            }

            page.Add(pick);
            var id = pick.Item1.Title.Id;
            perTitle[id] = perTitle.GetValueOrDefault(id) + 1;
            var genre = LeadGenre(pick.Item1);
            perGenre[genre] = perGenre.GetValueOrDefault(genre) + 1;
            previous = id;
        }

        return page;

        // relax 0 keeps every rule; 1 drops the genre mix, 2 the per-title cap, 3 the no-repeat rule.
        (ScoredTitle, TClip)? Pick(List<ScoredTitle> from, int relax)
        {
            foreach (var candidate in from)
            {
                var id = candidate.Title.Id;
                if (exhausted.Contains(id) || (relax < 3 && id == previous) || (relax < 2 && perTitle.GetValueOrDefault(id) >= titleCap))
                {
                    continue;
                }

                if (relax < 1 && LeadGenre(candidate) is { Length: > 0 } g && perGenre.GetValueOrDefault(g) >= genreCap)
                {
                    continue;
                }

                var clip = resolve(candidate);
                if (clip is null)
                {
                    exhausted.Add(id);
                    continue;
                }

                return (candidate, clip);
            }

            return null;
        }
    }

    /// <summary>One title's clips, for "Shuffle a show": no diversity rules, just its clips in a shuffled order.</summary>
    public static List<(ScoredTitle Title, TClip Clip)> Single<TClip>(ScoredTitle title, int count, Func<ScoredTitle, TClip?> resolve)
        where TClip : class
    {
        var page = new List<(ScoredTitle, TClip)>();
        for (var i = 0; i < count; i++)
        {
            if (resolve(title) is not { } clip)
            {
                break;
            }

            page.Add((title, clip));
        }

        return page;
    }

    private static string LeadGenre(ScoredTitle title) => title.Title.Features.Genres.FirstOrDefault() ?? string.Empty;
}
