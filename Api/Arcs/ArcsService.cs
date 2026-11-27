using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.ArcfinSync.Api.Recommendations;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>A clip picked for a feed: the film or episode, where it starts and ends.</summary>
public sealed record ResolvedClip(BaseItem Item, ClipCandidate Candidate)
{
    public string Id => new ClipKey(Item.Id, Candidate.StartTicks).ToString();
}

/// <summary>What one person's feed is made from, read from Jellyfin and kept for a few minutes.</summary>
internal sealed class UserLibrary
{
    public DateTime Built { get; init; }

    public int ConfigStamp { get; init; }

    public Dictionary<Guid, ArcTitle> Titles { get; } = new();

    public ConcurrentDictionary<Guid, BaseItem> Items { get; } = new();

    public List<Signal> Signals { get; init; } = new();

    public HashSet<Guid> PlayedEpisodes { get; } = new();

    public Dictionary<Guid, SeriesProgress> Furthest { get; } = new();

    public HashSet<Guid> PlayedMovies { get; } = new();

    public Dictionary<Guid, long> Resume { get; } = new();

    public ConcurrentDictionary<Guid, List<Episode>> Episodes { get; } = new();
}

internal sealed record Ranking(int Version, List<ScoredTitle> Scored, List<ScoredTitle> Explore);

/// <summary>What a feed page may add to the ranked scenes: a mood, request clips, and scenes others sent.</summary>
public sealed record FeedExtras(string? Mood, IReadOnlyList<RequestCandidate>? Requests, bool Sent);

/// <summary>
/// Arcs on the Jellyfin side: reads people's libraries, history and favourites, finds clips in
/// films and episodes, ranks them with <see cref="ArcsRanker"/>, and keeps feed sessions so
/// pages are stable. Library data is kept per person for ten minutes; the ranking is made again
/// whenever they do something in Arcs.
/// </summary>
public sealed class ArcsService
{
    public const string AlwaysSafe = "AlwaysSafe";
    private const int PoolLimit = 4000;
    private const int MaxSessionsPerUser = 12;
    private const int ItemTriesPerResolve = 6;
    private const int SearchTitleLimit = 400;
    private static readonly TimeSpan LibraryLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan ClipInfoLifetime = TimeSpan.FromMinutes(30);
    private static readonly MediaSegmentType[] SkippedSegments =
    {
        MediaSegmentType.Intro, MediaSegmentType.Recap, MediaSegmentType.Outro, MediaSegmentType.Preview, MediaSegmentType.Commercial
    };

    private static readonly SemaphoreSlim PrewarmGate = new(1, 1);
    private static readonly ConcurrentDictionary<Guid, UserLibrary> Libraries = new();
    private static readonly ConcurrentDictionary<string, Ranking> Rankings = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Guid, int> UserVersions = new();
    private static readonly ConcurrentDictionary<Guid, List<(string Key, ArcSession Session)>> Sessions = new();
    private static readonly ConcurrentDictionary<Guid, (DateTime At, List<ChapterMark> Chapters, List<SkipRange> Segments)> ClipInfo = new();
    private static int _configStamp;
    private static (DateTime At, HashSet<string> Ids)? _libraryTmdb;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userData;
    private readonly IChapterManager _chapters;
    private readonly IMediaSegmentManager _segments;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _config;
    private readonly ILogger _logger;
    private readonly LibraryTitles _titles;

    public ArcsService(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userData, IChapterManager chapters,
                       IMediaSegmentManager segments, IMediaEncoder mediaEncoder, IConfigurationManager config, ILogger logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userData = userData;
        _chapters = chapters;
        _segments = segments;
        _mediaEncoder = mediaEncoder;
        _config = config;
        _logger = logger;
        _titles = new LibraryTitles(userManager, libraryManager, userData);
    }

    public static ArcsSettings Settings => Plugin.Instance!.Configuration.Arcs;

    public static string DataFolder => Plugin.Instance!.DataFolderPath;

    public static bool IsAllowed(Guid userId) => Settings.Enabled && !Settings.DisabledUsers.Contains(userId);

    /// <summary>After an admin change: every library view and ranking is made again.</summary>
    public static void InvalidateAll()
    {
        Interlocked.Increment(ref _configStamp);
        Libraries.Clear();
        Rankings.Clear();
        ClipInfo.Clear();
    }

    /// <summary>After someone does something in Arcs: their next page is ranked again.</summary>
    public static void Invalidate(Guid userId) => UserVersions.AddOrUpdate(userId, 1, (_, v) => v + 1);

    public static void Forget(Guid userId)
    {
        Invalidate(userId);
        Libraries.TryRemove(userId, out _);
        Sessions.TryRemove(userId, out _);
    }

    private static int VersionFor(Guid userId) => unchecked((UserVersions.GetValueOrDefault(userId) * 7919) + Volatile.Read(ref _configStamp));

    public int ClampSeconds(int? seconds) => ClipPicker.Seconds(seconds, Settings.MinSeconds, Settings.MaxSeconds, Settings.DefaultSeconds);

    public bool SpoilerSafe(bool requested) => requested || Settings.Spoilers == AlwaysSafe;

    // ----- Feed -----

    public (List<ArcClipDto> Clips, string? Cursor, List<ResolvedClip> Resolved) Feed(User user, string feed, int count, ArcCursor? cursor, bool spoilerSafe,
                                                                                         int seconds, Guid? seriesId, ISet<string> exclude, FeedExtras? extras = null)
    {
        var now = DateTime.UtcNow;
        var data = ArcsStore.Current.Load(user.Id);
        var aggregate = ArcsStore.Current.Aggregate();
        if (feed == ArcFeeds.Liked)
        {
            return Liked(user, data, aggregate, count, cursor?.Page ?? 0, exclude);
        }

        var library = LibraryFor(user, now);
        var mood = feed is ArcFeeds.Series ? null : extras?.Mood;
        var requests = feed is ArcFeeds.ForYou or ArcFeeds.Discover ? extras?.Requests : null;
        var withSent = extras?.Sent == true && feed == ArcFeeds.ForYou;
        var sessionKey = string.Create(CultureInfo.InvariantCulture, $"{feed}|{seriesId:N}|{spoilerSafe}|{seconds}|{mood}|{requests is not null}|{withSent}");
        var session = SessionFor(user.Id, sessionKey, cursor?.Seed ?? Random.Shared.Next(), now);
        var pageIndex = cursor?.Page ?? 0;
        var version = VersionFor(user.Id);

        lock (session)
        {
            if (session.Stored(pageIndex, version, exclude) is { Data: List<ArcClipDto> storedClips } stored)
            {
                var more = stored.ClipIds.Count >= count;
                return (storedClips, more ? new ArcCursor(session.Seed, pageIndex + 1).Encode() : null, new List<ResolvedClip>());
            }

            var ranking = RankingFor(user, library, data, aggregate, feed, seriesId, version, now, mood);
            var used = session.ShownBefore(pageIndex);
            used.UnionWith(exclude);
            used.UnionWith(data.Reported);
            used.UnionWith(data.Dislikes.Select(d => d.ClipId));
            var dropped = ArcsStore.Current.DroppedClips();
            used.UnionWith(dropped);
            var random = session.RandomFor(pageIndex);
            var reported = dropped.Concat(data.Reported).Select(c => ClipKey.TryParse(c, out var k) ? k : default).Where(k => k.ItemId != Guid.Empty)
                .GroupBy(k => k.ItemId).ToDictionary(g => g.Key, g => (ISet<long>)g.Select(k => k.StartTicks).ToHashSet());

            ResolvedClip? Resolve(ScoredTitle title)
            {
                var clip = PickClip(user, library, title.Title, feed, spoilerSafe, seconds, used, reported, random);
                if (clip is not null)
                {
                    used.Add(clip.Id);
                }

                return clip;
            }

            var sent = withSent && pageIndex == 0 ? SentClips(user, library, data, aggregate, count, spoilerSafe, seconds, used, now) : new List<(ArcClipDto Dto, ResolvedClip Clip)>();
            var pool = requests is null ? new List<RequestCandidate>() : RequestsFor(data, requests, used, now, mood);
            var slots = pool.Count == 0 ? new HashSet<int>()
                : RequestRules.Slots(session.Seed, session.CountBefore(pageIndex), count, sent.Count, RequestKey.IsRequest(session.LastClipBefore(pageIndex)));
            var requestCount = Math.Min(slots.Count, pool.Count);
            var sceneCount = count - sent.Count - requestCount;

            List<(ScoredTitle Title, ResolvedClip Clip)> page;
            if (sceneCount <= 0)
            {
                page = new();
            }
            else if (feed == ArcFeeds.Series)
            {
                page = ranking.Scored.Count == 0 ? new() : ArcsPageBuilder.Single(ranking.Scored[0], sceneCount, Resolve);
            }
            else
            {
                var exploration = feed == ArcFeeds.ForYou ? Math.Clamp(Settings.Weights.Exploration, 0, 0.5) : 0;
                page = ArcsPageBuilder.Build(ranking.Scored, ranking.Explore, sceneCount, random, exploration,
                                             session.LastTitleBefore(pageIndex), session.TitlesBefore(pageIndex), Resolve);
            }

            var titles = page.ToDictionary(p => p.Clip.Id, p => p.Title.Title.Id, StringComparer.Ordinal);
            foreach (var (dto, clip) in sent)
            {
                titles.TryAdd(dto.Id, TitleOf(clip.Item.Id, null));
            }

            var scenes = page.Select(p => ToDto(user, p.Clip, p.Title.Reason, p.Title.SeedName, library, data, aggregate)).ToList();
            var clips = RequestRules.Interleave(sent.Select(x => x.Dto).ToList(), scenes, pool.Take(requestCount).Select(RequestDto).ToList(), slots, count);
            session.Store(pageIndex, new ArcPage(version, clips.Select(c => c.Id).ToList(), clips.Select(c => titles.GetValueOrDefault(c.Id)).ToList(), clips));
            var next = page.Count >= sceneCount ? new ArcCursor(session.Seed, pageIndex + 1).Encode() : null;
            return (clips, next, sent.Select(x => x.Clip).Concat(page.Select(p => p.Clip)).ToList());
        }
    }

    /// <summary>Scenes sent to this person they haven't watched yet, oldest first, that they may still see.</summary>
    private List<(ArcClipDto Dto, ResolvedClip Clip)> SentClips(User user, UserLibrary library, ArcsUserData data, ArcsAggregate aggregate, int count, bool spoilerSafe,
                                                               int seconds, ISet<string> used, DateTime now)
    {
        var result = new List<(ArcClipDto, ResolvedClip)>();
        foreach (var sent in ArcsInbox.Current.Unseen(user.Id, now))
        {
            if (result.Count >= count)
            {
                break;
            }

            if (used.Contains(sent.ClipId) || !ClipKey.TryParse(sent.ClipId, out var key) || Clip(user, key, seconds) is not { } clip
                || (spoilerSafe && !SpoilerAllows(library, clip)))
            {
                continue;
            }

            var dto = ToDto(user, clip, ArcReasons.Top, null, library, data, aggregate);
            dto.SentBy = _userManager.GetUserById(sent.FromUserId)?.Username;
            dto.SentNote = sent.Note;
            used.Add(clip.Id);
            result.Add((dto, clip));
        }

        return result;
    }

    /// <summary>How many sent scenes this person hasn't watched yet, among those the feed would show them now.</summary>
    public int UnseenCount(User user, bool spoilerSafe)
    {
        var now = DateTime.UtcNow;
        var unseen = ArcsInbox.Current.Unseen(user.Id, now);
        if (unseen.Count == 0)
        {
            return 0;
        }

        var library = spoilerSafe ? LibraryFor(user, now) : null;
        return unseen.Select(s => s.ClipId).Distinct(StringComparer.Ordinal)
            .Count(id => ClipKey.TryParse(id, out var key) && Clip(user, key, Settings.DefaultSeconds) is { } clip && (library is null || SpoilerAllows(library, clip)));
    }

    public static string TitleLine(BaseItem item) => item is Episode episode
        ? ArcsSentNotice.TitleLine(episode.SeriesName ?? episode.Name, episode.ParentIndexNumber, episode.IndexNumber)
        : ArcsSentNotice.TitleLine(item.Name, null, null);

    /// <summary>Whether the spoiler rules allow a clip for this person, as the feed applies them.</summary>
    private static bool SpoilerAllows(UserLibrary library, ResolvedClip clip)
    {
        if (clip.Item is Episode episode)
        {
            return SpoilerRules.AllowsEpisode(true, library.PlayedEpisodes.Contains(episode.Id), episode.ParentIndexNumber, episode.IndexNumber,
                                              library.Furthest.GetValueOrDefault(episode.SeriesId));
        }

        return clip.Candidate.EndTicks <= SpoilerRules.LatestEnd(true, true, library.PlayedMovies.Contains(clip.Item.Id), clip.Item.RunTimeTicks ?? 0);
    }

    /// <summary>Request clips not shown yet, not requested or put away, not seen in the last week, and in the mood if there is one.</summary>
    public static List<RequestCandidate> RequestsFor(ArcsUserData data, IReadOnlyList<RequestCandidate> requests, ISet<string> used, DateTime now, string? mood)
    {
        var mine = MyRequests(data, now);
        return requests.Where(r => !used.Contains(r.Id) && !mine.Contains(r.Id) && (mood is null || ArcMoods.Matches(mood, r.Genres))).ToList();
    }

    /// <summary>Request clips this person requested, put away, or saw in the last week.</summary>
    public static HashSet<string> MyRequests(ArcsUserData data, DateTime now)
    {
        var mine = new HashSet<string>(data.Requested, StringComparer.Ordinal);
        mine.UnionWith(data.DismissedRequests);
        mine.UnionWith(data.Events.Where(e => e.Kind == ArcEventKinds.View && RequestKey.IsRequest(e.ClipId) && now - e.At < TimeSpan.FromDays(7)).Select(e => e.ClipId));
        return mine;
    }

    public static ArcClipDto RequestDto(RequestCandidate c) => new()
    {
        Id = c.Id,
        ItemId = string.Empty,
        Kind = ArcClipKinds.Request,
        // The contract's Type is Movie or Episode; MediaType says whether it's a film or a show.
        Type = c.MediaType == "movie" ? "Movie" : "Episode",
        Name = c.Name,
        Overview = c.Overview,
        Year = c.Year,
        OfficialRating = c.Certification,
        CommunityRating = c.Rating,
        Genres = c.Genres.ToList(),
        RuntimeTicks = c.RuntimeTicks,
        Reason = new ArcReasonDto { Kind = ArcReasons.Request, SeedName = c.SeedName },
        TmdbId = c.TmdbId,
        MediaType = c.MediaType,
        TrailerYouTubeKey = Plugin.Instance?.Configuration.Arcs.YouTubeTrailers == false ? null : c.TrailerKey,
        PosterPath = c.PosterPath,
        BackdropPath = c.BackdropPath,
    };

    /// <summary>The library titles request clips are found from: the ones this person liked or watched most that TMDB knows.</summary>
    public List<RequestSeed> RequestSeeds(User user)
    {
        var now = DateTime.UtcNow;
        var library = LibraryFor(user, now);
        var data = ArcsStore.Current.Load(user.Id);
        var w = Settings.Weights;
        var affinity = ArcsRanker.Affinity(data, w, now);
        var weights = library.Signals.Select(sig => (sig.Title.Id, Weight: Recommender.SignalWeight(sig, now, w.History, w.Likes, w.Favorites)))
            .Concat(affinity.Select(a => (Id: a.Key, Weight: a.Value)))
            .GroupBy(x => x.Id).Select(g => (Id: g.Key, Weight: g.Sum(x => x.Weight))).Where(x => x.Weight > 0.5).OrderByDescending(x => x.Weight);
        var seeds = new List<RequestSeed>();
        foreach (var (id, weight) in weights)
        {
            var item = library.Items.GetValueOrDefault(id) ?? _libraryManager.GetItemById(id);
            if (item is not (Movie or Series) || !int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var tmdb) || tmdb <= 0)
            {
                continue;
            }

            seeds.Add(new RequestSeed(id, item.Name ?? string.Empty, item is Movie ? "movie" : "tv", tmdb, weight));
            if (seeds.Count == 8)
            {
                break;
            }
        }

        return seeds;
    }

    /// <summary>Every film and show on the server by TMDB id ("movie:123", "tv:456"), kept for half an hour.</summary>
    public ISet<string> LibraryTmdbIds()
    {
        if (_libraryTmdb is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(30))
        {
            return cached.Ids;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series }, Recursive = true }))
        {
            if (int.TryParse(item.GetProviderId(MetadataProvider.Tmdb), NumberStyles.None, CultureInfo.InvariantCulture, out var tmdb))
            {
                ids.Add(RequestRules.LibraryKey(item is Movie ? "movie" : "tv", tmdb));
            }
        }

        _libraryTmdb = (DateTime.UtcNow, ids);
        return ids;
    }

    /// <summary>The person's clip of the day: the top of their For You feed, spoiler-safe, the same all day.</summary>
    public ArcClipDto? ClipOfTheDay(User user)
    {
        var now = DateTime.UtcNow;
        var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var store = ArcsStore.Current;
        var data = store.Load(user.Id);
        var aggregate = store.Aggregate();
        if (data.ClipOfTheDayDate == today && ClipKey.TryParse(data.ClipOfTheDayId, out var saved)
            && Clip(user, saved, Settings.DefaultSeconds) is { } kept && !data.Reported.Contains(data.ClipOfTheDayId!))
        {
            var library = LibraryFor(user, now);
            return ToDto(user, kept, ArcReasons.Top, null, library, data, aggregate);
        }

        var seed = int.Parse(now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) ^ user.Id.GetHashCode();
        var (clips, _, _) = Feed(user, ArcFeeds.ForYou, 1, new ArcCursor(seed, 0), true, Settings.DefaultSeconds, null, new HashSet<string>());
        if (clips.Count == 0)
        {
            return null;
        }

        store.Change(user.Id, d =>
        {
            d.ClipOfTheDayDate = today;
            d.ClipOfTheDayId = clips[0].Id;
        });
        return clips[0];
    }

    private (List<ArcClipDto>, string?, List<ResolvedClip>) Liked(User user, ArcsUserData data, ArcsAggregate aggregate, int count, int page, ISet<string> exclude)
    {
        var library = LibraryFor(user, DateTime.UtcNow);
        var likes = data.Likes.OrderByDescending(l => l.At).ThenBy(l => l.ClipId, StringComparer.Ordinal).ToList();
        var clips = new List<ArcClipDto>();
        var resolved = new List<ResolvedClip>();
        foreach (var like in likes.Skip(page * count).Take(count).Where(l => !exclude.Contains(l.ClipId)))
        {
            if (ClipKey.TryParse(like.ClipId, out var key) && Clip(user, key, Settings.DefaultSeconds) is { } clip)
            {
                clips.Add(ToDto(user, clip, ArcReasons.Liked, null, library, data, aggregate));
                resolved.Add(clip);
            }
        }

        var more = likes.Count > (page + 1) * count;
        return (clips, more ? new ArcCursor(0, page + 1).Encode() : null, resolved);
    }

    private static ArcSession SessionFor(Guid userId, string key, int seed, DateTime now)
    {
        var list = Sessions.GetOrAdd(userId, _ => new List<(string, ArcSession)>());
        lock (list)
        {
            list.RemoveAll(s => now - s.Session.Touched > SessionLifetime);
            var found = list.FirstOrDefault(s => s.Key == key && s.Session.Seed == seed).Session;
            if (found is null)
            {
                found = new ArcSession(seed, now);
                list.Add((key, found));
                if (list.Count > MaxSessionsPerUser)
                {
                    list.RemoveAt(0);
                }
            }

            found.Touched = now;
            return found;
        }
    }

    // ----- Ranking -----

    private Ranking RankingFor(User user, UserLibrary library, ArcsUserData data, ArcsAggregate aggregate, string feed, Guid? seriesId, int version, DateTime now,
                               string? mood = null)
    {
        var key = $"{user.Id:N}|{feed}|{seriesId:N}|{mood}";
        if (Rankings.TryGetValue(key, out var cached) && cached.Version == version)
        {
            return cached;
        }

        Ranking ranking;
        if (feed == ArcFeeds.Series)
        {
            var title = seriesId is { } id ? TitleFor(user, library, id) : null;
            var scored = title is null ? new List<ScoredTitle>() : new List<ScoredTitle> { new(title, 0, ArcReasons.Series, null) };
            ranking = new Ranking(version, scored, new List<ScoredTitle>());
        }
        else
        {
            var input = BuildInput(user, library, data, aggregate, feed, now, mood);
            var scored = ArcsRanker.Score(input);
            var explore = feed == ArcFeeds.ForYou ? ArcsRanker.ExplorePool(input, scored) : new List<ScoredTitle>();
            ranking = new Ranking(version, scored, explore);
        }

        if (Rankings.Count > 5000)
        {
            Rankings.Clear();
        }

        Rankings[key] = ranking;
        return ranking;
    }

    private RankInput BuildInput(User user, UserLibrary library, ArcsUserData data, ArcsAggregate aggregate, string feed, DateTime now, string? mood)
    {
        var w = Settings.Weights;
        var profile = Recommender.Profile(library.Signals, now, w.History, w.Likes, w.Favorites);
        var affinity = ArcsRanker.Affinity(data, w, now);
        foreach (var (titleId, value) in affinity)
        {
            if (library.Titles.TryGetValue(titleId, out var title))
            {
                Recommender.AddToProfile(profile, title.Features, value * 0.6);
            }
        }

        var mine = new HashSet<Guid>(affinity.Where(a => a.Value > 1).Select(a => a.Key));
        mine.UnionWith(library.Signals.Where(s => s.Likes == true || s.Favorite).Select(s => s.Title.Id));
        var others = aggregate.PositiveTitles.Where(p => p.Key != user.Id).Select(p => p.Value);
        var likers = aggregate.TitleLikers
            .Select(t => (t.Key, Count: t.Value.Count(u => u != user.Id)))
            .Where(t => t.Count >= 2).ToDictionary(t => t.Key, t => t.Count);

        var seeds = library.Signals.Select(s => (s.Title, Weight: Recommender.SignalWeight(s, now, w.History, w.Likes, w.Favorites)))
            .Concat(affinity.Where(a => library.Titles.ContainsKey(a.Key)).Select(a => (library.Titles[a.Key].Features, Weight: a.Value)))
            .Where(s => s.Weight > 0.5)
            .GroupBy(s => s.Item1.Id).Select(g => (Title: g.First().Item1, Weight: g.Sum(x => x.Weight)))
            .OrderByDescending(s => s.Weight).Select(s => s.Title).Take(8).ToList();

        return new RankInput
        {
            Titles = library.Titles.Values.ToList(),
            Profile = profile,
            Affinity = affinity,
            Collaborative = ArcsRanker.Collaborative(mine, others),
            RecentViews = ArcsRanker.RecentViews(data, now),
            ArcsLikers = likers,
            Hidden = data.Hidden,
            Seeds = seeds,
            Weights = w,
            Now = now,
            Feed = feed,
            Mood = mood,
            MoodBoost = ArcsRanker.MoodBoosts(mood, data, aggregate, user.Id, library.Titles.Keys),
        };
    }

    // ----- Library -----

    private UserLibrary LibraryFor(User user, DateTime now)
    {
        var stamp = Volatile.Read(ref _configStamp);
        if (Libraries.TryGetValue(user.Id, out var cached) && cached.ConfigStamp == stamp && now - cached.Built < LibraryLifetime)
        {
            return cached;
        }

        var library = BuildLibrary(user, now, stamp);
        Libraries[user.Id] = library;
        Invalidate(user.Id);
        return library;
    }

    private UserLibrary BuildLibrary(User user, DateTime now, int stamp)
    {
        var settings = Settings;
        var banned = settings.BannedItems.ToHashSet();
        var libraries = AncestorIds(_libraryManager, settings.Libraries);
        var library = new UserLibrary { Built = now, ConfigStamp = stamp, Signals = _titles.Signals(user) };

        var items = new List<BaseItem>();
        foreach (var kind in new[] { BaseItemKind.Movie, BaseItemKind.Series })
        {
            items.AddRange(_libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { kind },
                Recursive = true,
                IsVirtualItem = false,
                AncestorIds = libraries,
                OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
                Limit = PoolLimit
            }));
        }

        items = items.Where(i => !banned.Contains(i.Id)).DistinctBy(i => i.Id).ToList();

        var playedEpisodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true,
            IsPlayed = true,
            IsVirtualItem = false,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = 5000
        }).OfType<Episode>().ToList();
        var lastPlayed = new Dictionary<Guid, DateTime?>();
        foreach (var episode in playedEpisodes)
        {
            library.PlayedEpisodes.Add(episode.Id);
            if (episode.SeriesId == Guid.Empty)
            {
                continue;
            }

            if (!lastPlayed.ContainsKey(episode.SeriesId))
            {
                lastPlayed[episode.SeriesId] = _userData.GetUserData(user, episode)?.LastPlayedDate;
            }

            if (episode.ParentIndexNumber is { } s && s > 0 && episode.IndexNumber is { } e)
            {
                var furthest = library.Furthest.GetValueOrDefault(episode.SeriesId);
                if (furthest is null || s > furthest.Season || (s == furthest.Season && e > furthest.Episode))
                {
                    library.Furthest[episode.SeriesId] = new SeriesProgress(s, e);
                }
            }
        }

        // A show is in progress while an episode after the furthest one played is still unplayed.
        var started = lastPlayed.Keys.ToArray();
        var ahead = new HashSet<Guid>();
        if (started.Length > 0)
        {
            foreach (var episode in _libraryManager.GetItemList(new InternalItemsQuery(user)
                     {
                         IncludeItemTypes = new[] { BaseItemKind.Episode },
                         Recursive = true,
                         IsPlayed = false,
                         IsVirtualItem = false,
                         AncestorIds = started,
                     }).OfType<Episode>())
            {
                if (library.Furthest.TryGetValue(episode.SeriesId, out var f) && episode.ParentIndexNumber is { } s && s > 0 && episode.IndexNumber is { } e
                    && (s > f.Season || (s == f.Season && e > f.Episode)))
                {
                    ahead.Add(episode.SeriesId);
                }
            }
        }

        var movies = items.Where(i => i is Movie).ToList();
        var movieData = movies.Count == 0 ? new Dictionary<Guid, UserItemData>() : _userData.GetUserDataBatch(movies, user);
        var others = _titles.PlayedByOthers(user, now);
        var features = _titles.Features(items).ToDictionary(f => f.Id);
        foreach (var item in items)
        {
            bool isStarted, inProgress;
            DateTime? played;
            if (item is Series)
            {
                isStarted = lastPlayed.ContainsKey(item.Id);
                inProgress = isStarted && ahead.Contains(item.Id);
                played = lastPlayed.GetValueOrDefault(item.Id);
            }
            else
            {
                var d = movieData.GetValueOrDefault(item.Id);
                if (d?.Played == true)
                {
                    library.PlayedMovies.Add(item.Id);
                }

                if (d is { Played: false, PlaybackPositionTicks: > 0 })
                {
                    library.Resume[item.Id] = d.PlaybackPositionTicks;
                }

                isStarted = d?.Played == true || d?.PlaybackPositionTicks > 0;
                inProgress = library.Resume.ContainsKey(item.Id);
                played = d?.LastPlayedDate;
            }

            library.Titles[item.Id] = new ArcTitle(features[item.Id], isStarted, inProgress, played, others.GetValueOrDefault(item.Id));
            library.Items[item.Id] = item;
        }

        return library;
    }

    /// <summary>
    /// The ids to query a set of libraries by: a library's items hang under its folders on disk,
    /// not under the library itself, so those folders' ids go in too.
    /// </summary>
    public static Guid[] AncestorIds(ILibraryManager libraryManager, IEnumerable<Guid> libraries)
    {
        var ids = new HashSet<Guid>();
        foreach (var id in libraries)
        {
            ids.Add(id);
            if (libraryManager.GetItemById(id) is Folder folder)
            {
                ids.UnionWith(folder.GetIdsForAncestorQuery());
                if (folder is CollectionFolder collection)
                {
                    ids.UnionWith(collection.PhysicalFolderIds);
                }
            }
        }

        return ids.ToArray();
    }

    /// <summary>A title for "Shuffle a show", even one outside the pool.</summary>
    private ArcTitle? TitleFor(User user, UserLibrary library, Guid id)
    {
        if (library.Titles.TryGetValue(id, out var title))
        {
            return title;
        }

        var item = _libraryManager.GetItemById(id);
        if (item is not (Series or Movie) || !CanUse(user, item))
        {
            return null;
        }

        library.Items[item.Id] = item;
        return new ArcTitle(_titles.Features(new[] { item })[0], false, false, null, 0);
    }

    /// <summary>Whether a film, show or episode may be used for this person: visible to them, in an Arcs library, not banned.</summary>
    public bool CanUse(User user, BaseItem item)
    {
        var settings = Settings;
        if (!item.IsVisible(user) || settings.BannedItems.Contains(item.Id))
        {
            return false;
        }

        if (item is Episode episode && settings.BannedItems.Contains(episode.SeriesId))
        {
            return false;
        }

        if (settings.Libraries.Count > 0)
        {
            var folders = _libraryManager.GetCollectionFolders(item);
            if (!folders.Any(f => settings.Libraries.Contains(f.Id)))
            {
                return false;
            }
        }

        return true;
    }

    // ----- Clips -----

    private ResolvedClip? PickClip(User user, UserLibrary library, ArcTitle title, string feed, bool spoilerSafe, int seconds, ISet<string> used,
                                   Dictionary<Guid, ISet<long>> reported, Random random)
    {
        if (!library.Items.TryGetValue(title.Id, out var item))
        {
            return null;
        }

        List<BaseItem> playable;
        if (item is Series series)
        {
            var furthest = library.Furthest.GetValueOrDefault(series.Id);
            playable = EpisodesOf(user, library, series)
                .Where(e => feed != ArcFeeds.Continue || library.PlayedEpisodes.Contains(e.Id))
                .Where(e => SpoilerRules.AllowsEpisode(spoilerSafe, library.PlayedEpisodes.Contains(e.Id), e.ParentIndexNumber, e.IndexNumber, furthest))
                .OrderBy(_ => random.Next()).Cast<BaseItem>().ToList();
        }
        else
        {
            playable = new List<BaseItem> { item };
        }

        foreach (var candidateItem in playable.Take(ItemTriesPerResolve))
        {
            var runtime = candidateItem.RunTimeTicks ?? 0;
            var latest = SpoilerRules.LatestEnd(spoilerSafe, candidateItem is Movie, library.PlayedMovies.Contains(candidateItem.Id), runtime);
            if (feed == ArcFeeds.Continue && library.Resume.TryGetValue(candidateItem.Id, out var position))
            {
                latest = Math.Min(latest, position);
            }

            var options = Candidates(candidateItem, seconds, latest, reported.GetValueOrDefault(candidateItem.Id))
                .Where(c => !used.Contains(new ClipKey(candidateItem.Id, c.StartTicks).ToString())).ToList();
            if (options.Count > 0)
            {
                // A cold open is made to hook people in: it's the scene picked more often than not.
                var cold = options.FirstOrDefault(c => c.IsColdOpen);
                var pick = cold is not null && random.NextDouble() < 0.6 ? cold : options[random.Next(options.Count)];
                return new ResolvedClip(candidateItem, pick);
            }
        }

        return null;
    }

    private List<Episode> EpisodesOf(User user, UserLibrary library, Series series)
    {
        return library.Episodes.GetOrAdd(series.Id, _ => _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
            AncestorIds = new[] { series.Id },
        }).OfType<Episode>().Where(e => e.RunTimeTicks > 0).ToList());
    }

    /// <summary>Every clip start of a film or episode for a length, as <see cref="ClipPicker.Candidates"/> works them out.</summary>
    public List<ClipCandidate> Candidates(BaseItem item, int seconds, long latestEnd, ISet<long>? reported)
    {
        var runtime = item.RunTimeTicks ?? 0;
        if (runtime <= 0)
        {
            return new List<ClipCandidate>();
        }

        var (chapters, segments) = ClipInfoFor(item);
        IReadOnlyList<long>? scenes = null;
        var isEpisode = item is Episode;
        if (ClipPicker.ContentChapters(chapters, runtime, Settings.MinSeconds, ClipPicker.HeadTicks(runtime, segments, isEpisode)).Count < 3
            && Settings.UseSceneDetection
            && SceneIndex.Load(DataFolder).TryGetValue(item.Id, out var entry))
        {
            scenes = entry.Starts;
        }

        return ClipPicker.Candidates(runtime, chapters, segments, scenes, seconds, Settings.MinSeconds, latestEnd, reported, isEpisode);
    }

    private (List<ChapterMark> Chapters, List<SkipRange> Segments) ClipInfoFor(BaseItem item)
    {
        var now = DateTime.UtcNow;
        if (ClipInfo.TryGetValue(item.Id, out var cached) && now - cached.At < ClipInfoLifetime)
        {
            return (cached.Chapters, cached.Segments);
        }

        var chapters = _chapters.GetChapters(item.Id).Select(c => new ChapterMark(c.StartPositionTicks, c.Name)).ToList();
        var segments = new List<SkipRange>();
        try
        {
            if (_segments.HasSegments(item.Id))
            {
                var options = _libraryManager.GetLibraryOptions(item);
                segments = _segments.GetSegmentsAsync(item, SkippedSegments, options, false).GetAwaiter().GetResult()
                    .Select(s => new SkipRange(s.StartTicks, s.EndTicks, s.Type == MediaSegmentType.Intro)).ToList();
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException)
        {
            _logger.LogDebug("Arcfin Sync Arcs: no media segments for {Item}: {Message}", item.Id, error.Message);
        }

        if (ClipInfo.Count > 20000)
        {
            ClipInfo.Clear();
        }

        ClipInfo[item.Id] = (now, chapters, segments);
        return (chapters, segments);
    }

    /// <summary>A clip by id, if the item is still there and this person may see it. Its end follows the same rules as the feed.</summary>
    public ResolvedClip? Clip(User user, ClipKey key, int seconds)
    {
        var item = _libraryManager.GetItemById(key.ItemId);
        if (item is not (Movie or Episode) || item.RunTimeTicks is not { } runtime || key.StartTicks < 0 || key.StartTicks >= runtime || !CanUse(user, item))
        {
            return null;
        }

        var match = Candidates(item, seconds, runtime, null).FirstOrDefault(c => c.StartTicks == key.StartTicks);
        var end = match?.EndTicks ?? Math.Min(runtime, key.StartTicks + (seconds * ClipPicker.TicksPerSecond));
        if (end <= key.StartTicks)
        {
            return null;
        }

        return new ResolvedClip(item, new ClipCandidate(key.StartTicks, end, match?.ChapterName, match?.IsColdOpen == true));
    }

    private ArcClipDto ToDto(User user, ResolvedClip clip, string reason, string? seed, UserLibrary library, ArcsUserData data, ArcsAggregate aggregate)
    {
        var item = clip.Item;
        var episode = item as Episode;
        var source = item.GetMediaSources(false).FirstOrDefault();
        var streams = source?.MediaStreams ?? (IReadOnlyList<MediaStream>)item.GetMediaStreams();
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var genres = item.Genres is { Length: > 0 } own ? own
            : episode is not null && library.Items.TryGetValue(episode.SeriesId, out var show) ? show.Genres : Array.Empty<string>();
        var id = clip.Id;
        return new ArcClipDto
        {
            Id = id,
            ItemId = item.Id.ToString("N"),
            MediaSourceId = source?.Id,
            Type = episode is not null ? "Episode" : "Movie",
            Name = item.Name ?? string.Empty,
            SeriesId = episode is not null && episode.SeriesId != Guid.Empty ? episode.SeriesId.ToString("N") : null,
            SeriesName = episode?.SeriesName,
            SeasonNumber = episode?.ParentIndexNumber,
            EpisodeNumber = episode?.IndexNumber,
            ChapterName = clip.Candidate.ChapterName,
            Overview = item.Overview,
            Year = item.ProductionYear,
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            Genres = genres.ToList(),
            StartTicks = clip.Candidate.StartTicks,
            EndTicks = clip.Candidate.EndTicks,
            RuntimeTicks = item.RunTimeTicks ?? 0,
            Width = video?.Width ?? (item.Width > 0 ? item.Width : null),
            Height = video?.Height ?? (item.Height > 0 ? item.Height : null),
            Subtitles = streams.Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream)
                .Select(s => new ArcSubtitleDto { Index = s.Index, Language = s.Language, Title = s.Title ?? s.DisplayTitle, IsDefault = s.IsDefault }).ToList(),
            Reason = new ArcReasonDto { Kind = reason, SeedName = reason == ArcReasons.Because ? seed : null },
            Liked = data.Likes.Any(l => l.ClipId == id),
            Disliked = data.Dislikes.Any(l => l.ClipId == id),
            IsFavorite = _userData.GetUserData(user, item)?.IsFavorite == true,
            LikeCount = aggregate.LikeCount(id),
            IsColdOpen = clip.Candidate.IsColdOpen,
            MyReaction = data.Reactions.FirstOrDefault(r => r.ClipId == id)?.Reaction,
            Reactions = aggregate.ReactionCounts(id),
        };
    }

    /// <summary>A clip as an ArcClip for administrators' stats, with the admin's own likes left out.</summary>
    public ArcClipDto? StatsClip(User admin, string clipId, ArcsAggregate aggregate)
    {
        if (!ClipKey.TryParse(clipId, out var key) || Clip(admin, key, Settings.DefaultSeconds) is not { } clip)
        {
            return null;
        }

        var dto = ToDto(admin, clip, ArcReasons.Top, null, new UserLibrary(), new ArcsUserData(), aggregate);
        dto.IsFavorite = false;
        return dto;
    }

    // ----- Rendering -----

    /// <summary>The render request for a clip, or null when the source can't be read.</summary>
    public static ClipRenderRequest? RenderRequest(ResolvedClip clip, string quality, int? subtitleIndex, Func<string, string> escapePath, out string? problem)
    {
        problem = null;
        var source = clip.Item.GetMediaSources(false).FirstOrDefault();
        if (source?.Path is not { Length: > 0 } path || (source.Protocol == MediaBrowser.Model.MediaInfo.MediaProtocol.File && !System.IO.File.Exists(path)))
        {
            problem = "The file for this clip isn't available.";
            return null;
        }

        BurnSubtitle? subtitle = null;
        if (subtitleIndex is { } index)
        {
            var stream = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Subtitle && s.Index == index);
            if (stream is null || !stream.IsTextSubtitleStream)
            {
                problem = "That subtitle can't be added to a clip.";
                return null;
            }

            if (stream.IsExternal)
            {
                if (string.IsNullOrEmpty(stream.Path) || !System.IO.File.Exists(stream.Path))
                {
                    problem = "That subtitle can't be added to a clip.";
                    return null;
                }

                subtitle = new BurnSubtitle(escapePath(stream.Path), null);
            }
            else
            {
                var relative = source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal).OrderBy(s => s.Index).ToList().IndexOf(stream);
                subtitle = new BurnSubtitle(escapePath(path), relative);
            }
        }

        var seconds = (int)Math.Max(1, (clip.Candidate.EndTicks - clip.Candidate.StartTicks) / ClipPicker.TicksPerSecond);
        var height = quality == "low" ? ClipRenderer.LowHeight : ClipRenderer.HighHeight;
        var video = source.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        return new ClipRenderRequest(path, clip.Candidate.StartTicks, seconds, height, subtitle,
                                     video is null ? ClipHdr.None : HdrOf(video.VideoRangeType, video.ColorTransfer),
                                     video?.Codec, video?.BitDepth ?? 8);
    }

    /// <summary>What a video stream's range needs to come down to standard range.</summary>
    public static ClipHdr HdrOf(VideoRangeType range, string? colorTransfer) => range switch
    {
        VideoRangeType.HDR10 or VideoRangeType.HDR10Plus or VideoRangeType.DOVIWithHDR10 or VideoRangeType.DOVIWithHDR10Plus
            or VideoRangeType.DOVIWithEL or VideoRangeType.DOVIWithELHDR10Plus => ClipHdr.Pq,
        VideoRangeType.HLG or VideoRangeType.DOVIWithHLG => ClipHdr.Hlg,
        VideoRangeType.DOVI or VideoRangeType.DOVIInvalid => ClipHdr.DolbyVision,
        VideoRangeType.Unknown when colorTransfer is "smpte2084" => ClipHdr.Pq,
        VideoRangeType.Unknown when colorTransfer is "arib-std-b67" => ClipHdr.Hlg,
        _ => ClipHdr.None
    };

    /// <summary>
    /// The GPU clips may use: only when Jellyfin itself transcodes with NVENC and this ffmpeg has the
    /// H.264 encoder for it. The other acceleration types use the CPU for clips.
    /// </summary>
    public static ClipGpu? GpuFrom(EncodingOptions options, bool hasNvenc)
    {
        if (options.HardwareAccelerationType != HardwareAccelerationType.nvenc || !options.EnableHardwareEncoding || !hasNvenc)
        {
            return null;
        }

        return new ClipGpu(options.HardwareDecodingCodecs ?? Array.Empty<string>(), options.EnableDecodingColorDepth10Hevc, options.EnableDecodingColorDepth10Vp9);
    }

    private ClipRenderContext RenderContext()
    {
        var gpu = WarmGpu(_config, _mediaEncoder, _logger);
        return new ClipRenderContext(_mediaEncoder.EncoderPath, ClipRenderer.CacheFolder(DataFolder), Settings.CacheMb * 1024L * 1024L, gpu);
    }

    /// <summary>
    /// The GPU for clips once it's warmed up (see ClipRenderer.EnsureGpuWarm); until then, and when
    /// Jellyfin doesn't use NVENC, null, so clips use the CPU.
    /// </summary>
    public static ClipGpu? WarmGpu(IConfigurationManager config, IMediaEncoder mediaEncoder, ILogger logger)
    {
        var gpu = GpuFrom(config.GetEncodingOptions(), mediaEncoder.SupportsEncoder("h264_nvenc"));
        if (gpu is null)
        {
            return null;
        }

        ClipRenderer.CudaCachePath ??= System.IO.Path.Combine(DataFolder, "arcs", "cuda-cache");
        ClipRenderer.EnsureGpuWarm(mediaEncoder.EncoderPath, logger);
        return ClipRenderer.GpuReady ? gpu : null;
    }

    /// <summary>The whole clip as an MP4, for sharing. Null when it couldn't be made.</summary>
    public Task<string?> RenderAsync(ResolvedClip clip, ClipRenderRequest request, string quality, int? subtitleIndex, CancellationToken cancellationToken)
    {
        var key = ClipRenderer.CacheKey(clip.Id, request.Seconds, quality, subtitleIndex);
        return ClipRenderer.GetAsync(RenderContext(), key, request, _logger, cancellationToken);
    }

    /// <summary>The cache key of a clip's HLS.</summary>
    public static string HlsKey(ResolvedClip clip, ClipRenderRequest request, string quality) => ClipRenderer.CacheKey(clip.Id, request.Seconds, quality, null, "hls");

    /// <summary>The clip's HLS playlist once its first segment is made (waiting up to <paramref name="wait"/>), as ffmpeg wrote it; null when it isn't.</summary>
    public Task<string?> PlaylistAsync(ResolvedClip clip, ClipRenderRequest request, string quality, TimeSpan wait, CancellationToken cancellationToken)
    {
        return ClipRenderer.PlaylistAsync(RenderContext(), HlsKey(clip, request, quality), clip.Id, request, _logger, wait, cancellationToken);
    }

    /// <summary>Whether this person may still see the film or episode a clip comes from.</summary>
    public bool CanSee(User user, ClipKey key)
    {
        return _libraryManager.GetItemById(key.ItemId) is { } item and (Movie or Episode) && CanUse(user, item);
    }

    /// <summary>
    /// Makes the first clips of a page as HLS in the background, so the first swipes don't wait. One
    /// prewarm render at a time across the server, so a clip someone is waiting for always has a free slot.
    /// </summary>
    public void Prewarm(IEnumerable<ResolvedClip> clips, string quality)
    {
        var first = clips.Take(Math.Clamp(Settings.Prewarm, 0, 10)).ToList();
        if (first.Count == 0)
        {
            return;
        }

        var escape = _mediaEncoder.EscapeSubtitleFilterPath;
        var context = RenderContext();
        _ = Task.Run(async () =>
        {
            foreach (var clip in first)
            {
                try
                {
                    if (RenderRequest(clip, quality, null, escape, out _) is not { } request)
                    {
                        continue;
                    }

                    await PrewarmGate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await ClipRenderer.EnsureHlsAsync(context, HlsKey(clip, request, quality), clip.Id, request, _logger).ConfigureAwait(false);
                    }
                    finally
                    {
                        PrewarmGate.Release();
                    }
                }
                catch (Exception error)
                {
                    _logger.LogDebug(error, "Arcfin Sync Arcs: prewarm of {Clip} failed", clip.Id);
                }
            }
        });
    }

    // ----- Dialogue search, best scenes, stats -----

    /// <summary>
    /// Scenes where a line of dialogue matches the search: every word in one subtitle line or two in
    /// a row, lines in the exact order first, then the titles that suit this person best. A clip
    /// starts about four seconds before its line and follows the feed's spoiler and skip rules.
    /// </summary>
    public List<ArcClipDto> Search(User user, string query, int count, int seconds, bool spoilerSafe)
    {
        var words = DialogueText.Words(DialogueText.Normalize(query)).Distinct(StringComparer.Ordinal).ToArray();
        var ordered = DialogueText.Words(DialogueText.Normalize(query));
        if (!Settings.DialogueSearch || words.Length == 0)
        {
            return new List<ArcClipDto>();
        }

        var now = DateTime.UtcNow;
        var (entries, filters) = DialogueIndex.LoadWithFilters(DataFolder);
        if (entries.Count == 0)
        {
            return new List<ArcClipDto>();
        }

        var library = LibraryFor(user, now);
        var data = ArcsStore.Current.Load(user.Id);
        var aggregate = ArcsStore.Current.Aggregate();
        var scores = RankingFor(user, library, data, aggregate, ArcFeeds.ForYou, null, VersionFor(user.Id), now).Scored
            .ToDictionary(t => t.Title.Id, t => t.Score);
        var hidden = data.ActiveHidden(now).Where(h => h.Kind == ArcHideKinds.Hide).Select(h => h.TitleId).ToHashSet();
        var reported = ReportedStarts(data);
        var hits = new List<(DialogueMatch Match, ResolvedClip Clip, double Score)>();
        var opened = 0;
        foreach (var (itemId, entry) in entries)
        {
            // Common words pass every word filter: a search reads a bounded number of titles, and
            // stops once it has plenty of lines with the words in order.
            if (opened >= SearchTitleLimit || hits.Count(h => h.Match.Phrase && !h.Match.Pair) >= count * 2)
            {
                break;
            }

            var track = DialogueIndex.TrackFor(entry.Tracks.Select(t => t.Lang).ToList(), user.SubtitleLanguagePreference);
            if (track < 0 || filters[itemId][track] is not { } filter || !words.All(filter.MightHave))
            {
                continue;
            }

            if (_libraryManager.GetItemById(itemId) is not { } item || item is not (Movie or Episode) || item.RunTimeTicks is not > 0 || !CanUse(user, item))
            {
                continue;
            }

            var titleId = item is Episode e && e.SeriesId != Guid.Empty ? e.SeriesId : item.Id;
            if (hidden.Contains(titleId))
            {
                continue;
            }

            if (item is Episode episode && !SpoilerRules.AllowsEpisode(spoilerSafe, library.PlayedEpisodes.Contains(episode.Id), episode.ParentIndexNumber,
                                                                         episode.IndexNumber, library.Furthest.GetValueOrDefault(episode.SeriesId)))
            {
                continue;
            }

            opened++;
            if (DialogueIndex.Track(DataFolder, itemId, track) is not { } lines)
            {
                continue;
            }

            var matches = DialogueMatcher.Find(lines.Cues, lines.Words, lines.Normalized, ordered);
            if (matches.Count == 0)
            {
                continue;
            }

            var runtime = item.RunTimeTicks!.Value;
            var latest = SpoilerRules.LatestEnd(spoilerSafe, item is Movie, library.PlayedMovies.Contains(item.Id), runtime);
            var segments = ClipInfoFor(item).Segments;
            var perItem = 0;
            var starts = new HashSet<long>();
            foreach (var match in matches.OrderByDescending(m => m.Phrase).ThenBy(m => m.Pair).ThenBy(m => m.StartMs))
            {
                var at = match.StartMs * (ClipPicker.TicksPerSecond / 1000);
                var clip = ClipPicker.AroundLine(runtime, segments, item is Episode, at, match.EndMs * (ClipPicker.TicksPerSecond / 1000), seconds, latest,
                                                 reported.GetValueOrDefault(item.Id));
                if (clip is null || !starts.Add(clip.StartTicks))
                {
                    continue;
                }

                hits.Add((match, new ResolvedClip(item, clip), scores.GetValueOrDefault(titleId)));
                if (++perItem == 3)
                {
                    break;
                }
            }
        }

        return hits.OrderByDescending(h => h.Match.Phrase).ThenBy(h => h.Match.Pair).ThenByDescending(h => h.Score)
            .ThenBy(h => h.Clip.Item.Id).ThenBy(h => h.Clip.Candidate.StartTicks)
            .Take(count)
            .Select(h =>
            {
                var dto = ToDto(user, h.Clip, ArcReasons.Top, null, library, data, aggregate);
                dto.MatchedLine = h.Match.Line;
                dto.MatchedTicks = h.Match.StartMs * (ClipPicker.TicksPerSecond / 1000);
                return dto;
            }).ToList();
    }

    /// <summary>
    /// A title's best scenes: the ones people liked, went on to watch from or shared most (only
    /// clips at least two people responded to), then its own best clips (cold opens first for a
    /// show), within this person's spoiler rules.
    /// </summary>
    public List<ArcClipDto>? Top(User user, Guid titleId, int count, int seconds, bool spoilerSafe)
    {
        var item = _libraryManager.GetItemById(titleId);
        if (item is not (Movie or Series) || !CanUse(user, item))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var library = LibraryFor(user, now);
        var data = ArcsStore.Current.Load(user.Id);
        var aggregate = ArcsStore.Current.Aggregate();
        var reported = ReportedStarts(data);
        var skip = new HashSet<string>(data.Reported, StringComparer.Ordinal);
        skip.UnionWith(ArcsStore.Current.DroppedClips());
        var clips = new List<ResolvedClip>();
        foreach (var (clipId, _) in aggregate.TopClips(titleId))
        {
            if (clips.Count >= count)
            {
                break;
            }

            if (!skip.Contains(clipId) && ClipKey.TryParse(clipId, out var key) && Clip(user, key, seconds) is { } clip && (!spoilerSafe || SpoilerAllows(library, clip)))
            {
                clips.Add(clip);
                skip.Add(clip.Id);
            }
        }

        if (clips.Count < count)
        {
            var sources = item is Series series
                ? EpisodesOf(user, library, series)
                    .Where(e => SpoilerRules.AllowsEpisode(spoilerSafe, library.PlayedEpisodes.Contains(e.Id), e.ParentIndexNumber, e.IndexNumber, library.Furthest.GetValueOrDefault(series.Id)))
                    .OrderBy(e => e.ParentIndexNumber ?? int.MaxValue).ThenBy(e => e.IndexNumber ?? int.MaxValue).Take(40).Cast<BaseItem>().ToList()
                : new List<BaseItem> { item };
            var options = sources.Select(source =>
            {
                var latest = SpoilerRules.LatestEnd(spoilerSafe, source is Movie, library.PlayedMovies.Contains(source.Id), source.RunTimeTicks ?? 0);
                return Candidates(source, seconds, latest, reported.GetValueOrDefault(source.Id))
                    .Select(c => new ResolvedClip(source, c)).Where(c => !skip.Contains(c.Id)).ToList();
            }).ToList();
            clips.AddRange(ArcsTop.Fill(options, count - clips.Count, c => c.Candidate));
        }

        return clips.Select(c => ToDto(user, c, ArcReasons.Top, null, library, data, aggregate)).ToList();
    }

    /// <summary>This person's own Arcs totals.</summary>
    public ArcMyStatsDto MyStats(User user)
    {
        var data = ArcsStore.Current.Load(user.Id);
        var totals = ArcsTotals.For(data);
        var result = new ArcMyStatsDto
        {
            Scenes = totals.Scenes,
            Minutes = totals.Minutes,
            Likes = totals.Likes,
            Shares = totals.Shares,
            WatchFromHere = totals.WatchFromHere,
            TopReaction = totals.TopReaction,
            Since = totals.Since,
        };
        foreach (var (id, scenes) in totals.Titles)
        {
            if (_libraryManager.GetItemById(id) is { } title && title.IsVisible(user))
            {
                result.TopTitles.Add(new ArcTopTitleDto { Id = id.ToString("N"), Name = title.Name ?? string.Empty, Scenes = scenes });
            }

            if (result.TopTitles.Count == 5)
            {
                break;
            }
        }

        foreach (var clipId in totals.Rewatched)
        {
            if (ClipKey.TryParse(clipId, out var key) && Clip(user, key, Settings.DefaultSeconds) is { } clip)
            {
                result.MostRewatched = ToDto(user, clip, ArcReasons.Top, null, LibraryFor(user, DateTime.UtcNow), data, ArcsStore.Current.Aggregate());
                break;
            }
        }

        return result;
    }

    private static Dictionary<Guid, ISet<long>> ReportedStarts(ArcsUserData data)
    {
        return ArcsStore.Current.DroppedClips().Concat(data.Reported).Select(c => ClipKey.TryParse(c, out var k) ? k : default).Where(k => k.ItemId != Guid.Empty)
            .GroupBy(k => k.ItemId).ToDictionary(g => g.Key, g => (ISet<long>)g.Select(k => k.StartTicks).ToHashSet());
    }

    // ----- Events -----

    /// <summary>Works out which film or show an event is about: the show for an episode.</summary>
    public Guid TitleOf(Guid itemId, Guid? seriesId)
    {
        if (seriesId is { } s && s != Guid.Empty)
        {
            return s;
        }

        return _libraryManager.GetItemById(itemId) is Episode { SeriesId: var series } && series != Guid.Empty ? series : itemId;
    }

    public string? NameOf(Guid id) => _libraryManager.GetItemById(id)?.Name;
}
