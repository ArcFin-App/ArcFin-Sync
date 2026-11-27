using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Integrations;

/// <summary>What a list row needs to know about a title: its name, artwork and age ratings by country.</summary>
public sealed class SeerrTitleCard
{
    public string? Title { get; set; }

    public string? PosterPath { get; set; }

    public string? BackdropPath { get; set; }

    /// <summary>Age ratings by country code (e.g. "GB" → "15"), from TMDB's release dates or content ratings.</summary>
    public Dictionary<string, string> Ratings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTime Fetched { get; set; }

    /// <summary>The rating for a country, else the US one, else any; empty when TMDB has none.</summary>
    public string Rating(string region)
        => Ratings.TryGetValue(region, out var r) ? r : Ratings.TryGetValue("US", out var us) ? us : Ratings.Values.FirstOrDefault() ?? string.Empty;
}

/// <summary>
/// Seerr's request list and watchlist carry only TMDB ids, so a client had to ask about each title
/// on its own: hundreds of calls for one page. The server answers for a whole page instead,
/// looking titles up side by side over Seerr's local address, and keeps what it found for a week,
/// for everyone and across restarts (a file in the plugin's data folder).
/// </summary>
public static class SeerrTitles
{
    public const int MaxTake = 100;
    private const int Lookups = 16;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private static readonly object FileGate = new();
    private static ConcurrentDictionary<string, SeerrTitleCard>? _cards;
    private static int _dirty;

    public static string Key(string mediaType, int tmdbId) => $"{(mediaType == "tv" ? "tv" : "movie")}:{tmdbId}";

    /// <summary>The (media type, TMDB id) of each row, in order; null where a row has none. A row's ids are in its "media" (requests) or on the row itself (watchlist).</summary>
    public static List<(string Type, int Id)?> Titles(JsonNode page)
    {
        var rows = new List<(string, int)?>();
        if (page["results"] is not JsonArray results)
        {
            return rows;
        }

        foreach (var row in results)
        {
            var ids = row?["media"] ?? row;
            var type = ids?["mediaType"] is JsonValue t && t.TryGetValue<string>(out var s) && s == "tv" ? "tv" : "movie";
            rows.Add(ids?["tmdbId"] is JsonValue id && id.TryGetValue<int>(out var tmdb) ? (type, tmdb) : null);
        }

        return rows;
    }

    /// <summary>A title's card from Seerr's movie or show detail.</summary>
    public static SeerrTitleCard? Card(JsonNode? detail)
    {
        var title = Text(detail?["title"]) ?? Text(detail?["name"]);
        if (detail is null || title is null)
        {
            return null;
        }

        var card = new SeerrTitleCard { Title = title, PosterPath = Text(detail["posterPath"]), BackdropPath = Text(detail["backdropPath"]), Fetched = DateTime.UtcNow };
        if (detail["releases"]?["results"] is JsonArray releases)
        {
            foreach (var entry in releases)
            {
                var rating = (entry?["release_dates"] as JsonArray)?.Select(d => Text(d?["certification"])).FirstOrDefault(c => !string.IsNullOrEmpty(c));
                if (Text(entry?["iso_3166_1"]) is { } country && rating is not null)
                {
                    card.Ratings[country] = rating;
                }
            }
        }

        if (detail["contentRatings"]?["results"] is JsonArray ratings)
        {
            foreach (var entry in ratings)
            {
                if (Text(entry?["iso_3166_1"]) is { } country && Text(entry?["rating"]) is { Length: > 0 } rating)
                {
                    card.Ratings[country] = rating;
                }
            }
        }

        return card;
    }

    /// <summary>Adds an "arcfin" object with the title and artwork to each row there's a card for.</summary>
    public static void Fill(JsonNode page, IReadOnlyDictionary<string, SeerrTitleCard> cards)
    {
        if (page["results"] is not JsonArray results)
        {
            return;
        }

        var titles = Titles(page);
        for (var i = 0; i < results.Count; i++)
        {
            if (titles[i] is { } t && results[i] is JsonObject row && cards.TryGetValue(Key(t.Type, t.Id), out var card))
            {
                row["arcfin"] = new JsonObject { ["title"] = card.Title, ["posterPath"] = card.PosterPath, ["backdropPath"] = card.BackdropPath };
            }
        }
    }

    /// <summary>Cards for these titles: kept ones at once, the rest looked up side by side and kept.</summary>
    public static async Task<Dictionary<string, SeerrTitleCard>> CardsAsync(SeerrClient seerr, IEnumerable<(string Type, int Id)> titles, CancellationToken token)
    {
        var store = Store();
        var now = DateTime.UtcNow;
        var found = new ConcurrentDictionary<string, SeerrTitleCard>(StringComparer.Ordinal);
        var missing = new List<(string Key, string Type, int Id)>();
        foreach (var (type, id) in titles.Distinct())
        {
            var key = Key(type, id);
            if (store.TryGetValue(key, out var card) && now - card.Fetched < Lifetime)
            {
                found[key] = card;
            }
            else
            {
                missing.Add((key, type == "tv" ? "tv" : "movie", id));
            }
        }

        await Parallel.ForEachAsync(missing, new ParallelOptions { MaxDegreeOfParallelism = Lookups, CancellationToken = token }, async (m, ct) =>
        {
            try
            {
                using var response = await seerr.SendAsync(HttpMethod.Get, $"{m.Type}/{m.Id}", null, null, null, ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode && Card(JsonNode.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false))) is { } card)
                {
                    found[m.Key] = card;
                    store[m.Key] = card;
                    Interlocked.Exchange(ref _dirty, 1);
                }
            }
            catch (Exception error) when (error is HttpRequestException or SeerrUnreachableException or System.Text.Json.JsonException
                                          or InvalidOperationException || error is TaskCanceledException && !ct.IsCancellationRequested)
            {
            }
        }).ConfigureAwait(false);

        Save();
        return new Dictionary<string, SeerrTitleCard>(found, StringComparer.Ordinal);
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string? FilePath()
        => Plugin.Instance?.DataFolderPath is { } folder ? Path.Combine(folder, "seerr", "titles.json") : null;

    private static ConcurrentDictionary<string, SeerrTitleCard> Store()
    {
        if (_cards is { } cards)
        {
            return cards;
        }

        lock (FileGate)
        {
            if (_cards is null)
            {
                var saved = FilePath() is { } path ? JsonFile.Read<Dictionary<string, SeerrTitleCard>>(path) : null;
                _cards = new ConcurrentDictionary<string, SeerrTitleCard>(saved ?? new Dictionary<string, SeerrTitleCard>(), StringComparer.Ordinal);
            }

            return _cards;
        }
    }

    private static void Save()
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0 || FilePath() is not { } path || _cards is not { } cards)
        {
            return;
        }

        lock (FileGate)
        {
            var cutoff = DateTime.UtcNow - Lifetime;
            JsonFile.Write(path, cards.Where(c => c.Value.Fetched > cutoff).ToDictionary(c => c.Key, c => c.Value));
        }
    }
}

/// <summary>
/// Seerr's answers to reads that are the same for a while (Discover rows, search, a title's page),
/// kept a few minutes, so opening Discover again doesn't wait on TMDB once more. Calls asking the same
/// thing at the same moment share one trip. A change made through the server (a request, an
/// approval, a watchlist edit) clears it.
/// </summary>
public static class SeerrAnswers
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private const int MaxEntries = 600;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (Task<SeerrAnswer> Answer, DateTime Asked)> Entries = new(StringComparer.Ordinal);

    public static bool Keeps(string path)
        => path.StartsWith("discover/", StringComparison.Ordinal) || path == "search" || path.StartsWith("genres/", StringComparison.Ordinal)
           || path.StartsWith("watchproviders/", StringComparison.Ordinal) || path.StartsWith("person/", StringComparison.Ordinal)
           || path.StartsWith("collection/", StringComparison.Ordinal) || path == "settings/public"
           || System.Text.RegularExpressions.Regex.IsMatch(path, @"^(movie|tv)/\d+(/.*)?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static Task<SeerrAnswer> GetAsync(string key, Func<Task<SeerrAnswer>> ask)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (Entries.TryGetValue(key, out var entry) && now - entry.Asked < Lifetime
                && !(entry.Answer.IsCompleted && (!entry.Answer.IsCompletedSuccessfully || entry.Answer.Result.Status != 200)))
            {
                return entry.Answer;
            }

            if (Entries.Count >= MaxEntries)
            {
                foreach (var old in Entries.OrderBy(e => e.Value.Asked).Take(MaxEntries / 4).Select(e => e.Key).ToList())
                {
                    Entries.Remove(old);
                }
            }

            var answer = ask();
            Entries[key] = (answer, now);
            return answer;
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }
}

public sealed record SeerrAnswer(int Status, string? ContentType, byte[] Body);
