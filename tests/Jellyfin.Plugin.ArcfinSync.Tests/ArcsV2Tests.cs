using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Plugin.ArcfinSync.Api.Arcs;
using Jellyfin.Plugin.ArcfinSync.Api.Recommendations;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ArcfinSync.Tests;

internal static class V2
{
    public static readonly DateTime Now = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);

    public static Guid Id(int i) => new(i, 0, 0, new byte[8]);

    public static ArcEventRecord E(string kind, string clip = "c-1", Guid? title = null, string? reaction = null, DateTime? at = null, double? watched = null, double? length = null) =>
        new() { ClipId = clip, Kind = kind, TitleId = title ?? Guid.Empty, Reaction = reaction, At = at ?? Now, WatchedSeconds = watched, ClipSeconds = length };

    public static string Folder(string name) => Path.Combine(Path.GetTempPath(), $"arcs-{name}-{Guid.NewGuid():N}");

    public static void Remove(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, true);
        }
    }
}

public class ArcsReactionTests
{
    [Fact]
    public void AReactionReplacesTheLastOneAndNoneClearsIt()
    {
        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[] { V2.E(ArcEventKinds.React, reaction: "funny", title: V2.Id(1)) }, V2.Now);
        ArcsStore.Apply(data, new[] { V2.E(ArcEventKinds.React, reaction: "wow", title: V2.Id(1)) }, V2.Now);
        Assert.Equal("wow", data.Reactions.Single().Reaction);
        ArcsStore.Apply(data, new[] { V2.E(ArcEventKinds.React, reaction: "none") }, V2.Now);
        Assert.Empty(data.Reactions);
    }

    [Fact]
    public void ReactionCountsNeedTwoPeopleAndAlwaysHaveEveryKind()
    {
        static ArcsUserData Reacted(string reaction)
        {
            var data = new ArcsUserData();
            ArcsStore.Apply(data, new[] { V2.E(ArcEventKinds.React, "x-1", V2.Id(1), reaction) }, V2.Now);
            return data;
        }

        var one = ArcsAggregate.From(new[] { (Guid.NewGuid(), Reacted("funny")), (Guid.NewGuid(), Reacted("sad")) });
        Assert.Equal(new Dictionary<string, int> { ["funny"] = 0, ["wow"] = 0, ["sad"] = 0 }, one.ReactionCounts("x-1"));

        var two = ArcsAggregate.From(new[] { (Guid.NewGuid(), Reacted("funny")), (Guid.NewGuid(), Reacted("funny")), (Guid.NewGuid(), Reacted("sad")) });
        Assert.Equal(2, two.ReactionCounts("x-1")["funny"]);
        Assert.Equal(0, two.ReactionCounts("x-1")["sad"]);
        Assert.Equal(3, two.ReactionCounts("nothing").Count);
    }

    [Fact]
    public void AReactionIsAPositiveSignal()
    {
        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[] { V2.E(ArcEventKinds.React, "x-1", V2.Id(1), "sad") }, V2.Now);
        Assert.True(ArcsRanker.Affinity(data, new ArcsWeights(), V2.Now)[V2.Id(1)] > 1);
    }

    [Fact]
    public void StartingAnEpisodeCountsLikeWatchingOnAndForTheTopScenes()
    {
        var w = new ArcsWeights();
        Assert.Equal(ArcsRanker.EventValue(V2.E(ArcEventKinds.WatchFromHere), w), ArcsRanker.EventValue(V2.E(ArcEventKinds.StartEpisode), w));
        var title = V2.Id(5);
        ArcsUserData Started()
        {
            var d = new ArcsUserData();
            ArcsStore.Apply(d, new[] { V2.E(ArcEventKinds.StartEpisode, "s-1", title) }, V2.Now);
            return d;
        }

        var aggregate = ArcsAggregate.From(new[] { (Guid.NewGuid(), Started()), (Guid.NewGuid(), Started()) });
        Assert.Equal("s-1", aggregate.TopClips(title).Single().ClipId);
        Assert.Equal(2, aggregate.WatchFromHere);
    }

    [Fact]
    public void TopClipsOnlyCountClipsTwoPeopleRespondedTo()
    {
        var title = V2.Id(9);
        var alone = new ArcsUserData();
        ArcsStore.Apply(alone, new[] { V2.E(ArcEventKinds.Like, "a-1", title), V2.E(ArcEventKinds.Share, "a-1", title), V2.E(ArcEventKinds.WatchFromHere, "a-1", title) }, V2.Now);
        var liker = new ArcsUserData();
        ArcsStore.Apply(liker, new[] { V2.E(ArcEventKinds.Like, "b-1", title) }, V2.Now);
        var sharer = new ArcsUserData();
        ArcsStore.Apply(sharer, new[] { V2.E(ArcEventKinds.Share, "b-1", title) }, V2.Now);
        var aggregate = ArcsAggregate.From(new[] { (Guid.NewGuid(), alone), (Guid.NewGuid(), liker), (Guid.NewGuid(), sharer) });
        Assert.Equal(new[] { "b-1" }, aggregate.TopClips(title).Select(t => t.ClipId));
    }
}

public class ArcsMoodTests
{
    private static ArcTitle Title(int i, params string[] genres) =>
        new(new TitleFeatures(V2.Id(i), "T" + i, false, genres, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), 7, 2015, V2.Now.AddYears(-1)),
            false, false, null, 0);

    [Fact]
    public void AMoodKeepsToItsGenres()
    {
        var titles = new List<ArcTitle> { Title(1, "Comedy"), Title(2, "Horror"), Title(3, "Animation"), Title(4, "Sci-Fi & Fantasy") };
        var input = new RankInput { Titles = titles, Now = V2.Now, Mood = ArcMoods.Funny };
        Assert.Equal(new[] { V2.Id(1), V2.Id(3) }, ArcsRanker.Score(input).Select(s => s.Title.Id).OrderBy(i => i));
        Assert.Equal(V2.Id(4), ArcsRanker.Score(new RankInput { Titles = titles, Now = V2.Now, Mood = ArcMoods.SciFi }).Single().Title.Id);
        Assert.Equal(4, ArcsRanker.Score(new RankInput { Titles = titles, Now = V2.Now }).Count);
        Assert.True(ArcMoods.Matches(ArcMoods.Scary, new[] { "thriller" }));
        Assert.False(ArcMoods.IsKnown("sleepy"));
    }

    [Fact]
    public void MoodBoostsFromTheirOwnReactionsAndFromTwoOthers()
    {
        var me = Guid.NewGuid();
        var mine = new ArcsUserData();
        ArcsStore.Apply(mine, new[] { V2.E(ArcEventKinds.React, "m-1", V2.Id(1), "funny") }, V2.Now);
        ArcsUserData Other(Guid title)
        {
            var d = new ArcsUserData();
            ArcsStore.Apply(d, new[] { V2.E(ArcEventKinds.React, "o-1", title, "funny") }, V2.Now);
            return d;
        }

        var oneOther = ArcsAggregate.From(new[] { (me, mine), (Guid.NewGuid(), Other(V2.Id(2))) });
        var boosts = ArcsRanker.MoodBoosts(ArcMoods.Funny, mine, oneOther, me, new[] { V2.Id(1), V2.Id(2) });
        Assert.True(boosts[V2.Id(1)] > 0);
        Assert.False(boosts.ContainsKey(V2.Id(2)));

        var twoOthers = ArcsAggregate.From(new[] { (me, mine), (Guid.NewGuid(), Other(V2.Id(2))), (Guid.NewGuid(), Other(V2.Id(2))) });
        Assert.True(ArcsRanker.MoodBoosts(ArcMoods.Funny, mine, twoOthers, me, new[] { V2.Id(2) })[V2.Id(2)] > 0);
        Assert.Empty(ArcsRanker.MoodBoosts(ArcMoods.Action, mine, twoOthers, me, new[] { V2.Id(1), V2.Id(2) }));
        Assert.Empty(ArcsRanker.MoodBoosts(ArcMoods.FeelGood, mine, twoOthers, me, new[] { V2.Id(1) }));
    }

    [Fact]
    public void TheBoostLiftsATitleInItsMood()
    {
        var titles = new List<ArcTitle> { Title(1, "Comedy"), Title(2, "Comedy") };
        var boosted = new RankInput { Titles = titles, Now = V2.Now, Mood = ArcMoods.Funny, MoodBoost = new Dictionary<Guid, double> { [V2.Id(2)] = 1 } };
        Assert.Equal(V2.Id(2), ArcsRanker.Score(boosted)[0].Title.Id);
    }
}

public class ArcsRequestTests
{
    private static RequestHit Hit(int id, string type = "movie", bool adult = false, int? status = null, bool requests = false) =>
        new(type, id, "Film " + id, null, 2020, 7, "/p.jpg", "/b.jpg", adult, status, requests);

    [Fact]
    public void RequestIdsRoundTrip()
    {
        Assert.True(RequestKey.TryParse("req-tv-1399", out var key));
        Assert.Equal(new RequestKey("tv", 1399), key);
        Assert.Equal("req-movie-12", new RequestKey("movie", 12).ToString());
        Assert.False(RequestKey.TryParse("req-book-1", out _));
        Assert.False(RequestKey.TryParse("req-movie-0", out _));
        Assert.False(ClipKey.TryParse("req-movie-12", out _));
    }

    [Fact]
    public void RequestSlotsAreAboutOneInEightNeverFirstAndNeverTwoInARow()
    {
        foreach (var seed in new[] { 1, 42, -7, 99999 })
        {
            var positions = new List<int>();
            var previous = false;
            for (var page = 0; page < 20; page++)
            {
                var slots = RequestRules.Slots(seed, page * 10, 10, 0, previous);
                positions.AddRange(slots.Select(s => (page * 10) + s));
                previous = slots.Contains(9);
            }

            Assert.DoesNotContain(0, positions);
            Assert.Equal(200 / RequestRules.Every, positions.Count);
            Assert.All(positions.Zip(positions.Skip(1)), p => Assert.True(p.Second - p.First > 1));
        }

        Assert.All(RequestRules.Slots(3, 0, 10, 2, false), s => Assert.True(s >= 2));
    }

    [Fact]
    public void APageStartingAfterARequestDoesNotStartWithOne()
    {
        for (var seed = 0; seed < 200; seed++)
        {
            for (var start = 1; start < 40; start++)
            {
                Assert.DoesNotContain(0, RequestRules.Slots(seed, start, 5, 0, true));
            }
        }
    }

    [Fact]
    public void InterleavePutsSentFirstAndRequestsAtTheirSlots()
    {
        var page = RequestRules.Interleave(new[] { "s1" }, new[] { "a", "b", "c", "d" }, new[] { "r1" }, new HashSet<int> { 0, 3 }, 6);
        Assert.Equal(new[] { "s1", "a", "b", "r1", "c", "d" }, page);
        Assert.Equal(new[] { "a", "b" }, RequestRules.Interleave(Array.Empty<string>(), new[] { "a", "b" }, Array.Empty<string>(), new HashSet<int> { 1 }, 5));
    }

    [Fact]
    public void OnlyTitlesNotHereAndNotRequestedAreUsed()
    {
        var library = new HashSet<string> { "movie:5" };
        var mine = new HashSet<string> { "req-movie-6" };
        Assert.True(RequestRules.Usable(Hit(1), library, mine));
        Assert.True(RequestRules.Usable(Hit(1, status: 1), library, mine));
        Assert.True(RequestRules.Usable(Hit(1, status: 7), library, mine));
        Assert.False(RequestRules.Usable(Hit(5), library, mine));
        Assert.True(RequestRules.Usable(Hit(5, "tv"), library, mine));
        Assert.False(RequestRules.Usable(Hit(6), library, mine));
        Assert.False(RequestRules.Usable(Hit(2, adult: true), library, mine));
        Assert.False(RequestRules.Usable(Hit(3, requests: true), library, mine));
        foreach (var status in new[] { 2, 3, 4, 5, 6 })
        {
            Assert.False(RequestRules.Usable(Hit(4, status: status), library, mine));
        }
    }

    [Fact]
    public void TheTrailerIsYouTubeInTheirLanguageThenEnglishOfficialFirst()
    {
        var videos = new List<TmdbVideo>
        {
            new("teaser", "YouTube", "Teaser", true, "de"),
            new("vimeo", "Vimeo", "Trailer", true, "de"),
            new("enfan", "YouTube", "Trailer", false, "en"),
            new("enoff", "YouTube", "Trailer", true, "en"),
            new("defan", "YouTube", "Trailer", false, "de"),
        };
        Assert.Equal("defan", RequestRules.Trailer(videos, "de"));
        Assert.Equal("enoff", RequestRules.Trailer(videos, "fr"));
        Assert.Null(RequestRules.Trailer(videos.Where(v => v.Language == "de").ToList(), "fr"));
        Assert.Equal("any", RequestRules.Trailer(new[] { new TmdbVideo("any", "YouTube", "Trailer", false, null) }, "fr"));
        Assert.Null(RequestRules.Trailer(Array.Empty<TmdbVideo>(), "en"));
    }

    [Fact]
    public void ParentalLimitsApplyWhereTheCertificationIsKnown()
    {
        Assert.True(RequestRules.RatingAllowed(10, true, 13, false));
        Assert.False(RequestRules.RatingAllowed(17, true, 13, false));
        Assert.True(RequestRules.RatingAllowed(17, true, null, true));
        Assert.True(RequestRules.RatingAllowed(null, false, 13, false));
        Assert.False(RequestRules.RatingAllowed(null, false, 13, true));
    }

    [Fact]
    public void RecommendationsFromSeveralSeedsAddUp()
    {
        var a = new RequestSeed(V2.Id(1), "A", "movie", 10, 2);
        var b = new RequestSeed(V2.Id(2), "B", "movie", 11, 1);
        var merged = RequestRules.Merge(new[]
        {
            (a, (IReadOnlyList<RequestHit>)new[] { Hit(100), Hit(200) }),
            (b, (IReadOnlyList<RequestHit>)new[] { Hit(200), Hit(300) }),
        });
        Assert.Equal(200, merged[0].Hit.TmdbId);
        Assert.Equal("A", merged[0].Seed.Name);
        Assert.Equal(new[] { 200, 100, 300 }, merged.Select(m => m.Hit.TmdbId));
    }

    [Fact]
    public void SeerrAndTmdbAnswersAreRead()
    {
        const string seerr = """
        { "page": 1, "results": [
          { "id": 603, "mediaType": "movie", "title": "The Matrix", "overview": "o", "posterPath": "/m.jpg", "backdropPath": "/b.jpg",
            "voteAverage": 8.24, "releaseDate": "1999-03-31", "adult": false, "mediaInfo": { "status": 5, "requests": [] } },
          { "id": 1, "name": "A Show", "firstAirDate": "2010-01-01" },
          { "id": 2, "mediaType": "person", "name": "Someone" },
          { "id": 3, "mediaType": "movie", "title": "Pending", "mediaInfo": { "status": 1, "requests": [ { "id": 9 } ] } }
        ] }
        """;
        using (var doc = JsonDocument.Parse(seerr))
        {
            var hits = RequestParsing.Hits(doc.RootElement, "tv");
            Assert.Equal(3, hits.Count);
            Assert.Equal(new RequestHit("movie", 603, "The Matrix", "o", 1999, 8.2f, "/m.jpg", "/b.jpg", false, 5, false), hits[0]);
            Assert.Equal("tv", hits[1].MediaType);
            Assert.Equal(2010, hits[1].Year);
            Assert.True(hits[2].HasRequests);
        }

        const string tmdb = """
        { "id": 603, "runtime": 136, "genres": [ { "id": 28, "name": "Action" } ],
          "videos": { "results": [ { "key": "abc", "site": "YouTube", "type": "Trailer", "official": true, "iso_639_1": "en" } ] },
          "release_dates": { "results": [
            { "iso_3166_1": "DE", "release_dates": [ { "certification": "16", "type": 3 } ] },
            { "iso_3166_1": "US", "release_dates": [ { "certification": "", "type": 1 }, { "certification": "R", "type": 3 } ] } ] } }
        """;
        using (var doc = JsonDocument.Parse(tmdb))
        {
            var details = RequestParsing.Details(doc.RootElement, "movie", "GB");
            Assert.Equal("R", details.Certification);
            Assert.Equal("16", RequestParsing.Details(doc.RootElement, "movie", "DE").Certification);
            Assert.Equal(136, details.RuntimeMinutes);
            Assert.Equal(new[] { "Action" }, details.Genres);
            Assert.Equal("abc", RequestRules.Trailer(details.Videos, "en"));
        }

        const string seerrTv = """
        { "id": 1399, "episodeRunTime": [ 0, 55 ], "genres": [ { "name": "Drama" } ],
          "relatedVideos": [ { "key": "xyz", "site": "YouTube", "type": "Trailer", "name": "Trailer" } ],
          "contentRatings": { "results": [ { "iso_3166_1": "US", "rating": "TV-MA" } ] },
          "mediaInfo": { "status": 2 } }
        """;
        using (var doc = JsonDocument.Parse(seerrTv))
        {
            var details = RequestParsing.Details(doc.RootElement, "tv", "US");
            Assert.Equal("TV-MA", details.Certification);
            Assert.Equal(55, details.RuntimeMinutes);
            Assert.Equal(2, details.MediaStatus);
            Assert.Equal("xyz", RequestRules.Trailer(details.Videos, "en"));
        }
    }

    [Fact]
    public void EventsOnARequestClipStayOffTheLibraryTitle()
    {
        var seed = V2.Id(3);
        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[]
        {
            V2.E(ArcEventKinds.View, "req-movie-1"),
            V2.E(ArcEventKinds.Request, "req-movie-1", seed),
            V2.E(ArcEventKinds.Hide, "req-movie-2"),
            V2.E(ArcEventKinds.NotNow, "req-tv-3"),
        }, V2.Now);
        Assert.Equal(new[] { "req-movie-1" }, data.Requested);
        Assert.Equal(new[] { "req-movie-2", "req-tv-3" }, data.DismissedRequests);
        Assert.Empty(data.Hidden);
        Assert.Empty(data.Likes);
        Assert.True(ArcsRanker.Affinity(data, new ArcsWeights(), V2.Now)[seed] > 0);
        Assert.True(ArcsService.MyRequests(data, V2.Now).SetEquals(new[] { "req-movie-1", "req-movie-2", "req-tv-3" }));
        Assert.Equal(0, ArcsAggregate.From(new[] { (Guid.NewGuid(), data) }).DistinctClips);
    }

    [Fact]
    public void RequestClipsKeepToTheMoodAndLeaveOutWhatTheyDid()
    {
        RequestCandidate C(int id, params string[] genres) => new("movie", id, "F" + id, null, null, genres, null, null, "k", null, null, 0, Guid.Empty, "S");
        var pool = new[] { C(1, "Comedy"), C(2, "Horror"), C(3, "Comedy", "Drama"), C(4, "Comedy") };
        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[] { V2.E(ArcEventKinds.Request, "req-movie-3") }, V2.Now);
        var used = new HashSet<string> { "req-movie-4" };
        Assert.Equal(new[] { 1 }, ArcsService.RequestsFor(data, pool, used, V2.Now, ArcMoods.Funny).Select(c => c.TmdbId));
        Assert.Equal(new[] { 1, 2 }, ArcsService.RequestsFor(data, pool, used, V2.Now, null).Select(c => c.TmdbId));
        Assert.Equal("Episode", ArcsService.RequestDto(C(5) with { MediaType = "tv" }).Type);
    }

    [Fact]
    public void ARequestClipCarriesItsTitle()
    {
        var dto = ArcsService.RequestDto(new RequestCandidate("tv", 1399, "A Show", "o", 2011, new[] { "Drama" }, 8.4f, "TV-MA", "xyz", "/p.jpg", "/b.jpg",
                                                              55 * 60 * ClipPicker.TicksPerSecond, V2.Id(1), "Seed Show"));
        Assert.Equal("req-tv-1399", dto.Id);
        Assert.Equal(string.Empty, dto.ItemId);
        Assert.Equal("request", dto.Kind);
        Assert.Equal("tv", dto.MediaType);
        Assert.Equal(1399, dto.TmdbId);
        Assert.Equal("xyz", dto.TrailerYouTubeKey);
        Assert.Equal(0, dto.StartTicks);
        Assert.Equal(0, dto.EndTicks);
        Assert.Equal("Seed Show", dto.Reason.SeedName);
        Assert.Equal("request", dto.Reason.Kind);
        Assert.Equal(3, dto.Reactions.Count);
    }
}

public class ArcsDialogueTests
{
    private const long S = ClipPicker.TicksPerSecond;

    [Fact]
    public void TextIsComparedWithoutCaseAccentsOrApostrophes()
    {
        Assert.Equal("cafe creme dont stop", DialogueText.Normalize("Café — Crème! Don't   stop…"));
        Assert.Equal("strasse", DialogueText.Normalize("STRASSE"));
        Assert.Equal("strasse", DialogueText.Normalize("Straße"));
        Assert.Equal("Hello there", DialogueText.Clean("<i>Hello</i>\\N{\\an8}there"));
    }

    [Fact]
    public void SrtIsRead()
    {
        const string srt = "1\r\n00:00:01,000 --> 00:00:03,500\r\n<i>Cool cool</i>\r\ncool.\r\n\r\n2\r\n00:01:02.250 --> 00:01:04,000 X1:0\r\nNoice.\r\n\r\n3\r\n00:01:05,000 --> 00:01:06,000\r\n♪\r\n";
        var cues = DialogueText.ParseSrt(srt);
        Assert.Equal(2, cues.Count);
        Assert.Equal(new SubtitleCue(1000, 3500, "Cool cool cool."), cues[0]);
        Assert.Equal(62250, cues[1].StartMs);
    }

    private static LoadedTrack Track(params (int Start, string Text)[] lines) => LoadedTrack.From(new DialogueTrack
    {
        Start = lines.Select(l => l.Start).ToList(),
        Dur = lines.Select(_ => 2000).ToList(),
        Text = lines.Select(l => l.Text).ToList(),
    });

    private static List<DialogueMatch> Find(LoadedTrack track, string query) =>
        DialogueMatcher.Find(track.Cues, track.Words, track.Normalized, DialogueText.Words(DialogueText.Normalize(query)));

    [Fact]
    public void AllWordsInOneLineOrTwoInARow()
    {
        var track = Track((1000, "I am the one who"), (3000, "knocks."), (6000, "Say my name."), (9000, "Who knocks?"));
        var knocks = Find(track, "the one who KNOCKS");
        var pair = Assert.Single(knocks);
        Assert.True(pair.Pair);
        Assert.True(pair.Phrase);
        Assert.Equal(1000, pair.StartMs);
        Assert.Equal("I am the one who knocks.", pair.Line);

        var both = Find(track, "who knocks");
        Assert.Equal(2, both.Count);
        Assert.Contains(both, m => !m.Pair && m.Phrase && m.StartMs == 9000);

        Assert.Empty(Find(track, "say knocks one"));
        Assert.Empty(Find(track, "name who am"));
    }

    [Fact]
    public void WordsOutOfOrderStillMatchButNotAsAPhrase()
    {
        var match = Assert.Single(Find(Track((0, "Cool, cool, cool. No doubt.")), "doubt cool"));
        Assert.False(match.Phrase);
        Assert.True(Assert.Single(Find(Track((0, "Crème brûlée, please")), "creme brulee")).Phrase);
    }

    [Fact]
    public void TheWordFilterKeepsEveryWordAndSurvivesStorage()
    {
        var words = Enumerable.Range(0, 2000).Select(i => "word" + i).ToList();
        var filter = WordFilter.For(words.Count);
        words.ForEach(filter.Add);
        var back = WordFilter.FromBase64(filter.ToBase64())!;
        Assert.All(words, w => Assert.True(back.MightHave(w)));
        var falsePositives = Enumerable.Range(0, 2000).Count(i => back.MightHave("other" + i));
        Assert.True(falsePositives < 60, $"{falsePositives} false positives");
        Assert.Null(WordFilter.FromBase64("not base64!"));
    }

    [Fact]
    public void ClipsStartFourSecondsBeforeTheLineOnAWholeSecond()
    {
        var runtime = 3000 * S;
        var clip = ClipPicker.AroundLine(runtime, new List<SkipRange>(), false, 1000 * S + 5_500_000, 1003 * S, 30, runtime)!;
        Assert.Equal(996 * S, clip.StartTicks);
        Assert.Equal(1026 * S, clip.EndTicks);
        Assert.InRange(1000 * S + 5_500_000, clip.StartTicks, clip.EndTicks);
    }

    [Fact]
    public void LinesInSkippedPartsOrPastTheSpoilerLimitGiveNoClip()
    {
        var runtime = 3000 * S;
        var segments = new List<SkipRange> { new(500 * S, 600 * S) };
        // In an intro, in a film's opening minutes, in its last 8 %.
        Assert.Null(ClipPicker.AroundLine(runtime, segments, false, 550 * S, 552 * S, 30, runtime));
        Assert.Null(ClipPicker.AroundLine(runtime, segments, false, 30 * S, 32 * S, 30, runtime));
        Assert.Null(ClipPicker.AroundLine(runtime, segments, false, 2900 * S, 2902 * S, 30, runtime));
        // An unwatched film's last quarter; a line just before it gives a clip that ends there.
        var latest = SpoilerRules.LatestEnd(true, true, false, runtime);
        Assert.Null(ClipPicker.AroundLine(runtime, segments, false, 2260 * S, 2262 * S, 30, latest));
        var near = ClipPicker.AroundLine(runtime, segments, false, 2240 * S, 2242 * S, 30, latest)!;
        Assert.Equal(2250 * S, near.EndTicks);
        Assert.Equal(2220 * S, near.StartTicks);
        // A reported start.
        Assert.Null(ClipPicker.AroundLine(runtime, segments, false, 1004 * S, 1006 * S, 30, runtime, new HashSet<long> { 1000 * S }));
    }

    [Fact]
    public void ASkippedStretchJustBeforeTheLineMovesTheStart()
    {
        var runtime = 3000 * S;
        var segments = new List<SkipRange> { new(500 * S, 601 * S + 300_000) };
        var clip = ClipPicker.AroundLine(runtime, segments, false, 603 * S, 605 * S, 30, runtime)!;
        Assert.Equal(602 * S, clip.StartTicks);
        Assert.Equal(632 * S, clip.EndTicks);

        var credits = new List<SkipRange> { new(1020 * S, 1100 * S) };
        var early = ClipPicker.AroundLine(runtime, credits, false, 1000 * S, 1002 * S, 30, runtime)!;
        Assert.Equal(990 * S, early.StartTicks);
        Assert.Equal(1020 * S, early.EndTicks);

        // No room for the whole clip between two skipped stretches.
        var tight = new List<SkipRange> { new(500 * S, 1000 * S), new(1020 * S, 1100 * S) };
        Assert.Null(ClipPicker.AroundLine(runtime, tight, false, 1005 * S, 1007 * S, 30, runtime));
    }

    [Fact]
    public void LanguageCodesMatchInEitherForm()
    {
        Assert.True(LanguageCodes.Same("en", "eng"));
        Assert.True(LanguageCodes.Same("ger", "deu"));
        Assert.True(LanguageCodes.Same("de", "ger"));
        Assert.True(LanguageCodes.Same("pt-BR", "por"));
        Assert.False(LanguageCodes.Same("eng", "fre"));
        Assert.Equal("und", LanguageCodes.Canonical(null));
        Assert.Equal(1, DialogueIndex.TrackFor(new[] { "eng", "fre" }, "fra"));
        Assert.Equal(0, DialogueIndex.TrackFor(new[] { "eng", "fre" }, "spa"));
        Assert.Equal(-1, DialogueIndex.TrackFor(Array.Empty<string>(), "eng"));
    }

    [Fact]
    public void OneStreamPerWantedLanguageFullOnesFirst()
    {
        var streams = new List<SubtitleChoice>
        {
            new(2, 0, "eng", false, null, true, false),
            new(3, 1, "eng", false, null, false, false),
            new(4, null, "fre", true, "/x.fr.srt", false, false),
            new(5, 2, "spa", false, null, false, true),
        };
        Assert.Equal(new[] { 3, 4 }, DialogueSelection.Pick(streams, new[] { "eng", "fra", "ita" }).Select(s => s.Index));
        Assert.Equal(new[] { 5 }, DialogueSelection.Pick(streams, new[] { "jpn" }).Select(s => s.Index));
        Assert.Empty(DialogueSelection.Pick(Array.Empty<SubtitleChoice>(), new[] { "eng" }));
    }

    [Fact]
    public void TheIndexKeepsLinesOnceAndFindsThemAgain()
    {
        var folder = V2.Folder("dialogue");
        try
        {
            var item = Guid.NewGuid();
            var entry = DialogueIndex.SaveItem(folder, item, "sig", new[]
            {
                ("eng", new List<SubtitleCue> { new(1000, 2000, "Winter is coming."), new(5000, 6000, "Hodor.") }),
                ("fre", new List<SubtitleCue>()),
            });
            Assert.Single(entry.Tracks);
            DialogueIndex.SaveList(folder, new[] { entry, new DialogueEntry { ItemId = Guid.NewGuid(), Signature = "none" } });

            var (entries, filters) = DialogueIndex.LoadWithFilters(folder);
            Assert.Equal(2, entries.Count);
            Assert.True(filters[item][0]!.MightHave("winter"));
            var track = DialogueIndex.Track(folder, item, 0)!;
            Assert.Equal("winter is coming", track.Normalized[0]);
            Assert.Equal(new SubtitleCue(5000, 6000, "Hodor."), track.Cues[1]);
            Assert.Null(DialogueIndex.Track(folder, item, 1));

            var size = new FileInfo(Path.Combine(DialogueIndex.Folder(folder), $"{item:N}.json")).Length;
            Assert.True(size < 200, $"{size} bytes");

            // A film's worth of lines: 1500 of about 40 characters.
            var film = Guid.NewGuid();
            var lines = Enumerable.Range(0, 1500).Select(i => new SubtitleCue(60000 + (i * 3500), 62500 + (i * 3500), $"Line {i}: we should get out of here before it")).ToList();
            var filmEntry = DialogueIndex.SaveItem(folder, film, "sig", new[] { ("eng", lines) });
            var filmSize = new FileInfo(Path.Combine(DialogueIndex.Folder(folder), $"{film:N}.json")).Length;
            Assert.InRange(filmSize, 50_000, 100_000);
            Assert.InRange(filmEntry.Tracks[0].Filter.Length, 100, 4000);

            DialogueIndex.RemoveItem(folder, item);
            Assert.Null(DialogueIndex.Track(folder, item, 0));
        }
        finally
        {
            V2.Remove(folder);
        }
    }

    [Fact]
    public async Task EmbeddedSubtitlesAreReadWithFfmpeg()
    {
        var ffmpeg = new[] { "/opt/homebrew/bin/ffmpeg", "/usr/local/bin/ffmpeg", "/usr/bin/ffmpeg" }.FirstOrDefault(File.Exists);
        if (ffmpeg is null)
        {
            return;
        }

        var folder = V2.Folder("ffmpeg");
        Directory.CreateDirectory(folder);
        try
        {
            var srt = Path.Combine(folder, "in.srt");
            var srt2 = Path.Combine(folder, "in2.srt");
            var mkv = Path.Combine(folder, "film.mkv");
            await File.WriteAllTextAsync(srt, "1\n00:00:01,000 --> 00:00:02,500\nCrème brûlée, s'il vous plaît.\n\n2\n00:00:03,000 --> 00:00:04,000\n<i>Cool cool cool.</i>\n");
            await File.WriteAllTextAsync(srt2, "1\n00:00:01,000 --> 00:00:02,000\nSecond track.\n");
            var make = Process.Start(new ProcessStartInfo(ffmpeg)
            {
                ArgumentList = { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=black:s=64x64:d=5", "-i", srt, "-i", srt2,
                                 "-map", "0:v", "-map", "1:s", "-map", "2:s", "-c:v", "mpeg4", "-c:s", "srt", mkv },
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            await make.WaitForExitAsync();
            Assert.Equal(0, make.ExitCode);

            var texts = await DialogueExtractor.ExtractAsync(ffmpeg, mkv, new[] { 0, 1 }, NullLogger.Instance, CancellationToken.None);
            var cues = DialogueText.ParseSrt(texts[0]!);
            Assert.Equal(2, cues.Count);
            Assert.Equal("Crème brûlée, s'il vous plaît.", cues[0].Text);
            Assert.Equal(3000, cues[1].StartMs);
            Assert.Equal("Second track.", DialogueText.ParseSrt(texts[1]!).Single().Text);

            var external = await DialogueExtractor.ExtractAsync(ffmpeg, srt, new[] { 0 }, NullLogger.Instance, CancellationToken.None);
            Assert.Equal(2, DialogueText.ParseSrt(external[0]!).Count);

            var missing = await DialogueExtractor.ExtractAsync(ffmpeg, mkv, new[] { 5 }, NullLogger.Instance, CancellationToken.None);
            Assert.Null(missing[0]);
        }
        finally
        {
            V2.Remove(folder);
        }
    }
}

public class ArcsInboxTests
{
    [Fact]
    public void SentScenesComeOldestFirstUntilSeen()
    {
        var folder = V2.Folder("inbox");
        try
        {
            var inbox = new ArcsInbox(folder);
            var (a, b, me) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            inbox.Send(a, me, "x-1", "lol this", V2.Now);
            inbox.Send(b, me, "x-2", null, V2.Now.AddMinutes(1));
            inbox.Send(a, me, "x-1", "again", V2.Now.AddMinutes(2));
            var unseen = inbox.Unseen(me, V2.Now.AddMinutes(3));
            Assert.Equal(new[] { "x-2", "x-1" }, unseen.Select(s => s.ClipId));
            Assert.Equal("again", unseen[1].Note);

            Assert.True(inbox.MarkSeen(me, new[] { "x-1", "nope" }, V2.Now.AddMinutes(4)));
            Assert.Equal("x-2", inbox.Unseen(me, V2.Now.AddMinutes(4)).Single().ClipId);
            Assert.False(inbox.MarkSeen(me, new[] { "x-1" }, V2.Now.AddMinutes(5)));
            Assert.Empty(inbox.Unseen(me, V2.Now.AddDays(31)));

            // The inbox is apart from the person's Arcs history, so resetting that keeps it.
            new ArcsStore(folder).Reset(me);
            Assert.Single(inbox.Unseen(me, V2.Now.AddMinutes(6)));
        }
        finally
        {
            V2.Remove(folder);
        }
    }

    [Fact]
    public void ThirtyADayAndFiftyWaiting()
    {
        var folder = V2.Folder("limits");
        try
        {
            var inbox = new ArcsInbox(folder);
            var me = Guid.NewGuid();
            var senders = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
            for (var i = 0; i < ArcsInbox.DailyLimit; i++)
            {
                Assert.Equal(ArcSendResult.Sent, inbox.Send(senders[0], me, $"a-{i}", null, V2.Now.AddSeconds(i)));
            }

            Assert.Equal(ArcSendResult.DailyLimit, inbox.Send(senders[0], me, "a-x", null, V2.Now.AddHours(1)));
            Assert.Equal(ArcSendResult.Sent, inbox.Send(senders[0], me, "a-x", null, V2.Now.AddDays(1).AddMinutes(1)));

            for (var i = 0; i < 25; i++)
            {
                inbox.Send(senders[1], me, $"b-{i}", null, V2.Now.AddDays(1).AddMinutes(2 + i));
            }

            var unseen = inbox.Unseen(me, V2.Now.AddDays(1).AddHours(1));
            Assert.Equal(ArcsInbox.UnseenCap, unseen.Count);
            Assert.DoesNotContain(unseen, s => s.ClipId is "a-0" or "a-5");
            Assert.Contains(unseen, s => s.ClipId == "b-24");
        }
        finally
        {
            V2.Remove(folder);
        }
    }

    [Fact]
    public void NotesArePlainTextAndShort()
    {
        Assert.Null(ArcsInbox.CleanNote("   "));
        Assert.Equal("lol this", ArcsInbox.CleanNote(" lol\n\tthis\u0007 "));
        Assert.Equal(ArcsInbox.NoteLength, ArcsInbox.CleanNote(new string('a', 500))!.Length);
    }
}

public class ArcsMyStatsTests
{
    [Fact]
    public void TotalsComeFromTheirOwnEvents()
    {
        var friends = V2.Id(1);
        var film = V2.Id(2);
        var data = new ArcsUserData();
        ArcsStore.Apply(data, new[]
        {
            V2.E(ArcEventKinds.View, "f-1", friends, watched: 30, length: 30, at: V2.Now.AddDays(-3)),
            V2.E(ArcEventKinds.View, "f-2", friends, watched: 30, length: 30),
            V2.E(ArcEventKinds.View, "f-2", friends, watched: 10, length: 30),
            V2.E(ArcEventKinds.Rewatch, "f-2", friends, length: 30),
            V2.E(ArcEventKinds.Rewatch, "f-2", friends, length: 30),
            V2.E(ArcEventKinds.Rewatch, "m-1", film, length: 20),
            V2.E(ArcEventKinds.View, "m-1", film, watched: 20, length: 20),
            V2.E(ArcEventKinds.Like, "m-1", film),
            V2.E(ArcEventKinds.Share, "m-1", film),
            V2.E(ArcEventKinds.WatchFromHere, "f-1", friends),
            V2.E(ArcEventKinds.StartEpisode, "f-2", friends),
            V2.E(ArcEventKinds.React, "f-1", friends, "funny"),
            V2.E(ArcEventKinds.React, "f-2", friends, "funny"),
            V2.E(ArcEventKinds.React, "m-1", film, "sad"),
            V2.E(ArcEventKinds.View, "req-movie-5", watched: 600),
        }, V2.Now);
        var totals = ArcsTotals.For(data);
        Assert.Equal(3, totals.Scenes);
        Assert.Equal(3, totals.Minutes);
        Assert.Equal(1, totals.Likes);
        Assert.Equal(1, totals.Shares);
        Assert.Equal(2, totals.WatchFromHere);
        Assert.Equal(new[] { (friends, 2), (film, 1) }, totals.Titles);
        Assert.Equal("f-2", totals.Rewatched[0]);
        Assert.Equal("funny", totals.TopReaction);
        Assert.Equal(V2.Now.AddDays(-3), totals.Since);

        var none = ArcsTotals.For(new ArcsUserData());
        Assert.Null(none.Since);
        Assert.Null(none.TopReaction);
        Assert.Empty(none.Rewatched);
    }

    [Fact]
    public void BestScenesFillUpWithColdOpensThenChaptersAcrossEpisodes()
    {
        var episodes = new List<List<ClipCandidate>>
        {
            new() { new(100, 200, null, true), new(300, 400, "Fight"), new(500, 600, null) },
            new() { new(100, 200, null), new(300, 400, "Chase") },
            new() { new(50, 150, null, true) },
        };
        var picked = ArcsTop.Fill(episodes, 5, c => c);
        Assert.Equal(new long[] { 100, 50, 300, 300, 500 }, picked.Select(c => c.StartTicks));
        Assert.True(picked[0].IsColdOpen && picked[1].IsColdOpen);
        Assert.Empty(ArcsTop.Fill(episodes, 0, c => c));
    }
}

public class ArcsV2WireTests
{
    [Fact]
    public void NewClipFieldsHaveTheirDefaults()
    {
        var clip = new ArcClipDto();
        Assert.Equal("scene", clip.Kind);
        Assert.False(clip.IsColdOpen);
        Assert.Null(clip.MyReaction);
        Assert.Equal(new[] { "funny", "wow", "sad" }, clip.Reactions.Keys);
        var json = JsonSerializer.Serialize(clip);
        Assert.Contains("\"Kind\":\"scene\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Reactions\":{\"funny\":0,\"wow\":0,\"sad\":0}", json, StringComparison.Ordinal);
        Assert.Contains("\"MatchedTicks\":null", json, StringComparison.Ordinal);
    }

    [Fact]
    public void JellyfinsOwnSerializerKeepsTheReactionKeysLowercase()
    {
        var clip = ArcsService.RequestDto(new RequestCandidate("movie", 7, "F", null, null, Array.Empty<string>(), null, null, "k", null, null, 0, Guid.Empty, "S"));
        clip.Reactions["funny"] = 2;
        var pascal = JsonSerializer.Serialize(clip, Jellyfin.Extensions.Json.JsonDefaults.PascalCaseOptions);
        Assert.Contains("\"Reactions\":{\"funny\":2,\"wow\":0,\"sad\":0}", pascal, StringComparison.Ordinal);
        Assert.Contains("\"Kind\":\"request\"", pascal, StringComparison.Ordinal);
        Assert.Contains("\"TrailerYouTubeKey\":\"k\"", pascal, StringComparison.Ordinal);
        Assert.Contains("\"Type\":\"Movie\"", pascal, StringComparison.Ordinal);
        var main = JsonSerializer.Serialize(clip, Jellyfin.Extensions.Json.JsonDefaults.Options);
        Assert.Contains("\"funny\":2", main, StringComparison.Ordinal);
    }

    [Fact]
    public void ReactEventsCarryTheirReaction()
    {
        var e = JsonSerializer.Deserialize<ArcEventsBody>("""{ "Events": [ { "ClipId": "x-1", "Kind": "react", "Reaction": "wow" } ] }""")!;
        Assert.Equal("wow", e.Events![0].Reaction);
        Assert.Contains(ArcEventKinds.React, ArcEventKinds.All);
        Assert.Contains(ArcEventKinds.Request, ArcEventKinds.All);
        Assert.Contains(ArcEventKinds.StartEpisode, ArcEventKinds.All);
    }

    private static ArcsSettings Valid() => new();

    [Fact]
    public void AdminSettingsKeepTheNewChoices()
    {
        var input = Valid();
        input.RequestsInFeed = false;
        input.Sending = "Off";
        input.DialogueSearch = false;
        var clean = ArcsSettingsRules.Clean(input, _ => true, out var error)!;
        Assert.Null(error);
        Assert.False(clean.RequestsInFeed);
        Assert.Equal("Off", clean.Sending);
        Assert.False(clean.DialogueSearch);

        var defaults = ArcsSettingsRules.Clean(Valid(), _ => true, out _)!;
        Assert.True(defaults.RequestsInFeed);
        Assert.Equal("Everyone", defaults.Sending);
        Assert.True(defaults.DialogueSearch);

        var bad = Valid();
        bad.Sending = "Friends";
        Assert.Null(ArcsSettingsRules.Clean(bad, _ => true, out error));
        Assert.NotNull(error);

        var old = JsonSerializer.Deserialize<ArcsSettings>("""{ "Enabled": true }""")!;
        Assert.Equal("Everyone", ArcsSettingsRules.Clean(old, _ => true, out _)!.Sending);
    }
}

public class ArcsFriendTests
{
    [Fact]
    public void CodesAreEightUnambiguousCharactersInTwoGroups()
    {
        var folder = V2.Folder("codes");
        try
        {
            var friends = new ArcsFriends(folder);
            var me = Guid.NewGuid();
            var code = friends.CodeFor(me);
            Assert.Matches("^[2-9A-HJKMNP-Z]{4}-[2-9A-HJKMNP-Z]{4}$", code);
            Assert.DoesNotContain(code, c => c is '0' or 'O' or '1' or 'I' or 'L');
            Assert.Equal(code, friends.CodeFor(me));
            Assert.Equal(code, new ArcsFriends(folder).CodeFor(me));
            Assert.NotEqual(code, friends.CodeFor(Guid.NewGuid()));
        }
        finally
        {
            V2.Remove(folder);
        }
    }

    [Fact]
    public void CodesIgnoreCaseSpacesAndDashes()
    {
        Assert.Equal("K7Q2MX9P", ArcsFriends.Normalize("k7q2-mx9p"));
        Assert.Equal("K7Q2MX9P", ArcsFriends.Normalize(" K7Q2 MX9P "));
        Assert.Equal("K7Q2MX9P", ArcsFriends.Normalize("K7-Q2-MX-9P"));
        Assert.Null(ArcsFriends.Normalize("K7Q2-MX9"));
        Assert.Null(ArcsFriends.Normalize("K7Q2-MX9PP"));
        Assert.Null(ArcsFriends.Normalize("K7Q2-MX0P"));
        Assert.Null(ArcsFriends.Normalize(null));
        Assert.Equal("K7Q2-MX9P", ArcsFriends.Format("K7Q2MX9P"));
    }

    [Fact]
    public void AddingIsMutualAndRemovingIsBothWays()
    {
        var folder = V2.Folder("friends");
        try
        {
            var friends = new ArcsFriends(folder);
            var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
            var code = friends.CodeFor(b).ToLowerInvariant();
            var (result, friend) = friends.Add(a, code, _ => true, V2.Now);
            Assert.Equal(FriendAddResult.Added, result);
            Assert.Equal(b, friend);
            Assert.True(friends.AreFriends(a, b));
            Assert.True(friends.AreFriends(b, a));
            Assert.Equal(FriendAddResult.Added, friends.Add(b, friends.CodeFor(a), _ => true, V2.Now).Result);
            Assert.Single(friends.FriendsOf(a));

            friends.Remove(b, a);
            Assert.Empty(friends.FriendsOf(a));
            Assert.Empty(friends.FriendsOf(b));
        }
        finally
        {
            V2.Remove(folder);
        }
    }

    [Fact]
    public void OwnCodeUnknownCodeAndPeopleWhoCantReceiveAllLookTheSame()
    {
        var folder = V2.Folder("wrong");
        try
        {
            var friends = new ArcsFriends(folder);
            var (me, off) = (Guid.NewGuid(), Guid.NewGuid());
            Assert.Equal(FriendAddResult.WrongCode, friends.Add(me, friends.CodeFor(me), _ => true, V2.Now).Result);
            Assert.Equal(FriendAddResult.WrongCode, friends.Add(me, "ZZZZ-ZZZZ", _ => true, V2.Now).Result);
            Assert.Equal(FriendAddResult.WrongCode, friends.Add(me, friends.CodeFor(off), id => id != off, V2.Now).Result);
            Assert.Empty(friends.FriendsOf(me));
            Assert.Empty(friends.FriendsOf(off));
        }
        finally
        {
            V2.Remove(folder);
        }
    }

    [Fact]
    public void TenWrongCodesAnHour()
    {
        var folder = V2.Folder("tries");
        try
        {
            var friends = new ArcsFriends(folder);
            var (me, other) = (Guid.NewGuid(), Guid.NewGuid());
            var good = friends.CodeFor(other);
            for (var i = 0; i < ArcsFriends.TriesPerHour; i++)
            {
                Assert.Equal(FriendAddResult.WrongCode, friends.Add(me, "WRONG", _ => true, V2.Now.AddMinutes(i)).Result);
            }

            Assert.Equal(FriendAddResult.TooManyTries, friends.Add(me, good, _ => true, V2.Now.AddMinutes(30)).Result);
            Assert.Equal(FriendAddResult.Added, friends.Add(Guid.NewGuid(), good, _ => true, V2.Now.AddMinutes(30)).Result);
            Assert.Equal(FriendAddResult.Added, friends.Add(me, good, _ => true, V2.Now.AddMinutes(61)).Result);
        }
        finally
        {
            V2.Remove(folder);
        }
    }

    [Fact]
    public void ANewCodeReplacesTheOldOneAndKeepsFriends()
    {
        var folder = V2.Folder("newcode");
        try
        {
            var friends = new ArcsFriends(folder);
            var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            var old = friends.CodeFor(b);
            friends.Add(a, old, _ => true, V2.Now);
            var fresh = friends.NewCode(b);
            Assert.NotEqual(old, fresh);
            Assert.Equal(fresh, friends.CodeFor(b));
            Assert.Equal(FriendAddResult.WrongCode, friends.Add(c, old, _ => true, V2.Now).Result);
            Assert.Equal(FriendAddResult.Added, friends.Add(c, fresh, _ => true, V2.Now).Result);
            Assert.True(friends.AreFriends(a, b));

            // Resetting Arcs history keeps friends and the code.
            new ArcsStore(folder).Reset(b);
            Assert.Equal(fresh, friends.CodeFor(b));
            Assert.Equal(2, friends.FriendsOf(b).Count);
        }
        finally
        {
            V2.Remove(folder);
        }
    }

    [Fact]
    public void AtMostAHundredFriends()
    {
        var folder = V2.Folder("full");
        try
        {
            var friends = new ArcsFriends(folder);
            var me = Guid.NewGuid();
            for (var i = 0; i < ArcsFriends.MaxFriends; i++)
            {
                Assert.Equal(FriendAddResult.Added, friends.Add(me, friends.CodeFor(Guid.NewGuid()), _ => true, V2.Now).Result);
            }

            Assert.Equal(FriendAddResult.Full, friends.Add(me, friends.CodeFor(Guid.NewGuid()), _ => true, V2.Now).Result);
            Assert.Equal(FriendAddResult.Full, friends.Add(Guid.NewGuid(), friends.CodeFor(me), _ => true, V2.Now).Result);
        }
        finally
        {
            V2.Remove(folder);
        }
    }

    [Fact]
    public void HiddenUsersCanReceiveButDisabledOnesCant()
    {
        var hidden = new Jellyfin.Database.Implementations.Entities.User("hidden", "auth", "reset");
        hidden.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsHidden, true);
        Assert.True(Jellyfin.Plugin.ArcfinSync.Api.Arcs.ArcsController.CanReceive(hidden, true));
        Assert.False(Jellyfin.Plugin.ArcfinSync.Api.Arcs.ArcsController.CanReceive(hidden, false));

        var disabled = new Jellyfin.Database.Implementations.Entities.User("disabled", "auth", "reset");
        disabled.SetPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled, true);
        Assert.False(Jellyfin.Plugin.ArcfinSync.Api.Arcs.ArcsController.CanReceive(disabled, true));
    }

    [Fact]
    public void ASentSceneBecomesAnEventForTheRecipientOnly()
    {
        var folder = V2.Folder("events");
        try
        {
            var store = new Jellyfin.Plugin.ArcfinSync.Api.Notifications.EventStore(folder);
            var (to, other) = (Guid.NewGuid(), Guid.NewGuid());
            var line = ArcsSentNotice.TitleLine("Friends", 1, 5);
            Assert.Equal("Friends · S1 · E5", line);
            Assert.Equal("Heat", ArcsSentNotice.TitleLine("Heat", null, null));
            store.Add(new[] { ArcsSentNotice.Event(to, "Alex", line, " lol\nthis ", "x-1") }, V2.Now);
            store.Add(new[] { ArcsSentNotice.Event(to, "Sam", "Heat", null, "x-2") }, V2.Now);

            var events = store.After(0, to);
            Assert.Equal(2, events.Count);
            Assert.Equal("arcs-sent", events[0].Kind);
            Assert.StartsWith("arcs-sent.", events[0].Id, StringComparison.Ordinal);
            Assert.NotEqual(events[0].Id, events[1].Id);
            Assert.Equal("Alex", events[0].Title);
            Assert.Equal("Friends · S1 · E5\nlol this", events[0].Body);
            Assert.Equal("x-1", events[0].ClipId);
            Assert.Equal("Heat", events[1].Body);
            Assert.Empty(store.After(0, other));
            Assert.Contains("\"ClipId\":\"x-1\"", JsonSerializer.Serialize(events[0], Jellyfin.Extensions.Json.JsonDefaults.PascalCaseOptions), StringComparison.Ordinal);
        }
        finally
        {
            V2.Remove(folder);
        }
    }

    [Fact]
    public void SceneScansAreClampedAndDefaultToOne()
    {
        Assert.Equal(1, ArcsSettingsRules.Clean(new ArcsSettings(), _ => true, out _)!.SceneScans);
        var many = new ArcsSettings();
        many.SceneScans = 50;
        Assert.Equal(ArcsSettingsRules.MaxSceneScans, ArcsSettingsRules.Clean(many, _ => true, out _)!.SceneScans);
        var none = new ArcsSettings();
        none.SceneScans = 0;
        Assert.Equal(1, ArcsSettingsRules.Clean(none, _ => true, out _)!.SceneScans);
        var old = JsonSerializer.Deserialize<ArcsSettings>("""{ "Enabled": true }""")!;
        Assert.Equal(1, old.SceneScans);
    }

    [Fact]
    public void SceneScanDropsNonKeyframesBeforeDecodingOnBothPaths()
    {
        var cpu = SceneIndex.ScanArguments("/m/a.mkv", gpu: false);
        var gpu = SceneIndex.ScanArguments("/m/a.mkv", gpu: true);
        foreach (var args in new[] { cpu, gpu })
        {
            var discard = args.IndexOf("-discard");
            Assert.True(discard >= 0 && args[discard + 1] == "nokey" && discard < args.IndexOf("-i"));
        }

        Assert.DoesNotContain("-hwaccel", cpu);
        Assert.Contains(cpu, a => a.StartsWith("scale=160:-2,select=", StringComparison.Ordinal));
        Assert.True(gpu.IndexOf("-hwaccel") < gpu.IndexOf("-i"));
        Assert.Contains(gpu, a => a.StartsWith("scale_cuda=160:-2:format=nv12,hwdownload,format=nv12,select=", StringComparison.Ordinal));
    }

    [Fact]
    public void AdminCanTurnOffYouTubeTrailersForRequestClips()
    {
        var off = new ArcsSettings { YouTubeTrailers = false };
        Assert.False(ArcsSettingsRules.Clean(off, _ => true, out _)!.YouTubeTrailers);
        Assert.True(JsonSerializer.Deserialize<ArcsSettings>("""{ "Enabled": true }""")!.YouTubeTrailers);
    }
}
