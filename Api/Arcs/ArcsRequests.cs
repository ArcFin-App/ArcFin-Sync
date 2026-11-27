using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArcfinSync.Api.Integrations;
using Jellyfin.Plugin.ArcfinSync.Api.Security;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>A library title request clips are found from: one the person liked or watched most.</summary>
public sealed record RequestSeed(Guid TitleId, string Name, string MediaType, int TmdbId, double Weight);

/// <summary>A title not on the server, with its trailer, that the person could request through Seerr.</summary>
public sealed record RequestCandidate(
    string MediaType,
    int TmdbId,
    string Name,
    string? Overview,
    int? Year,
    IReadOnlyList<string> Genres,
    float? Rating,
    string? Certification,
    string TrailerKey,
    string? PosterPath,
    string? BackdropPath,
    long RuntimeTicks,
    Guid SeedTitleId,
    string SeedName)
{
    public string Id => new RequestKey(MediaType, TmdbId).ToString();
}

/// <summary>One recommendation as Seerr or TMDB lists it.</summary>
public sealed record RequestHit(string MediaType, int TmdbId, string Name, string? Overview, int? Year, float? Rating, string? PosterPath, string? BackdropPath,
                                bool Adult, int? MediaStatus, bool HasRequests);

/// <summary>A title's video as TMDB lists it.</summary>
public sealed record TmdbVideo(string Key, string Site, string Type, bool Official, string? Language);

/// <summary>What a title's details add: trailers, certification, genres and length.</summary>
public sealed record RequestDetails(List<TmdbVideo> Videos, string? Certification, List<string> Genres, int? RuntimeMinutes, bool Adult, int? MediaStatus, bool HasRequests);

/// <summary>The rules for which titles become request clips, and where they go in a page. Free of Jellyfin and HTTP so they can be tested.</summary>
public static class RequestRules
{
    public const int Every = 8;

    /// <summary>
    /// The page positions request clips take: one in each run of eight across the feed, never the
    /// first of a run, so never the feed's first clip and never two in a row. Positions before
    /// <paramref name="firstFree"/> (sent scenes) are left alone, as is a first position that would
    /// follow a request at the end of the page before.
    /// </summary>
    public static HashSet<int> Slots(int seed, int pageStart, int count, int firstFree, bool previousWasRequest)
    {
        var slots = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var global = pageStart + i;
            var block = global / Every;
            var offset = 1 + (int)((uint)HashCode.Combine(seed, block, 0x5eed) % (Every - 1));
            if (global % Every == offset && i >= firstFree && !(i == 0 && previousWasRequest))
            {
                slots.Add(i);
            }
        }

        return slots;
    }

    /// <summary>
    /// A page: sent scenes first, then scenes with request clips at their slots. Ends when the
    /// scenes run out; a slot with no request left takes a scene.
    /// </summary>
    public static List<T> Interleave<T>(IReadOnlyList<T> sent, IReadOnlyList<T> scenes, IReadOnlyList<T> requests, ISet<int> slots, int count)
    {
        var page = new List<T>(count);
        int s = 0, r = 0;
        for (var i = 0; i < count; i++)
        {
            if (i < sent.Count)
            {
                page.Add(sent[i]);
            }
            else if (slots.Contains(i) && r < requests.Count)
            {
                page.Add(requests[r++]);
            }
            else if (s < scenes.Count)
            {
                page.Add(scenes[s++]);
            }
            else
            {
                break;
            }
        }

        return page;
    }

    /// <summary>
    /// Whether a recommendation may become a request clip: not adult, not on the server, not
    /// requested or known to Seerr already (pending, processing, available or blocked), and not one
    /// the person requested or put away.
    /// </summary>
    public static bool Usable(RequestHit hit, ISet<string> inLibrary, ISet<string> mine)
    {
        if (hit.Adult || hit.HasRequests || hit.MediaStatus is >= 2 and <= 6)
        {
            return false;
        }

        return !inLibrary.Contains(LibraryKey(hit.MediaType, hit.TmdbId)) && !mine.Contains(new RequestKey(hit.MediaType, hit.TmdbId).ToString());
    }

    public static string LibraryKey(string mediaType, int tmdbId) => string.Create(CultureInfo.InvariantCulture, $"{mediaType}:{tmdbId}");

    /// <summary>
    /// The trailer to show: a YouTube trailer in the person's language, else English, official ones
    /// first. Videos with no language (Seerr doesn't pass it on) count when nothing better is there.
    /// </summary>
    public static string? Trailer(IEnumerable<TmdbVideo> videos, string language)
    {
        var trailers = videos.Where(v => v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) && v.Type.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
                                         && v.Key.Length is > 0 and <= 32).ToList();
        foreach (var wanted in new[] { language, "en" })
        {
            var match = trailers.Where(v => string.Equals(v.Language, wanted, StringComparison.OrdinalIgnoreCase)).OrderByDescending(v => v.Official).FirstOrDefault();
            if (match is not null)
            {
                return match.Key;
            }
        }

        return trailers.Where(v => string.IsNullOrEmpty(v.Language)).OrderByDescending(v => v.Official).FirstOrDefault()?.Key;
    }

    /// <summary>
    /// Whether the person's parental controls allow a title, best effort: with a known certification
    /// its score must be within their limit; without one it's left out when they block unrated titles.
    /// </summary>
    public static bool RatingAllowed(int? certificationScore, bool certificationKnown, int? maxScore, bool blockUnrated)
    {
        if (!certificationKnown || certificationScore is null)
        {
            return !blockUnrated;
        }

        return maxScore is not { } max || certificationScore <= max;
    }

    /// <summary>Recommendations from several seeds put together: each counts for its seed's weight, more near the top of its list.</summary>
    public static List<(RequestHit Hit, RequestSeed Seed, double Score)> Merge(IEnumerable<(RequestSeed Seed, IReadOnlyList<RequestHit> Hits)> lists)
    {
        var merged = new Dictionary<string, (RequestHit Hit, RequestSeed Seed, double Score, double Best)>(StringComparer.Ordinal);
        foreach (var (seed, hits) in lists)
        {
            for (var i = 0; i < hits.Count; i++)
            {
                var hit = hits[i];
                var key = LibraryKey(hit.MediaType, hit.TmdbId);
                var value = seed.Weight * (1 - (Math.Min(i, 19) / 20.0));
                if (merged.TryGetValue(key, out var found))
                {
                    merged[key] = value > found.Best ? (hit, seed, found.Score + value, value) : (found.Hit, found.Seed, found.Score + value, found.Best);
                }
                else
                {
                    merged[key] = (hit, seed, value, value);
                }
            }
        }

        return merged.Values.OrderByDescending(m => m.Score).ThenBy(m => m.Hit.TmdbId).Select(m => (m.Hit, m.Seed, m.Score)).ToList();
    }
}

/// <summary>Reads Seerr's and TMDB's answers, which use camelCase and snake_case names for the same things.</summary>
public static class RequestParsing
{
    public static List<RequestHit> Hits(JsonElement root, string defaultType)
    {
        var result = new List<RequestHit>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var r in results.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id) || id <= 0)
            {
                continue;
            }

            var type = Str(r, "mediaType", "media_type") ?? defaultType;
            if (type is not ("movie" or "tv"))
            {
                continue;
            }

            var name = Str(r, "title", "name") ?? string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            var date = Str(r, "releaseDate", "release_date") ?? Str(r, "firstAirDate", "first_air_date");
            var (status, requests) = MediaInfo(r);
            result.Add(new RequestHit(type, id, name, Str(r, "overview", "overview"), Year(date), Float(r, "voteAverage", "vote_average"),
                                      Str(r, "posterPath", "poster_path"), Str(r, "backdropPath", "backdrop_path"), Bool(r, "adult"), status, requests));
        }

        return result;
    }

    public static RequestDetails Details(JsonElement root, string mediaType, string country)
    {
        var videos = new List<TmdbVideo>();
        foreach (var list in new[] { Path(root, "videos", "results"), Path(root, "relatedVideos") })
        {
            if (list is not { ValueKind: JsonValueKind.Array } array)
            {
                continue;
            }

            foreach (var v in array.EnumerateArray())
            {
                if (Str(v, "key", "key") is { } key && Str(v, "site", "site") is { } site && Str(v, "type", "type") is { } type)
                {
                    videos.Add(new TmdbVideo(key, site, type, Bool(v, "official"), Str(v, "iso_639_1", "iso6391")));
                }
            }
        }

        var genres = new List<string>();
        if (root.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array)
        {
            genres.AddRange(g.EnumerateArray().Select(x => Str(x, "name", "name")).Where(n => !string.IsNullOrEmpty(n))!);
        }

        int? runtime = root.TryGetProperty("runtime", out var rt) && rt.TryGetInt32(out var minutes) && minutes > 0 ? minutes : null;
        foreach (var name in new[] { "episode_run_time", "episodeRunTime" })
        {
            if (runtime is null && root.TryGetProperty(name, out var ert) && ert.ValueKind == JsonValueKind.Array)
            {
                runtime = ert.EnumerateArray().Select(x => x.TryGetInt32(out var m) ? m : 0).FirstOrDefault(m => m > 0) is var first && first > 0 ? first : null;
            }
        }

        var (status, requests) = MediaInfo(root);
        return new RequestDetails(videos, Certification(root, mediaType, country), genres, runtime, Bool(root, "adult"), status, requests);
    }

    /// <summary>The certification for a country (else the US): a film's release certification, a show's content rating.</summary>
    public static string? Certification(JsonElement root, string mediaType, string country)
    {
        var results = mediaType == "movie"
            ? Path(root, "release_dates", "results") ?? Path(root, "releases", "results")
            : Path(root, "content_ratings", "results") ?? Path(root, "contentRatings", "results");
        if (results is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }

        foreach (var wanted in new[] { country, "US" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var entry in array.EnumerateArray())
            {
                if (!string.Equals(Str(entry, "iso_3166_1", "iso31661"), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (mediaType != "movie")
                {
                    return Str(entry, "rating", "rating") is { Length: > 0 } rating ? rating : null;
                }

                if (entry.TryGetProperty("release_dates", out var dates) && dates.ValueKind == JsonValueKind.Array)
                {
                    var found = dates.EnumerateArray().Select(d => Str(d, "certification", "certification")).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    private static (int? Status, bool Requests) MediaInfo(JsonElement e)
    {
        if (!e.TryGetProperty("mediaInfo", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return (null, false);
        }

        int? status = info.TryGetProperty("status", out var s) && s.TryGetInt32(out var value) ? value : null;
        var requests = info.TryGetProperty("requests", out var r) && r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 0;
        return (status, requests);
    }

    private static JsonElement? Path(JsonElement e, params string[] names)
    {
        var current = e;
        foreach (var name in names)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current;
    }

    private static string? Str(JsonElement e, string a, string b)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in new[] { a, b })
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text)
            {
                return text;
            }
        }

        return null;
    }

    private static float? Float(JsonElement e, string a, string b)
    {
        foreach (var name in new[] { a, b })
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && d > 0)
            {
                return (float)Math.Round(d, 1);
            }
        }

        return null;
    }

    private static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;
}

/// <summary>What the request pool needs to know about the person.</summary>
public sealed record RequestViewer(Guid UserId, string Language, string Country, int? MaxRatingScore, bool BlockUnratedMovies, bool BlockUnratedShows,
                                   Func<string, string, int?> RatingScore);

/// <summary>
/// Request clips: titles not on the server, related to what the person liked or watched most, each
/// with a YouTube trailer. Recommendations and request status come from Seerr (so titles already
/// requested, or on the server, are left out), trailers and certifications from TMDB when the server
/// has a TMDB key, otherwise from Seerr's own title details. Shown only when Seerr is set up and the
/// person has a Seerr account that may request (or Seerr accounts are added on first use). TMDB
/// lookups are kept for 12 hours, Seerr's for an hour, and each person's list for an hour; the list is made in the background, so
/// a feed page never waits on Seerr for more than a moment.
/// </summary>
public sealed class ArcsRequests
{
    private const int SeedCount = 5;
    private const int PoolSize = 20;
    private const int AdminBit = 2;
    private const int RequestBit = 32;
    private const int RequestMovieBit = 262144;
    private const int RequestTvBit = 524288;
    private static readonly TimeSpan LookupLifetime = TimeSpan.FromHours(12);

    // Seerr's lists carry whether a title is requested or here already, which changes far more often than TMDB's details.
    private static readonly TimeSpan SeerrLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan PoolLifetime = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, (DateTime At, string? Json)> Lookups = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Guid, (DateTime At, List<RequestCandidate> Pool)> Pools = new();
    private static readonly ConcurrentDictionary<Guid, Task> Building = new();

    private readonly IHttpClientFactory _http;
    private readonly ILogger _logger;

    public ArcsRequests(IHttpClientFactory http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Whether request clips can show at all: the admin allows them and Seerr is set up.</summary>
    public static bool Available => ArcsService.Settings.RequestsInFeed && SeerrClient.IsConfigured;

    public static void Forget(Guid userId) => Pools.TryRemove(userId, out _);

    /// <summary>The library title a request clip was found from, while the person's list is kept.</summary>
    public static Guid? SeedOf(Guid userId, string requestId) =>
        Pools.TryGetValue(userId, out var pool) ? pool.Pool.FirstOrDefault(c => c.Id == requestId)?.SeedTitleId : null;

    /// <summary>
    /// The person's request clips as they stand. When the list is missing or old it's made again in
    /// the background; a missing one is waited for up to <paramref name="wait"/>.
    /// </summary>
    public async Task<List<RequestCandidate>> PoolAsync(RequestViewer viewer, Func<List<RequestSeed>> seeds, Func<ISet<string>> inLibrary, ISet<string> mine,
                                                        TimeSpan wait, CancellationToken cancellationToken)
    {
        if (!Available)
        {
            return new List<RequestCandidate>();
        }

        var now = DateTime.UtcNow;
        var have = Pools.TryGetValue(viewer.UserId, out var cached);
        if (!have || now - cached.At > PoolLifetime)
        {
            var build = Building.GetOrAdd(viewer.UserId, id => Task.Run(async () =>
            {
                try
                {
                    var pool = await BuildAsync(viewer, seeds(), inLibrary(), mine, CancellationToken.None).ConfigureAwait(false);
                    Pools[viewer.UserId] = (DateTime.UtcNow, pool);
                }
                catch (Exception error)
                {
                    _logger.LogDebug(error, "Arcfin Sync Arcs: request clips couldn't be found");
                    Pools[viewer.UserId] = (DateTime.UtcNow - PoolLifetime + TimeSpan.FromMinutes(5), have ? cached.Pool : new List<RequestCandidate>());
                }
                finally
                {
                    Building.TryRemove(viewer.UserId, out _);
                }
            }));
            if (!have)
            {
                try
                {
                    await build.WaitAsync(wait, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }
            }
        }

        return Pools.TryGetValue(viewer.UserId, out var current) ? current.Pool.Where(c => !mine.Contains(c.Id)).ToList() : new List<RequestCandidate>();
    }

    private async Task<List<RequestCandidate>> BuildAsync(RequestViewer viewer, List<RequestSeed> seeds, ISet<string> inLibrary, ISet<string> mine, CancellationToken token)
    {
        var result = new List<RequestCandidate>();
        var seerr = new SeerrClient(_http);
        var permissions = await seerr.UserPermissionsAsync(token).ConfigureAwait(false);
        var bits = permissions.TryGetValue(viewer.UserId, out var b) ? b : (int?)null;
        if (bits is null && Plugin.Instance?.Configuration.Integrations.AddSeerrUsers != true)
        {
            return result;
        }

        bool MayRequest(string type) => bits is not { } p
                                        || (p & (AdminBit | RequestBit)) != 0
                                        || (p & (type == "movie" ? RequestMovieBit : RequestTvBit)) != 0;

        var lists = new List<(RequestSeed, IReadOnlyList<RequestHit>)>();
        foreach (var seed in seeds.Take(SeedCount))
        {
            var hits = new List<RequestHit>();
            foreach (var kind in new[] { "recommendations", "similar" })
            {
                if (hits.Count >= 10)
                {
                    break;
                }

                var json = await SeerrAsync(seerr, $"{seed.MediaType}/{seed.TmdbId}/{kind}", $"?page=1&language={Uri.EscapeDataString(viewer.Language)}", token).ConfigureAwait(false);
                using var doc = Parse(json);
                if (doc is not null)
                {
                    hits.AddRange(RequestParsing.Hits(doc.RootElement, seed.MediaType).Where(h => hits.All(x => x.TmdbId != h.TmdbId || x.MediaType != h.MediaType)));
                }
            }

            lists.Add((seed, hits));
        }

        var tmdbKey = SecretVault.Current.Get(SeerrClient.TmdbKeyName);
        foreach (var (hit, seed, _) in RequestRules.Merge(lists).Where(m => RequestRules.Usable(m.Hit, inLibrary, mine) && MayRequest(m.Hit.MediaType)).Take(PoolSize * 2))
        {
            if (result.Count >= PoolSize)
            {
                break;
            }

            var json = tmdbKey is null
                ? await SeerrAsync(seerr, $"{hit.MediaType}/{hit.TmdbId}", $"?language={Uri.EscapeDataString(viewer.Language)}", token).ConfigureAwait(false)
                : await TmdbAsync(tmdbKey, hit, viewer.Language, token).ConfigureAwait(false);
            RequestDetails details;
            using (var doc = Parse(json))
            {
                if (doc is null)
                {
                    continue;
                }

                details = RequestParsing.Details(doc.RootElement, hit.MediaType, viewer.Country);
            }

            if (details.Adult || details.HasRequests || details.MediaStatus is >= 2 and <= 6 || RequestRules.Trailer(details.Videos, viewer.Language) is not { } trailer)
            {
                continue;
            }

            var known = !string.IsNullOrWhiteSpace(details.Certification);
            var score = known ? viewer.RatingScore(details.Certification!, viewer.Country) : null;
            if (!RequestRules.RatingAllowed(score, known && score is not null, viewer.MaxRatingScore, hit.MediaType == "movie" ? viewer.BlockUnratedMovies : viewer.BlockUnratedShows))
            {
                continue;
            }

            result.Add(new RequestCandidate(hit.MediaType, hit.TmdbId, hit.Name, hit.Overview, hit.Year, details.Genres, hit.Rating, details.Certification, trailer,
                                            hit.PosterPath, hit.BackdropPath, (details.RuntimeMinutes ?? 0) * 60L * ClipPicker.TicksPerSecond, seed.TitleId, seed.Name));
        }

        return result;
    }

    private async Task<string?> SeerrAsync(SeerrClient seerr, string path, string query, CancellationToken token)
    {
        var url = "seerr:" + path + query;
        if (Cached(url, out var json))
        {
            return json;
        }

        try
        {
            using var response = await seerr.SendAsync(HttpMethod.Get, path, query, null, null, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            json = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token).ConfigureAwait(false) : null;
        }
        catch (SeerrUnreachableException)
        {
            return null;
        }

        Keep(url, json);
        return json;
    }

    private async Task<string?> TmdbAsync(string key, RequestHit hit, string language, CancellationToken token)
    {
        var append = hit.MediaType == "movie" ? "videos,release_dates" : "videos,content_ratings";
        var query = $"append_to_response={append}&language={Uri.EscapeDataString(language)}&include_video_language={Uri.EscapeDataString(language)},en,null";
        var url = $"tmdb:{hit.MediaType}/{hit.TmdbId}?{query}";
        if (Cached(url, out var json))
        {
            return json;
        }

        var bearer = key.Length > 40;
        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.themoviedb.org/3/{hit.MediaType}/{hit.TmdbId}?{query}" + (bearer ? string.Empty : "&api_key=" + Uri.EscapeDataString(key)));
        request.Headers.Accept.ParseAdd("application/json");
        if (bearer)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        }

        var client = _http.CreateClient(SeerrClient.HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(20);
        try
        {
            using var response = await client.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            json = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(token).ConfigureAwait(false) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }

        Keep(url, json);
        return json;
    }

    private static JsonDocument? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Cached(string url, out string? json)
    {
        if (Lookups.TryGetValue(url, out var hit) && DateTime.UtcNow - hit.At < (url.StartsWith("seerr:", StringComparison.Ordinal) ? SeerrLifetime : LookupLifetime))
        {
            json = hit.Json;
            return true;
        }

        json = null;
        return false;
    }

    private static void Keep(string url, string? json)
    {
        if (Lookups.Count > 5000)
        {
            Lookups.Clear();
        }

        Lookups[url] = (DateTime.UtcNow, json);
    }
}
