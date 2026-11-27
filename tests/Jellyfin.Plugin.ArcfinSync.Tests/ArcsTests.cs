using Jellyfin.Data.Enums;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Api.Arcs;
using Jellyfin.Plugin.ArcfinSync.Api.Recommendations;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ArcfinSync.Tests;

public class ArcsClipTests
{
    private const long S = ClipPicker.TicksPerSecond;
    private const long Runtime = 6000 * S;

    private static List<ChapterMark> Chapters(params (long Seconds, string? Name)[] marks) => marks.Select(m => new ChapterMark(m.Seconds * S, m.Name)).ToList();

    [Fact]
    public void ChapterClipsStartTwoSecondsIn()
    {
        var clips = ClipPicker.Candidates(Runtime, Chapters((0, null), (1000, "The Heist"), (2000, null), (5800, null)), new List<SkipRange>(), null, 30, 15, Runtime);
        var heist = clips.Single(c => c.ChapterName == "The Heist");
        Assert.Equal(1002 * S, heist.StartTicks);
        Assert.Equal(1032 * S, heist.EndTicks);
        Assert.Contains(clips, c => c.StartTicks == 2002 * S);
    }

    [Fact]
    public void ShortChaptersGiveShorterClipsAndMarkerChaptersAreDropped()
    {
        var clips = ClipPicker.Candidates(Runtime, Chapters((0, null), (1000, null), (1020, null), (1028, null), (3000, null)), new List<SkipRange>(), null, 30, 15, Runtime);
        var first = clips.Single(c => c.StartTicks == 1002 * S);
        Assert.Equal(18 * S, first.EndTicks - first.StartTicks);
        // Eight seconds is a marker, not a scene.
        Assert.DoesNotContain(clips, c => c.StartTicks == 1022 * S);
    }

    [Fact]
    public void WithoutSegmentsTheStartAndEndOfTheTitleAreSkipped()
    {
        // 6 % of 6000 s is 360 s; the last 8 % starts at 5520 s.
        var clips = ClipPicker.Candidates(Runtime, Chapters((0, null), (300, null), (400, null), (5500, null), (5600, null)), new List<SkipRange>(), null, 30, 15, Runtime);
        Assert.Contains(clips, c => c.StartTicks == 402 * S);
        Assert.All(clips, c => Assert.True(c.StartTicks >= 360 * S && c.EndTicks <= 5520 * S));
    }

    [Fact]
    public void SegmentsAddToTheFixedSkips()
    {
        // A film with only its credits marked still skips its opening logos.
        var segments = new List<SkipRange> { new(1000 * S, 1100 * S), new(5900 * S, Runtime) };
        var clips = ClipPicker.Candidates(Runtime, Chapters((0, null), (1000, null), (1100, null), (5600, null)), segments, null, 30, 15, Runtime);
        Assert.Contains(clips, c => c.StartTicks == 1102 * S);
        Assert.DoesNotContain(clips, c => c.StartTicks < 360 * S || (c.StartTicks < 1100 * S && c.EndTicks > 1000 * S));
    }

    [Fact]
    public void ChaptersNamedForCreditsOrRecapsAreSkipped()
    {
        var clips = ClipPicker.Candidates(Runtime, Chapters((0, null), (1000, "Previously on"), (2000, "Opening Credits"), (3000, "Showdown"), (4000, "End Credits")),
                                          new List<SkipRange>(), null, 30, 15, Runtime);
        Assert.Equal(new[] { "Showdown" }, clips.Where(c => c.ChapterName is not null).Select(c => c.ChapterName));
    }

    [Fact]
    public void WithoutChaptersEvenlySpacedPointsAreUsed()
    {
        var clips = ClipPicker.Candidates(Runtime, Chapters((0, null)), new List<SkipRange>(), null, 30, 15, Runtime);
        Assert.Equal(new[] { 900 * S, 1500 * S, 2100 * S, 2700 * S, 3300 * S, 3900 * S }, clips.Select(c => c.StartTicks));
        Assert.All(clips, c => Assert.Equal(30 * S, c.EndTicks - c.StartTicks));
    }

    [Fact]
    public void SceneIndexStartsAreUsedWhenThereAreNoChapters()
    {
        var clips = ClipPicker.Candidates(Runtime, new List<ChapterMark>(), new List<SkipRange>(), new List<long> { 900 * S, 100 * S, 3000 * S }, 30, 15, Runtime);
        Assert.Equal(new[] { 900 * S, 3000 * S }, clips.Select(c => c.StartTicks));
    }

    [Fact]
    public void ChaptersThatAreAllSkippedFallBack()
    {
        var clips = ClipPicker.Candidates(Runtime, Chapters((0, "Intro"), (5700, "Credits")), new List<SkipRange>(), null, 30, 15, Runtime);
        Assert.Equal(6, clips.Count);
    }

    [Fact]
    public void AnEpisodesColdOpenBeforeItsMarkedIntroIsAClip()
    {
        // As a chapter tool writes them: a prologue, then the intro's marker five times over.
        const long episode = 1300 * S;
        var chapters = Chapters((0, "Prologue"), (195, "Opening"), (195, "Opening"), (195, "Opening"), (195, "Opening"), (195, "Opening"));
        var segments = new List<SkipRange> { new(195 * S, 280 * S, true), new(1250 * S, episode) };
        var clips = ClipPicker.Candidates(episode, chapters, segments, null, 30, 15, episode, isEpisode: true);
        var cold = clips.Single(c => c.IsColdOpen);
        Assert.Equal(2 * S, cold.StartTicks);
        Assert.Equal(32 * S, cold.EndTicks);
        Assert.Equal("Prologue", cold.ChapterName);
        Assert.DoesNotContain(clips, c => c.StartTicks < 280 * S && c.EndTicks > 195 * S);
    }

    [Fact]
    public void AColdOpenStartsAfterARecap()
    {
        const long episode = 1300 * S;
        var segments = new List<SkipRange> { new(0, 40 * S), new(120 * S, 200 * S, true) };
        var cold = ClipPicker.Candidates(episode, new List<ChapterMark>(), segments, null, 30, 15, episode, isEpisode: true).Single(c => c.IsColdOpen);
        Assert.Equal(42 * S, cold.StartTicks);
    }

    [Fact]
    public void FilmsAndUnmarkedEpisodesStillSkipTheirOpening()
    {
        // A film whose chapters are only "Main" at the start and its credits markers.
        const long film = 9425 * S;
        var chapters = Chapters((0, "Main"), (8770, "Ending"), (8770, "Ending"), (8770, "Ending"));
        var segments = new List<SkipRange> { new(8770 * S, film) };
        var clips = ClipPicker.Candidates(film, chapters, segments, null, 30, 15, film);
        Assert.NotEmpty(clips);
        Assert.All(clips, c => Assert.True(c.StartTicks >= 565 * S && !c.IsColdOpen));
        var episode = ClipPicker.Candidates(1300 * S, Chapters((0, "Prologue"), (600, null)), new List<SkipRange>(), null, 30, 15, 1300 * S, isEpisode: true);
        Assert.All(episode, c => Assert.True(c.StartTicks >= 90 * S && !c.IsColdOpen));
    }

    [Fact]
    public void NothingEndsAfterTheSpoilerLimitAndReportedStartsAreDropped()
    {
        var latest = SpoilerRules.LatestEnd(true, true, false, Runtime);
        Assert.Equal(4500 * S, latest);
        var clips = ClipPicker.Candidates(Runtime, Chapters((0, null), (1000, null), (4480, null), (5000, null)), new List<SkipRange>(), null, 30, 15, latest,
                                          new HashSet<long> { 1002 * S });
        Assert.NotEmpty(clips);
        Assert.All(clips, c => Assert.True(c.EndTicks <= latest));
        Assert.DoesNotContain(clips, c => c.StartTicks == 1002 * S || c.StartTicks == 4482 * S);
        Assert.Equal(Runtime, SpoilerRules.LatestEnd(true, true, true, Runtime));
        Assert.Equal(Runtime, SpoilerRules.LatestEnd(false, true, false, Runtime));
        Assert.Equal(Runtime, SpoilerRules.LatestEnd(true, false, false, Runtime));
    }

    [Fact]
    public void SecondsAreKeptInsideTheAdminLimits()
    {
        Assert.Equal(30, ClipPicker.Seconds(null, 15, 60, 30));
        Assert.Equal(15, ClipPicker.Seconds(5, 15, 60, 30));
        Assert.Equal(60, ClipPicker.Seconds(600, 15, 60, 30));
    }

    [Fact]
    public void SceneStartsAreSpreadOutAndAMinuteApart()
    {
        var scenes = Enumerable.Range(0, 600).Select(i => i * 10.0).ToList();
        var starts = ClipPicker.SpreadSceneStarts(scenes, 6000, 8);
        Assert.Equal(8, starts.Count);
        Assert.True(starts.Zip(starts.Skip(1)).All(p => p.Second - p.First >= 60 * S));
        Assert.True(starts[0] >= 360 * S && starts[^1] <= 5460 * S);
        Assert.Empty(ClipPicker.SpreadSceneStarts(new[] { 10.0, 5990.0 }, 6000, 8));
    }

    [Theory]
    [InlineData(true, false, 2, 1, 3, 5, true)]
    [InlineData(true, false, 3, 6, 3, 5, false)]
    [InlineData(true, false, 3, 5, 3, 5, true)]
    [InlineData(true, false, 4, 1, 3, 5, false)]
    [InlineData(true, true, 9, 9, 3, 5, true)]
    [InlineData(false, false, 9, 9, 3, 5, true)]
    public void EpisodesUpToTheFurthestPlayedAreAllowed(bool safe, bool played, int season, int episode, int fs, int fe, bool allowed)
    {
        Assert.Equal(allowed, SpoilerRules.AllowsEpisode(safe, played, season, episode, new SeriesProgress(fs, fe)));
    }

    [Fact]
    public void AShowNotStartedOnlyGivesItsFirstThreeEpisodes()
    {
        Assert.True(SpoilerRules.AllowsEpisode(true, false, 1, 3, null));
        Assert.False(SpoilerRules.AllowsEpisode(true, false, 1, 4, null));
        Assert.False(SpoilerRules.AllowsEpisode(true, false, 2, 1, null));
        Assert.False(SpoilerRules.AllowsEpisode(true, false, 0, 1, null));
        Assert.True(SpoilerRules.AllowsEpisode(false, false, 5, 10, null));
    }

    [Fact]
    public void ClipIdsRoundTrip()
    {
        var key = new ClipKey(Guid.NewGuid(), 12345678900);
        Assert.True(ClipKey.TryParse(key.ToString(), out var back));
        Assert.Equal(key, back);
        Assert.False(ClipKey.TryParse("nope", out _));
        Assert.False(ClipKey.TryParse(key.ItemId.ToString("N") + "--5", out _));
    }

    [Fact]
    public void ShowInfoTimesAreRead()
    {
        var output = "[Parsed_showinfo_2 @ 0x1] n:   0 pts:  20480 pts_time:20      duration:512\n[Parsed_showinfo_2 @ 0x1] n:   1 pts:  40960 pts_time:40.5 duration:512";
        Assert.Equal(new[] { 20.0, 40.5 }, SceneIndex.ParseShowInfo(output));
    }

    [Fact]
    public void RenderArgumentsSeekOnTheInputAndKeepThePictureH264Friendly()
    {
        var args = ClipRenderer.Arguments(new ClipRenderRequest("/m/film.mkv", 125 * S, 30, 720, null), "/c/out.tmp");
        Assert.True(args.IndexOf("-ss") < args.IndexOf("-i"));
        Assert.Equal("125", args[args.IndexOf("-ss") + 1]);
        var vf = args[args.IndexOf("-vf") + 1];
        Assert.Equal("scale=-2:trunc(min(720\\,ih)/2)*2,format=yuv420p", vf);
        Assert.Contains("+faststart", args);
        Assert.Contains("libx264", args);
        Assert.Equal("/c/out.tmp", args[^1]);
    }

    [Fact]
    public void BurnedSubtitlesFollowTheSourceTimeline()
    {
        var args = ClipRenderer.Arguments(new ClipRenderRequest("/m/film.mkv", 40 * S, 8, 480, new BurnSubtitle("/m/film.mkv", 1)), "/c/out.tmp");
        var vf = args[args.IndexOf("-vf") + 1];
        Assert.Equal("scale=-2:trunc(min(480\\,ih)/2)*2,setpts=PTS+40/TB,subtitles=f='/m/film.mkv':si=1,setpts=PTS-STARTPTS,format=yuv420p", vf);
    }

    [Fact]
    public void CacheKeysDependOnEverythingThatChangesTheFile()
    {
        var a = ClipRenderer.CacheKey("x-1", 30, "high", null);
        Assert.Equal(a, ClipRenderer.CacheKey("x-1", 30, "high", null));
        Assert.NotEqual(a, ClipRenderer.CacheKey("x-1", 20, "high", null));
        Assert.NotEqual(a, ClipRenderer.CacheKey("x-1", 30, "low", null));
        Assert.NotEqual(a, ClipRenderer.CacheKey("x-1", 30, "high", 3));
    }

    [Fact]
    public void PruneRemovesTheLeastRecentlyUsedFirst()
    {
        var folder = Path.Combine(Path.GetTempPath(), "arcs-prune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var path = Path.Combine(folder, $"{i}.mp4");
                File.WriteAllBytes(path, new byte[1000]);
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow.AddMinutes(-10 + i));
            }

            ClipRenderer.Prune(folder, 2000);
            Assert.False(File.Exists(Path.Combine(folder, "0.mp4")));
            Assert.True(File.Exists(Path.Combine(folder, "2.mp4")));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

public class ArcsRankingTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
    private static readonly string[] GenreNames = { "Drama", "Comedy", "Action", "Horror", "Documentary" };

    private static TitleFeatures Features(int i, string genre, string? person = null, DateTime? added = null) =>
        new(new Guid(i, 0, 0, new byte[8]), "Title " + i, i % 2 == 0, new[] { genre }, person is null ? Array.Empty<string>() : new[] { person },
            Array.Empty<string>(), Array.Empty<string>(), 7, 2015, added ?? Now.AddYears(-1));

    private static List<ArcTitle> Library(int count) =>
        Enumerable.Range(1, count).Select(i => new ArcTitle(Features(i, GenreNames[i % GenreNames.Length]), false, false, null, 0)).ToList();

    private static RankInput Input(List<ArcTitle> titles, ArcsUserData? data = null, ArcsWeights? weights = null, Dictionary<string, double>? profile = null, string feed = ArcFeeds.ForYou)
    {
        data ??= new ArcsUserData();
        weights ??= new ArcsWeights();
        var p = profile ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var affinity = ArcsRanker.Affinity(data, weights, Now);
        foreach (var (id, value) in affinity)
        {
            if (titles.FirstOrDefault(t => t.Id == id) is { } t)
            {
                Recommender.AddToProfile(p, t.Features, value * 0.6);
            }
        }

        return new RankInput
        {
            Titles = titles,
            Profile = p,
            Affinity = affinity,
            Hidden = data.Hidden,
            RecentViews = ArcsRanker.RecentViews(data, Now),
            Weights = weights,
            Now = Now,
            Feed = feed,
        };
    }

    private static List<(ScoredTitle Title, string Clip)> Page(RankInput input, Random random, int count = 10, Guid? last = null, Dictionary<Guid, int>? before = null)
    {
        var scored = ArcsRanker.Score(input);
        var explore = input.Feed == ArcFeeds.ForYou ? ArcsRanker.ExplorePool(input, scored) : new List<ScoredTitle>();
        var n = 0;
        return ArcsPageBuilder.Build(scored, explore, count, random, input.Weights.Exploration, last, before ?? new Dictionary<Guid, int>(),
                                     t => $"{t.Title.Id:N}-{n++}");
    }

    private static ArcEventRecord Event(ArcTitle title, string kind, double? watched = null) =>
        new() { ClipId = $"{title.Id:N}-100", ItemId = title.Id, TitleId = title.Id, Kind = kind, WatchedSeconds = watched, ClipSeconds = 30, At = Now.AddMinutes(-1) };

    [Fact]
    public void NoTitleTwiceInARowAndAtMostTwoPerPage()
    {
        // Few titles, so the rules have to hold against pressure to repeat.
        var titles = Library(6);
        var input = Input(titles, weights: new ArcsWeights { Exploration = 0 });
        for (var seed = 0; seed < 50; seed++)
        {
            var page = Page(input, new Random(seed));
            Assert.Equal(10, page.Count);
            Assert.True(page.Zip(page.Skip(1)).All(p => p.First.Title.Title.Id != p.Second.Title.Title.Id));
            Assert.True(page.GroupBy(p => p.Title.Title.Id).All(g => g.Count() <= 2));
        }
    }

    [Fact]
    public void OneOrTwoTitlesStillFillAPage()
    {
        foreach (var size in new[] { 1, 2 })
        {
            var input = Input(Library(size), feed: ArcFeeds.ForYou, weights: new ArcsWeights { Exploration = 0 });
            var page = Page(input, new Random(2));
            Assert.Equal(10, page.Count);
            if (size == 2)
            {
                Assert.True(page.Zip(page.Skip(1)).All(p => p.First.Title.Title.Id != p.Second.Title.Title.Id));
            }
        }
    }

    [Fact]
    public void APageEndsWhenNoTitleHasClipsLeft()
    {
        var input = Input(Library(3), weights: new ArcsWeights { Exploration = 0 });
        var scored = ArcsRanker.Score(input);
        var left = 4;
        var page = ArcsPageBuilder.Build(scored, new List<ScoredTitle>(), 10, new Random(1), 0, null, new Dictionary<Guid, int>(),
                                         t => left-- > 0 ? "clip" : null);
        Assert.Equal(4, page.Count);
    }

    [Fact]
    public void APageMixesGenres()
    {
        var titles = Enumerable.Range(1, 30).Select(i => new ArcTitle(Features(i, i <= 20 ? "Drama" : GenreNames[1 + (i % 4)]), false, false, null, 0)).ToList();
        var profile = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["g:Drama"] = 10 };
        var page = Page(Input(titles, profile: profile, weights: new ArcsWeights { Exploration = 0 }), new Random(1));
        Assert.True(page.Count(p => p.Title.Title.Features.Genres.First() == "Drama") <= 4);
    }

    [Fact]
    public void TheNextPageDoesNotStartWithTheTitleThatEndedTheLastOne()
    {
        var titles = Library(3);
        var input = Input(titles, weights: new ArcsWeights { Exploration = 0 });
        var first = Page(input, new Random(3));
        var last = first[^1].Title.Title.Id;
        var second = Page(input, new Random(4), last: last);
        Assert.NotEqual(last, second[0].Title.Title.Id);
    }

    [Fact]
    public void AboutOneSlotInSixExplores()
    {
        var titles = Enumerable.Range(1, 60).Select(i => new ArcTitle(Features(i, i <= 30 ? "Drama" : GenreNames[1 + (i % 4)]), false, false, null, 0)).ToList();
        var profile = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["g:Drama"] = 10 };
        var input = Input(titles, profile: profile);
        var explored = 0;
        var total = 0;
        for (var seed = 0; seed < 200; seed++)
        {
            var page = Page(input, new Random(seed));
            explored += page.Count(p => p.Title.Reason == ArcReasons.Explore);
            total += page.Count;
        }

        var share = explored / (double)total;
        Assert.InRange(share, 0.10, 0.20);

        var none = Input(titles, profile: profile, weights: new ArcsWeights { Exploration = 0 });
        Assert.DoesNotContain(Page(none, new Random(1)), p => p.Title.Reason == ArcReasons.Explore);
    }

    [Fact]
    public void ExploringPicksOutsideTheUsualGenres()
    {
        var titles = Enumerable.Range(1, 40).Select(i => new ArcTitle(Features(i, i <= 20 ? "Drama" : "Horror"), false, false, null, 0)).ToList();
        var profile = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["g:Drama"] = 10 };
        var input = Input(titles, profile: profile);
        var pool = ArcsRanker.ExplorePool(input, ArcsRanker.Score(input));
        Assert.NotEmpty(pool);
        Assert.All(pool, p => Assert.Equal("Horror", p.Title.Features.Genres.First()));
    }

    [Fact]
    public void HiddenTitlesNeverComeBackAndNotNowWaitsAWeek()
    {
        var titles = Library(10);
        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[] { Event(titles[0], ArcEventKinds.Hide), Event(titles[1], ArcEventKinds.NotNow) }, Now);
        var scored = ArcsRanker.Score(Input(titles, data)).Select(s => s.Title.Id).ToList();
        Assert.DoesNotContain(titles[0].Id, scored);
        Assert.DoesNotContain(titles[1].Id, scored);

        var later = Input(titles, data);
        var weekLater = new RankInput { Titles = later.Titles, Profile = later.Profile, Affinity = later.Affinity, Hidden = later.Hidden, Weights = later.Weights, Now = Now.AddDays(8), Feed = later.Feed };
        var back = ArcsRanker.Score(weekLater).Select(s => s.Title.Id).ToList();
        Assert.DoesNotContain(titles[0].Id, back);
        Assert.Contains(titles[1].Id, back);
    }

    [Fact]
    public void FewerFromSeriesHalvesTheScoreForAMonth()
    {
        var titles = Library(4);
        var profile = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["g:" + titles[0].Features.Genres.First()] = 5 };
        var before = ArcsRanker.Score(Input(titles, profile: profile)).Single(s => s.Title.Id == titles[0].Id).Score;
        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[] { Event(titles[0], ArcEventKinds.FewerFromSeries) }, Now);
        var input = Input(titles, data, profile: new Dictionary<string, double>(profile, StringComparer.OrdinalIgnoreCase));
        var after = ArcsRanker.Score(input).Single(s => s.Title.Id == titles[0].Id).Score;
        Assert.Equal(before / 2, after, 6);
        Assert.Equal(Now.AddMinutes(-1).AddDays(30), data.Hidden.Single().Until);
    }

    [Fact]
    public void EventsChangeTheNextPage()
    {
        var titles = Enumerable.Range(1, 20).Select(i => new ArcTitle(Features(i, GenreNames[i % 5], person: i == 7 || i == 12 ? "Same Director" : null), false, false, null, 0)).ToList();
        var target = titles.Single(t => t.Features.Id == new Guid(7, 0, 0, new byte[8]));
        var related = titles.Single(t => t.Features.Id == new Guid(12, 0, 0, new byte[8]));
        var input = Input(titles, weights: new ArcsWeights { Exploration = 0 });
        var rankBefore = ArcsRanker.Score(input).FindIndex(s => s.Title.Id == related.Id);

        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[] { Event(target, ArcEventKinds.Like), Event(target, ArcEventKinds.WatchFromHere), Event(target, ArcEventKinds.View, 30) }, Now);
        var after = Input(titles, data, new ArcsWeights { Exploration = 0 });
        var scored = ArcsRanker.Score(after);
        Assert.Equal(target.Id, scored[0].Title.Id);
        Assert.True(scored.FindIndex(s => s.Title.Id == related.Id) < rankBefore);
        Assert.Contains(Page(after, new Random(1)), p => p.Title.Title.Id == target.Id);

        var disliked = new ArcsUserData();
        ArcsStore.Apply(disliked, new[] { Event(target, ArcEventKinds.Dislike), Event(target, ArcEventKinds.View, 1) }, Now);
        Assert.Equal(target.Id, ArcsRanker.Score(Input(titles, disliked)).Last().Title.Id);
    }

    [Fact]
    public void WatchFromHereCountsMoreThanALike()
    {
        var w = new ArcsWeights();
        var t = Library(1)[0];
        var liked = new ArcsUserData();
        ArcsStore.Apply(liked, new[] { Event(t, ArcEventKinds.Like) }, Now);
        var watched = new ArcsUserData();
        ArcsStore.Apply(watched, new[] { Event(t, ArcEventKinds.WatchFromHere) }, Now);
        Assert.True(ArcsRanker.Affinity(watched, w, Now)[t.Id] > ArcsRanker.Affinity(liked, w, Now)[t.Id]);
    }

    [Fact]
    public void QuickSkipsCountAgainstAndWatchingThroughCountsFor()
    {
        Assert.True(ArcsRanker.ViewValue(1.5, 30) < 0);
        Assert.True(ArcsRanker.ViewValue(29, 30) > 0);
        Assert.Equal(0, ArcsRanker.ViewValue(null, 30));
    }

    [Fact]
    public void AdminWeightsScaleTheSignals()
    {
        var t = Library(1)[0];
        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[] { Event(t, ArcEventKinds.WatchFromHere) }, Now);
        Assert.Equal(0, ArcsRanker.Affinity(data, new ArcsWeights { WatchFromHere = 0 }, Now).GetValueOrDefault(t.Id));
        Assert.Equal(2 * ArcsRanker.Affinity(data, new ArcsWeights(), Now)[t.Id], ArcsRanker.Affinity(data, new ArcsWeights { WatchFromHere = 2 }, Now)[t.Id], 6);
    }

    [Fact]
    public void CollaborativeScoresNeedAtLeastTwoOtherPeople()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var mine = new HashSet<Guid> { a };
        var one = new[] { new HashSet<Guid> { a, b } };
        Assert.Empty(ArcsRanker.Collaborative(mine, one));

        var two = new[] { new HashSet<Guid> { a, b }, new HashSet<Guid> { a, b } };
        var scores = ArcsRanker.Collaborative(mine, two);
        Assert.True(scores[b] > 0);
        Assert.False(scores.ContainsKey(a));
    }

    [Fact]
    public void LikeCountsNeedTwoPeople()
    {
        var clip = $"{Guid.NewGuid():N}-100";
        var title = Guid.NewGuid();
        ArcsUserData Liker() => new() { Likes = { new ArcLike { ClipId = clip, TitleId = title, At = Now } } };
        Assert.Equal(0, ArcsAggregate.From(new[] { (Guid.NewGuid(), Liker()) }).LikeCount(clip));
        Assert.Equal(2, ArcsAggregate.From(new[] { (Guid.NewGuid(), Liker()), (Guid.NewGuid(), Liker()) }).LikeCount(clip));
    }

    [Fact]
    public void ReasonsExplainThePick()
    {
        var seed = new ArcTitle(Features(1, "Drama", person: "Lead Actor"), true, false, Now.AddDays(-2), 0);
        var similar = new ArcTitle(Features(2, "Drama", person: "Lead Actor"), false, false, null, 0);
        var fresh = new ArcTitle(Features(3, "Horror", added: Now.AddDays(-1)), false, false, null, 0);
        var popular = new ArcTitle(Features(4, "Comedy"), false, false, null, 5);
        var going = new ArcTitle(Features(5, "Action"), true, true, Now.AddDays(-1), 0);
        var profile = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Recommender.AddToProfile(profile, seed.Features, 3);
        var input = Input(new List<ArcTitle> { seed, similar, fresh, popular, going }, profile: profile);
        input = new RankInput { Titles = input.Titles, Profile = input.Profile, Seeds = new List<TitleFeatures> { seed.Features }, Weights = input.Weights, Now = Now, Feed = ArcFeeds.ForYou };
        var scored = ArcsRanker.Score(input).ToDictionary(s => s.Title.Id);
        Assert.Equal(ArcReasons.Because, scored[similar.Id].Reason);
        Assert.Equal("Title 1", scored[similar.Id].SeedName);
        Assert.Equal(ArcReasons.New, scored[fresh.Id].Reason);
        Assert.Equal(ArcReasons.Popular, scored[popular.Id].Reason);
        Assert.Equal(ArcReasons.Continue, scored[going.Id].Reason);
    }

    [Fact]
    public void FeedsKeepToTheirTitles()
    {
        var unseen = new ArcTitle(Features(1, "Drama"), false, false, null, 0);
        var going = new ArcTitle(Features(2, "Drama"), true, true, Now.AddDays(-1), 0);
        var done = new ArcTitle(Features(3, "Drama"), true, false, Now.AddDays(-30), 0);
        var titles = new List<ArcTitle> { unseen, going, done };
        Assert.Equal(new[] { going.Id }, ArcsRanker.Score(Input(titles, feed: ArcFeeds.Continue)).Select(s => s.Title.Id));
        Assert.Equal(new[] { unseen.Id }, ArcsRanker.Score(Input(titles, feed: ArcFeeds.Discover)).Select(s => s.Title.Id));
        Assert.Equal(3, ArcsRanker.Score(Input(titles)).Count);
        Assert.All(ArcsRanker.Score(Input(titles, feed: ArcFeeds.Continue)), s => Assert.Equal(ArcReasons.Continue, s.Reason));
    }

    [Fact]
    public void TitlesShownEarlierInTheSessionStepBack()
    {
        var titles = Library(12);
        var input = Input(titles, weights: new ArcsWeights { Exploration = 0 });
        var first = Page(input, new Random(5), count: 3);
        var before = first.GroupBy(p => p.Title.Title.Id).ToDictionary(g => g.Key, g => 5);
        var second = Page(input, new Random(5), count: 3, before: before);
        Assert.Empty(second.Select(p => p.Title.Title.Id).Intersect(before.Keys));
    }
}

public class ArcsSessionTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CursorsRoundTripAndRejectNonsense()
    {
        var cursor = new ArcCursor(-123456, 4);
        Assert.True(ArcCursor.TryDecode(cursor.Encode(), out var back));
        Assert.Equal(cursor, back);
        Assert.False(ArcCursor.TryDecode("not a cursor", out _));
        Assert.False(ArcCursor.TryDecode(Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("a1.x.2")), out _));
    }

    [Fact]
    public void TheSamePageComesBackUntilSomethingChanges()
    {
        var session = new ArcSession(42, Now);
        var page = new ArcPage(1, new List<string> { "a-1", "b-1" }, new List<Guid> { Guid.NewGuid(), Guid.NewGuid() });
        session.Store(0, page);
        var none = new HashSet<string>();
        Assert.Same(page, session.Stored(0, 1, none));
        Assert.Null(session.Stored(0, 2, none));
        Assert.Null(session.Stored(0, 1, new HashSet<string> { "b-1" }));
        Assert.Null(session.Stored(1, 1, none));
    }

    [Fact]
    public void ALaterPageNeverRepeatsEarlierOnes()
    {
        var session = new ArcSession(1, Now);
        var t = Guid.NewGuid();
        session.Store(0, new ArcPage(1, new List<string> { "a-1", "b-1" }, new List<Guid> { t, Guid.NewGuid() }));
        session.Store(1, new ArcPage(1, new List<string> { "c-1" }, new List<Guid> { t }));
        Assert.Equal(new[] { "a-1", "b-1" }, session.ShownBefore(1).OrderBy(x => x));
        Assert.Equal(2, session.TitlesBefore(2)[t]);
        Assert.Equal(t, session.LastTitleBefore(2));

        session.Store(0, new ArcPage(2, new List<string> { "d-1" }, new List<Guid> { Guid.NewGuid() }));
        Assert.Single(session.Pages);
    }

    [Fact]
    public void ThePageRandomnessIsStable()
    {
        var a = new ArcSession(9, Now).RandomFor(3).Next();
        var b = new ArcSession(9, Now).RandomFor(3).Next();
        Assert.Equal(a, b);
        Assert.NotEqual(a, new ArcSession(9, Now).RandomFor(4).Next());
    }
}

public class ArcsStoreTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);

    private static ArcEventRecord E(string kind, string clip = "c-1", Guid? title = null) =>
        new() { ClipId = clip, Kind = kind, TitleId = title ?? Guid.Empty, At = Now };

    [Fact]
    public void LikesAndDislikesReplaceEachOther()
    {
        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[] { E(ArcEventKinds.Like) }, Now);
        Assert.Single(data.Likes);
        ArcsStore.Apply(data, new[] { E(ArcEventKinds.Dislike) }, Now);
        Assert.Empty(data.Likes);
        Assert.Single(data.Dislikes);
        ArcsStore.Apply(data, new[] { E(ArcEventKinds.Undislike), E(ArcEventKinds.Like), E(ArcEventKinds.Unlike) }, Now);
        Assert.Empty(data.Likes);
        Assert.Empty(data.Dislikes);
    }

    [Fact]
    public void OneShareOfAClipCountsPerTenMinutes()
    {
        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[] { E(ArcEventKinds.Share), E(ArcEventKinds.Share) }, Now);
        ArcsStore.Apply(data, new[] { E(ArcEventKinds.Share) }, Now);
        Assert.Single(data.Events);
        ArcsStore.Apply(data, new[] { E(ArcEventKinds.Share, "c-2") }, Now);
        Assert.Equal(2, data.Events.Count);
    }

    [Fact]
    public void EventHistoryIsBounded()
    {
        var data = new ArcsUserData();
        ArcsStore.Apply(data, Enumerable.Range(0, ArcsStore.EventLimit + 50).Select(i => E(ArcEventKinds.View, $"c-{i}")), Now);
        Assert.Equal(ArcsStore.EventLimit, data.Events.Count);
        Assert.Equal("c-50", data.Events[0].ClipId);
    }

    [Fact]
    public void HideAndUnhide()
    {
        var title = Guid.NewGuid();
        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[] { E(ArcEventKinds.Hide, title: title), E(ArcEventKinds.NotNow, title: title) }, Now);
        Assert.Equal(2, data.ActiveHidden(Now).Count);
        Assert.Single(data.ActiveHidden(Now.AddDays(8)));
        ArcsStore.Apply(data, new[] { E(ArcEventKinds.Unhide, title: title) }, Now);
        Assert.Equal(ArcHideKinds.NotNow, data.ActiveHidden(Now).Single().Kind);
    }

    [Fact]
    public void ReportsFromTwoPeopleDropAClipAndSurviveAReset()
    {
        var folder = Path.Combine(Path.GetTempPath(), "arcs-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArcsStore(folder);
            var a = Guid.NewGuid();
            store.Change(a, d => ArcsStore.Apply(d, new[] { E(ArcEventKinds.Report, "x-1") }, Now));
            store.AddReports(a, new[] { "x-1" });
            Assert.Empty(store.DroppedClips());
            store.AddReports(a, new[] { "x-1" });
            Assert.Empty(store.DroppedClips());
            store.AddReports(Guid.NewGuid(), new[] { "x-1" });
            Assert.Contains("x-1", store.DroppedClips());
            store.Reset(a);
            Assert.Empty(store.Load(a).Events);
            Assert.Contains("x-1", store.DroppedClips());
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public void AggregateCountsWithoutTheRequester()
    {
        var folder = Path.Combine(Path.GetTempPath(), "arcs-agg-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArcsStore(folder);
            var title = Guid.NewGuid();
            foreach (var user in new[] { Guid.NewGuid(), Guid.NewGuid() })
            {
                store.Change(user, d => ArcsStore.Apply(d, new[] { E(ArcEventKinds.Like, "y-1", title), E(ArcEventKinds.WatchFromHere, "y-1", title), E(ArcEventKinds.Share, "y-1", title) }, Now));
            }

            var aggregate = store.Aggregate();
            Assert.Equal(2, aggregate.LikeCount("y-1"));
            Assert.Equal(2, aggregate.Shares);
            Assert.Equal(2, aggregate.WatchFromHere);
            Assert.Equal(2, aggregate.PositiveTitles.Count);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }
    }

    [Fact]
    public void AdminSettingsAreChecked()
    {
        var ok = ArcsSettingsRules.Clean(new ArcsSettings { Weights = new ArcsWeights { Exploration = 2, Likes = double.NaN } }, _ => true, out var error);
        Assert.Null(error);
        Assert.Equal(0.5, ok!.Weights.Exploration);
        Assert.Equal(1, ok.Weights.Likes);
        Assert.Null(ArcsSettingsRules.Clean(new ArcsSettings { MinSeconds = 40, MaxSeconds = 20 }, _ => true, out _));
        Assert.Null(ArcsSettingsRules.Clean(new ArcsSettings { DefaultSeconds = 90 }, _ => true, out _));
        Assert.Null(ArcsSettingsRules.Clean(new ArcsSettings { Sharing = "Maybe" }, _ => true, out _));
        var gone = Guid.NewGuid();
        Assert.Empty(ArcsSettingsRules.Clean(new ArcsSettings { DisabledUsers = { gone } }, _ => false, out _)!.DisabledUsers);
    }

    [Fact]
    public void HdrClipsAreToneMappedBeforeSubtitlesAndLabelledStandardRange()
    {
        var request = new ClipRenderRequest("/m/hdr.mkv", 600_000_000L, 20, 720, new BurnSubtitle("/m/hdr.mkv", 0), ClipHdr.Pq);
        var args = ClipRenderer.Arguments(request, "/c/out.tmp", ToneMapper.Tonemapx);
        var vf = args[args.IndexOf("-vf") + 1];
        Assert.Contains("setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc", vf, StringComparison.Ordinal);
        Assert.True(vf.IndexOf("tonemapx=", StringComparison.Ordinal) < vf.IndexOf("subtitles=", StringComparison.Ordinal));
        Assert.True(vf.IndexOf("scale=", StringComparison.Ordinal) < vf.IndexOf("tonemapx=", StringComparison.Ordinal));
        Assert.Equal("bt709", args[args.IndexOf("-color_trc") + 1]);
    }

    [Fact]
    public void HlgAndDolbyVisionGetTheirOwnLabels()
    {
        Assert.Contains(ClipRenderer.ToneMapFilters(ClipHdr.Hlg, ToneMapper.Tonemapx), f => f.Contains("color_trc=arib-std-b67", StringComparison.Ordinal));
        // Dolby Vision alone: the tone mapper applies its metadata, so nothing is labelled first.
        Assert.DoesNotContain(ClipRenderer.ToneMapFilters(ClipHdr.DolbyVision, ToneMapper.Tonemapx), f => f.StartsWith("setparams", StringComparison.Ordinal));
        Assert.Contains(ClipRenderer.ToneMapFilters(ClipHdr.Pq, ToneMapper.Zscale), f => f.StartsWith("tonemap=", StringComparison.Ordinal));
    }

    [Fact]
    public void StandardRangeAndNoToneMapperLeaveThePictureAlone()
    {
        var sdr = ClipRenderer.Arguments(new ClipRenderRequest("/m/a.mkv", 0, 10, 720, null), "/c/o.tmp");
        Assert.DoesNotContain("tonemap", sdr[sdr.IndexOf("-vf") + 1], StringComparison.Ordinal);
        Assert.DoesNotContain("-color_trc", sdr);
        var none = ClipRenderer.Arguments(new ClipRenderRequest("/m/a.mkv", 0, 10, 720, null, ClipHdr.Pq), "/c/o.tmp", ToneMapper.None);
        Assert.DoesNotContain("tonemap", none[none.IndexOf("-vf") + 1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(VideoRangeType.HDR10, null, ClipHdr.Pq)]
    [InlineData(VideoRangeType.DOVIWithHDR10, null, ClipHdr.Pq)]
    [InlineData(VideoRangeType.HLG, null, ClipHdr.Hlg)]
    [InlineData(VideoRangeType.DOVI, null, ClipHdr.DolbyVision)]
    [InlineData(VideoRangeType.DOVIWithSDR, null, ClipHdr.None)]
    [InlineData(VideoRangeType.SDR, null, ClipHdr.None)]
    [InlineData(VideoRangeType.Unknown, "smpte2084", ClipHdr.Pq)]
    public void RangeTypesMapToTheRightConversion(VideoRangeType range, string? transfer, ClipHdr expected)
    {
        Assert.Equal(expected, ArcsService.HdrOf(range, transfer));
    }

    [Fact]
    public void ToneMapperIsReadFromTheFilterList()
    {
        Assert.Equal(ToneMapper.Tonemapx, ClipRenderer.ParseToneMapper(" .S tonemap  V->V x\n .S tonemapx  V->V y\n"));
        Assert.Equal(ToneMapper.Zscale, ClipRenderer.ParseToneMapper(" .S tonemap  V->V x\n .S zscale  V->V y\n"));
        Assert.Equal(ToneMapper.None, ClipRenderer.ParseToneMapper(" .S scale  V->V x\n"));
    }
}

public class ArcsStreamingTests
{
    private const long S = ClipPicker.TicksPerSecond;
    private static readonly HashSet<string> CudaFilters = new() { "scale_cuda", "tonemap_cuda", "tonemapx" };
    private static readonly ClipGpu Gpu = new(new[] { "h264", "hevc" }, Hevc10Bit: true, Vp910Bit: false);

    private static string Vf(List<string> args) => args[args.IndexOf("-vf") + 1];

    [Fact]
    public void HlsArgumentsWriteTwoSecondFmp4SegmentsIntoAGrowingPlaylist()
    {
        var args = ClipRenderer.HlsArguments(new ClipRenderRequest("/m/film.mkv", 125 * S, 30, 720, null), ToneMapper.None);
        Assert.Equal("hls", args[args.IndexOf("-f") + 1]);
        Assert.Equal("2", args[args.IndexOf("-hls_time") + 1]);
        Assert.Equal("event", args[args.IndexOf("-hls_playlist_type") + 1]);
        Assert.Equal("fmp4", args[args.IndexOf("-hls_segment_type") + 1]);
        Assert.Equal("0", args[args.IndexOf("-hls_list_size") + 1]);
        Assert.Equal("expr:gte(t,n_forced*2)", args[args.IndexOf("-force_key_frames") + 1]);
        Assert.Contains("temp_file", args[args.IndexOf("-hls_flags") + 1], StringComparison.Ordinal);
        Assert.Equal("seg_%05d.m4s", args[args.IndexOf("-hls_segment_filename") + 1]);
        Assert.DoesNotContain("+faststart", args);
        Assert.DoesNotContain(args, a => a.Contains('/') && a != "/m/film.mkv" && a != Vf(args));
        Assert.Equal(ClipRenderer.PlaylistName, args[^1]);
        Assert.Equal("scale=-2:trunc(min(720\\,ih)/2)*2,format=yuv420p", Vf(args));
        Assert.Contains("libx264", args);
    }

    [Fact]
    public void HlsNeverBurnsSubtitles()
    {
        var request = new ClipRenderRequest("/m/film.mkv", 40 * S, 8, 480, new BurnSubtitle("/m/film.mkv", 1), ClipHdr.Pq);
        var vf = Vf(ClipRenderer.HlsArguments(request, ToneMapper.Tonemapx));
        Assert.DoesNotContain("subtitles=", vf, StringComparison.Ordinal);
        Assert.DoesNotContain("setpts", vf, StringComparison.Ordinal);
        Assert.Contains("tonemapx=", vf, StringComparison.Ordinal);
    }

    [Fact]
    public void GpuArgumentsDecodeScaleAndEncodeOnTheCard()
    {
        var args = ClipRenderer.HlsArguments(new ClipRenderRequest("/m/film.mkv", 125 * S, 30, 720, null, Codec: "h264"), ToneMapper.None, ClipEncoder.Nvenc);
        Assert.True(args.IndexOf("-hwaccel") < args.IndexOf("-i"));
        Assert.Equal("cuda", args[args.IndexOf("-hwaccel_output_format") + 1]);
        Assert.Equal("cuda=cu:0", args[args.IndexOf("-init_hw_device") + 1]);
        Assert.Equal("scale_cuda=-2:trunc(min(720\\,ih)/2)*2:format=yuv420p", Vf(args));
        Assert.Equal("h264_nvenc", args[args.IndexOf("-c:v") + 1]);
        Assert.Equal("1", args[args.IndexOf("-forced-idr") + 1]);
        Assert.Equal("expr:gte(t,n_forced*2)", args[args.IndexOf("-force_key_frames") + 1]);
        Assert.DoesNotContain("-pix_fmt", args);
        Assert.DoesNotContain("-color_trc", args);
    }

    [Fact]
    public void GpuHdrStaysTenBitUntilTheToneMapper()
    {
        var args = ClipRenderer.HlsArguments(new ClipRenderRequest("/m/hdr.mkv", 0, 20, 720, null, ClipHdr.Pq, "hevc", 10), ToneMapper.None, ClipEncoder.Nvenc);
        Assert.Equal("scale_cuda=-2:trunc(min(720\\,ih)/2)*2:format=p010,tonemap_cuda=format=yuv420p:p=bt709:t=bt709:m=bt709:tonemap=bt2390:peak=100:desat=0", Vf(args));
        Assert.Equal("bt709", args[args.IndexOf("-color_trc") + 1]);
    }

    [Fact]
    public void SharedMp4sUseTheGpuToo()
    {
        var args = ClipRenderer.Arguments(new ClipRenderRequest("/m/film.mkv", 0, 20, 720, null, Codec: "h264"), "/c/o.tmp", ToneMapper.None, ClipEncoder.Nvenc);
        Assert.Contains("h264_nvenc", args);
        Assert.Contains("+faststart", args);
        Assert.DoesNotContain("-force_key_frames", args);
    }

    [Fact]
    public void TheGpuOnlyTakesClipsItCanDoStartToFinish()
    {
        var health = new GpuHealth();
        var now = DateTime.UtcNow;
        ClipEncoder For(ClipRenderRequest r, ClipGpu? gpu = null, IReadOnlySet<string>? filters = null) => ClipRenderer.EncoderFor(r, gpu ?? Gpu, filters ?? CudaFilters, health, now);
        var sdr = new ClipRenderRequest("/m/a.mkv", 0, 10, 720, null, Codec: "h264");
        var hdr = new ClipRenderRequest("/m/a.mkv", 0, 10, 720, null, ClipHdr.Pq, "hevc", 10);

        Assert.Equal(ClipEncoder.Nvenc, For(sdr));
        Assert.Equal(ClipEncoder.Nvenc, For(hdr));
        Assert.Equal(ClipEncoder.Nvenc, For(hdr with { Hdr = ClipHdr.Hlg }));
        Assert.Equal(ClipEncoder.Cpu, ClipRenderer.EncoderFor(sdr, null, CudaFilters, health, now));
        Assert.Equal(ClipEncoder.Cpu, For(sdr with { Subtitle = new BurnSubtitle("/m/a.mkv", 0) }));
        Assert.Equal(ClipEncoder.Cpu, For(hdr with { Hdr = ClipHdr.DolbyVision }));
        Assert.Equal(ClipEncoder.Cpu, For(hdr, Gpu with { Hevc10Bit = false }));
        Assert.Equal(ClipEncoder.Cpu, For(sdr with { Codec = "av1" }));
        Assert.Equal(ClipEncoder.Cpu, For(sdr with { Codec = null }));
        Assert.Equal(ClipEncoder.Cpu, For(hdr, filters: new HashSet<string> { "scale_cuda" }));
        Assert.Equal(ClipEncoder.Nvenc, For(sdr, filters: new HashSet<string> { "scale_cuda" }));
        Assert.Equal(ClipEncoder.Cpu, For(sdr, filters: new HashSet<string>()));

        health.MarkFailed(now);
        Assert.Equal(ClipEncoder.Cpu, For(sdr));
    }

    [Fact]
    public void AGpuFailureIsRememberedForAFewMinutesAndLoggedOnce()
    {
        var health = new GpuHealth();
        var now = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(health.IsDown(now));
        Assert.True(health.MarkFailed(now));
        Assert.False(health.MarkFailed(now.AddSeconds(30)));
        Assert.True(health.IsDown(now.AddMinutes(4)));
        Assert.False(health.IsDown(now.AddSeconds(30) + GpuHealth.Cooldown));
        Assert.True(health.MarkFailed(now.AddMinutes(20)));
    }

    [Fact]
    public void OnlyJellyfinsNvencSettingTurnsTheGpuOn()
    {
        var options = new MediaBrowser.Model.Configuration.EncodingOptions
        {
            HardwareAccelerationType = MediaBrowser.Model.Entities.HardwareAccelerationType.nvenc,
            EnableHardwareEncoding = true,
            HardwareDecodingCodecs = new[] { "h264", "hevc" },
            EnableDecodingColorDepth10Hevc = true,
        };
        var gpu = ArcsService.GpuFrom(options, hasNvenc: true);
        Assert.NotNull(gpu);
        Assert.Contains("hevc", gpu!.DecodeCodecs);
        Assert.True(gpu.Hevc10Bit);
        Assert.Null(ArcsService.GpuFrom(options, hasNvenc: false));
        options.EnableHardwareEncoding = false;
        Assert.Null(ArcsService.GpuFrom(options, hasNvenc: true));
        options.EnableHardwareEncoding = true;
        foreach (var other in new[] { MediaBrowser.Model.Entities.HardwareAccelerationType.vaapi, MediaBrowser.Model.Entities.HardwareAccelerationType.qsv,
                                      MediaBrowser.Model.Entities.HardwareAccelerationType.videotoolbox, MediaBrowser.Model.Entities.HardwareAccelerationType.none })
        {
            options.HardwareAccelerationType = other;
            Assert.Null(ArcsService.GpuFrom(options, hasNvenc: true));
        }
    }

    private const string Growing = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:EVENT\n#EXT-X-INDEPENDENT-SEGMENTS\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:2.000000,\nseg_00000.m4s\n";

    [Fact]
    public void APlaylistIsReadyAtItsFirstSegmentAndCompleteAtEndlist()
    {
        var empty = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:2\n#EXT-X-PLAYLIST-TYPE:EVENT\n";
        Assert.False(ClipRenderer.IsReady(empty));
        Assert.True(ClipRenderer.IsReady(Growing));
        Assert.False(ClipRenderer.IsComplete(Growing));
        Assert.True(ClipRenderer.IsComplete(Growing + "#EXT-X-ENDLIST\n"));
    }

    [Fact]
    public void TheServedPlaylistPointsAtTheFilesBesideIt()
    {
        var key = new string('a', 32);
        var served = ClipRenderer.ServedPlaylist(Growing, key).Split('\n');
        Assert.Contains($"#EXT-X-MAP:URI=\"hls/{key}/init.mp4\"", served);
        Assert.Contains($"hls/{key}/seg_00000.m4s", served);
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:EVENT", served);
        Assert.Equal("#EXT-X-START:TIME-OFFSET=0", served[1]);
        Assert.DoesNotContain(served, l => l == "seg_00000.m4s");

        var finished = ClipRenderer.ServedPlaylist(Growing + "#EXTINF:1.000000,\nseg_00001.m4s\n#EXT-X-ENDLIST\n", key).Split('\n');
        Assert.Contains("#EXT-X-PLAYLIST-TYPE:EVENT", finished);
        Assert.Contains("#EXT-X-ENDLIST", finished);
        Assert.Contains($"hls/{key}/seg_00001.m4s", finished);
    }

    [Fact]
    public void OnlyFilesFfmpegWroteForThisClipAreServed()
    {
        var cache = Path.Combine(Path.GetTempPath(), "arcs-hls-" + Guid.NewGuid().ToString("N"));
        var key = ClipRenderer.CacheKey("x-1", 30, "high", null, "hls");
        var folder = ClipRenderer.HlsFolder(cache, key);
        Directory.CreateDirectory(folder);
        try
        {
            var clipId = new string('b', 32) + "-100";
            File.WriteAllText(Path.Combine(folder, ".clip"), clipId);
            foreach (var name in new[] { "init.mp4", "seg_00000.m4s", "index.m3u8", "seg_00001.m4s.tmp" })
            {
                File.WriteAllBytes(Path.Combine(folder, name), new byte[10]);
            }

            Assert.Equal(Path.Combine(folder, "init.mp4"), ClipRenderer.HlsFile(cache, key, clipId, "init.mp4"));
            Assert.NotNull(ClipRenderer.HlsFile(cache, key, clipId, "seg_00000.m4s"));
            Assert.Null(ClipRenderer.HlsFile(cache, key, clipId, "seg_00002.m4s"));
            Assert.Null(ClipRenderer.HlsFile(cache, key, new string('c', 32) + "-100", "init.mp4"));
            foreach (var bad in new[] { "index.m3u8", ".clip", "seg_00001.m4s.tmp", "../index.m3u8", "..%2Finit.mp4", "seg_0000.m4s", "seg_00000.mp4", "" })
            {
                Assert.Null(ClipRenderer.HlsFile(cache, key, clipId, bad));
            }

            foreach (var badKey in new[] { "..", key.ToUpperInvariant(), key[..31], key + "0", "../" + key[3..] })
            {
                Assert.Null(ClipRenderer.HlsFile(cache, badKey, clipId, "init.mp4"));
            }
        }
        finally
        {
            Directory.Delete(cache, true);
        }
    }

    [Fact]
    public void CacheKeysCarryTheFormatAndTheRenderVersion()
    {
        var mp4 = ClipRenderer.CacheKey("x-1", 30, "high", null);
        Assert.NotEqual(mp4, ClipRenderer.CacheKey("x-1", 30, "high", null, "hls"));
        Assert.Matches("^[0-9a-f]{32}$", mp4);
        // The key the previous way of making clips used, so none of those files are served now.
        var old = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("x-1|30|high|-|r2")))[..32].ToLowerInvariant();
        Assert.NotEqual(old, mp4);
    }

    [Fact]
    public void HlsFoldersCountTowardTheCacheAndArePrunedWithTheMp4s()
    {
        var cache = Path.Combine(Path.GetTempPath(), "arcs-prune-hls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            var oldFolder = ClipRenderer.HlsFolder(cache, new string('1', 32));
            var newFolder = ClipRenderer.HlsFolder(cache, new string('2', 32));
            foreach (var (folder, minutes) in new[] { (oldFolder, -30), (newFolder, -1) })
            {
                Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, "seg_00000.m4s"), new byte[1000]);
                File.WriteAllText(Path.Combine(folder, "index.m3u8"), "#EXTM3U\n#EXT-X-ENDLIST\n");
                File.SetLastAccessTimeUtc(Path.Combine(folder, "index.m3u8"), DateTime.UtcNow.AddMinutes(minutes));
            }

            var mp4 = Path.Combine(cache, "c.mp4");
            File.WriteAllBytes(mp4, new byte[1000]);
            File.SetLastAccessTimeUtc(mp4, DateTime.UtcNow.AddMinutes(-10));
            var size = ClipRenderer.CacheSize(cache);
            Assert.True(size > 3000);

            ClipRenderer.Prune(cache, size - 500);
            Assert.False(Directory.Exists(oldFolder));
            Assert.True(File.Exists(mp4));
            Assert.True(Directory.Exists(newFolder));

            ClipRenderer.Clear(cache);
            Assert.False(Directory.Exists(newFolder));
            Assert.False(File.Exists(mp4));
            Assert.Equal(0, ClipRenderer.CacheSize(cache));
        }
        finally
        {
            Directory.Delete(cache, true);
        }
    }

    /// <summary>A real render with the machine's ffmpeg, when it has one: the playlist is served at its first segment and finishes as a full clip.</summary>
    [Fact]
    public async System.Threading.Tasks.Task ARealHlsRenderIsServedAtItsFirstSegmentAndFinishes()
    {
        var ffmpeg = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Select(d => Path.Combine(d, "ffmpeg")).FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "arcs-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "source.mkv");
            var make = System.Diagnostics.Process.Start(ffmpeg, new[]
            {
                "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=1280x720:r=24", "-f", "lavfi", "-i", "sine=f=440",
                "-t", "12", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-y", source
            });
            await make.WaitForExitAsync();
            Assert.Equal(0, make.ExitCode);

            var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
            var context = new ClipRenderContext(ffmpeg, Path.Combine(root, "cache"), 1L << 30, null);
            var clipId = new string('d', 32) + "-20000000";
            var request = new ClipRenderRequest(source, 2 * S, 7, 480, null, Codec: "h264");
            var key = ClipRenderer.CacheKey(clipId, 7, "low", null, "hls");

            var first = await ClipRenderer.PlaylistAsync(context, key, clipId, request, logger, TimeSpan.FromSeconds(10), default);
            Assert.NotNull(first);
            Assert.True(ClipRenderer.IsReady(first!));
            Assert.True(await ClipRenderer.EnsureHlsAsync(context, key, clipId, request, logger));

            var done = await ClipRenderer.PlaylistAsync(context, key, clipId, request, logger, TimeSpan.FromSeconds(1), default);
            Assert.True(ClipRenderer.IsComplete(done!));
            var segments = done!.Split('\n').Where(l => l.StartsWith("seg_", StringComparison.Ordinal)).ToList();
            Assert.Equal(4, segments.Count);
            Assert.All(segments, s => Assert.NotNull(ClipRenderer.HlsFile(context.CacheFolder, key, clipId, s)));
            Assert.NotNull(ClipRenderer.HlsFile(context.CacheFolder, key, clipId, "init.mp4"));
            Assert.All(done.Split('\n').Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal)).SkipLast(1), l => Assert.Equal("#EXTINF:2.000000,", l));

            // Left half made by a render that died: started again rather than served as a playlist that never ends.
            var staleKey = ClipRenderer.CacheKey(clipId, 6, "low", null, "hls");
            var stale = ClipRenderer.HlsFolder(context.CacheFolder, staleKey);
            Directory.CreateDirectory(stale);
            File.WriteAllText(Path.Combine(stale, ".clip"), clipId);
            File.WriteAllText(Path.Combine(stale, ClipRenderer.PlaylistName), "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:EVENT\n#EXTINF:2.000000,\nseg_00000.m4s\n");
            Assert.True(await ClipRenderer.EnsureHlsAsync(context, staleKey, clipId, request with { Seconds = 6 }, logger));
            Assert.True(ClipRenderer.IsComplete(File.ReadAllText(Path.Combine(stale, ClipRenderer.PlaylistName))));

            // A source ffmpeg can't read: no playlist, and nothing left behind to be served later.
            var brokenKey = ClipRenderer.CacheKey(clipId, 5, "low", null, "hls");
            var broken = request with { SourcePath = Path.Combine(root, "missing.mkv"), Seconds = 5 };
            Assert.Null(await ClipRenderer.PlaylistAsync(context, brokenKey, clipId, broken, logger, TimeSpan.FromSeconds(10), default));
            Assert.False(Directory.Exists(ClipRenderer.HlsFolder(context.CacheFolder, brokenKey)));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
