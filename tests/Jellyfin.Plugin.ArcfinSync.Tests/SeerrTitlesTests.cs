using System.Collections.Generic;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.ArcfinSync.Api.Integrations;
using Xunit;

namespace Jellyfin.Plugin.ArcfinSync.Tests;

public class SeerrTitlesTests
{
    private static JsonNode Requests() => JsonNode.Parse("""
        { "pageInfo": { "results": 3 }, "results": [
            { "id": 1, "media": { "mediaType": "movie", "tmdbId": 603 } },
            { "id": 2, "media": { "mediaType": "tv", "tmdbId": 1399 } },
            { "id": 3, "media": { "mediaType": "movie" } } ] }
        """)!;

    [Fact]
    public void TitlesAreReadFromRequestsAndWatchlistRows()
    {
        var titles = SeerrTitles.Titles(Requests());
        Assert.Equal(new (string, int)?[] { ("movie", 603), ("tv", 1399), null }, titles);

        var watchlist = JsonNode.Parse("""{ "results": [ { "tmdbId": 5, "mediaType": "tv" } ] }""")!;
        Assert.Equal(("tv", 5), SeerrTitles.Titles(watchlist)[0]);
    }

    [Fact]
    public void ACardHasTheTitleArtworkAndRatingsByCountry()
    {
        var movie = SeerrTitles.Card(JsonNode.Parse("""
            { "title": "The Matrix", "posterPath": "/p.jpg", "backdropPath": "/b.jpg",
              "releases": { "results": [
                { "iso_3166_1": "GB", "release_dates": [ { "certification": "" }, { "certification": "15" } ] },
                { "iso_3166_1": "US", "release_dates": [ { "certification": "R" } ] } ] } }
            """))!;
        Assert.Equal("The Matrix", movie.Title);
        Assert.Equal("15", movie.Rating("gb"));
        Assert.Equal("R", movie.Rating("DE"));

        var show = SeerrTitles.Card(JsonNode.Parse("""{ "name": "A Show", "contentRatings": { "results": [ { "iso_3166_1": "FR", "rating": "12" } ] } }"""))!;
        Assert.Equal("12", show.Rating("GB"));
        Assert.Equal(string.Empty, SeerrTitles.Card(JsonNode.Parse("""{ "title": "Unrated" }"""))!.Rating("GB"));
        Assert.Null(SeerrTitles.Card(JsonNode.Parse("""{ "id": 5 }""")));
    }

    [Fact]
    public void FillAddsTheCardToRowsItKnows()
    {
        var page = Requests();
        SeerrTitles.Fill(page, new Dictionary<string, SeerrTitleCard> { [SeerrTitles.Key("movie", 603)] = new() { Title = "The Matrix", PosterPath = "/p.jpg" } });
        Assert.Equal("The Matrix", page["results"]![0]!["arcfin"]!["title"]!.GetValue<string>());
        Assert.Null(page["results"]![1]!["arcfin"]);
    }

    [Theory]
    [InlineData("discover/movies", true)]
    [InlineData("discover/watchlist", true)]
    [InlineData("search", true)]
    [InlineData("movie/603", true)]
    [InlineData("tv/1399/season/1", true)]
    [InlineData("request", false)]
    [InlineData("request/5", false)]
    [InlineData("auth/me", false)]
    [InlineData("user/3/quota", false)]
    [InlineData("issue", false)]
    public void OnlyReadsThatStayTheSameAreKept(string path, bool kept) => Assert.Equal(kept, SeerrAnswers.Keeps(path));
}
