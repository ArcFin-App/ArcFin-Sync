using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ArcfinSync.Api.Recommendations;

/// <summary>What the recommender knows about a title.</summary>
public sealed record TitleFeatures(
    Guid Id,
    string Name,
    bool IsSeries,
    IReadOnlyCollection<string> Genres,
    IReadOnlyCollection<string> People,
    IReadOnlyCollection<string> Tags,
    IReadOnlyCollection<string> Studios,
    double? Rating,
    int? Year,
    DateTime Added);

/// <summary>
/// One thing the user did with a title: watched it (how long ago), liked or disliked it, or
/// made it a favourite. Likes are Jellyfin's own per-user rating, the same one the apps set.
/// </summary>
public sealed record Signal(TitleFeatures Title, bool Watched, bool? Likes, bool Favorite, DateTime? LastPlayed);

/// <summary>
/// Picks titles for one person from what they've watched and rated, scored on shared genres,
/// people, tags and studios, nudged by rating and recency. A first cut: simple and explainable,
/// so it can be tuned against real libraries.
/// </summary>
public static class Recommender
{
    private const double GenreWeight = 1.0;
    private const double PersonWeight = 1.6;
    private const double TagWeight = 0.5;
    private const double StudioWeight = 0.4;
    private static readonly TimeSpan HalfLife = TimeSpan.FromDays(120);

    /// <summary>How much each feature counts for this person; disliked titles push theirs down.</summary>
    public static Dictionary<string, double> Profile(IEnumerable<Signal> signals, DateTime now) => Profile(signals, now, 1, 1, 1);

    /// <summary>The same, with history, likes and favourites each scaled by an admin's weight.</summary>
    public static Dictionary<string, double> Profile(IEnumerable<Signal> signals, DateTime now, double history, double likes, double favorites)
    {
        var profile = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var signal in signals)
        {
            AddToProfile(profile, signal.Title, SignalWeight(signal, now, history, likes, favorites));
        }

        return profile;
    }

    /// <summary>Adds one title's features to a profile, e.g. for something done with it outside Jellyfin's own history.</summary>
    public static void AddToProfile(Dictionary<string, double> profile, TitleFeatures title, double weight)
    {
        if (weight == 0)
        {
            return;
        }

        foreach (var (feature, featureWeight) in Features(title))
        {
            profile[feature] = profile.GetValueOrDefault(feature) + weight * featureWeight;
        }
    }

    public static double SignalWeight(Signal signal, DateTime now) => SignalWeight(signal, now, 1, 1, 1);

    public static double SignalWeight(Signal signal, DateTime now, double history, double likes, double favorites)
    {
        if (signal.Likes == false)
        {
            return -3 * likes;
        }

        var weight = 0.0;
        if (signal.Watched)
        {
            var age = signal.LastPlayed is { } played ? Math.Max(0, (now - played).TotalDays) : 365;
            weight += (Math.Pow(0.5, age / HalfLife.TotalDays) + 0.25) * history;
        }

        if (signal.Likes == true)
        {
            weight += 2.5 * likes;
        }

        if (signal.Favorite)
        {
            weight += 2 * favorites;
        }

        return weight;
    }

    /// <summary>A candidate's score against a profile: overlap, scaled so big profiles don't swamp it, plus a rating nudge.</summary>
    public static double Score(Dictionary<string, double> profile, TitleFeatures candidate)
    {
        if (profile.Count == 0)
        {
            return RatingNudge(candidate);
        }

        var scale = Math.Sqrt(profile.Values.Where(v => v > 0).Sum() + 1);
        var overlap = Features(candidate).Sum(f => profile.GetValueOrDefault(f.Feature) * f.Weight);
        return overlap / scale + RatingNudge(candidate);
    }

    /// <summary>How alike two titles are, for "Because you watched".</summary>
    public static double Similarity(TitleFeatures seed, TitleFeatures candidate)
    {
        var seedFeatures = Features(seed).ToDictionary(f => f.Feature, f => f.Weight, StringComparer.OrdinalIgnoreCase);
        var shared = Features(candidate).Where(f => seedFeatures.ContainsKey(f.Feature)).Sum(f => f.Weight);
        var size = Math.Sqrt(seedFeatures.Values.Sum() * Features(candidate).Sum(f => f.Weight) + 1);
        var sameKind = seed.IsSeries == candidate.IsSeries ? 0.15 : 0;
        var era = seed.Year is { } a && candidate.Year is { } b ? Math.Max(0, 0.15 - Math.Abs(a - b) * 0.01) : 0;
        return shared / size + sameKind + era + RatingNudge(candidate) * 0.5;
    }

    /// <summary>The best <paramref name="count"/> candidates, keeping a mix of genres rather than ten of the same.</summary>
    public static List<TitleFeatures> Top(IEnumerable<(TitleFeatures Title, double Score)> scored, int count)
    {
        var picked = new List<TitleFeatures>();
        var genreUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (title, score) in scored.OrderByDescending(s => s.Score).ThenBy(s => s.Title.Id))
        {
            if (score <= 0)
            {
                break;
            }

            var lead = title.Genres.FirstOrDefault() ?? string.Empty;
            if (lead.Length > 0 && genreUse.GetValueOrDefault(lead) >= Math.Max(3, count / 3))
            {
                continue;
            }

            picked.Add(title);
            genreUse[lead] = genreUse.GetValueOrDefault(lead) + 1;
            if (picked.Count == count)
            {
                break;
            }
        }

        return picked;
    }

    /// <summary>The lead genres a profile leans on most, strongest first.</summary>
    public static List<string> TopGenres(Dictionary<string, double> profile, int count)
    {
        return profile.Where(p => p.Key.StartsWith("g:", StringComparison.Ordinal) && p.Value > 0)
            .OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Take(count).Select(p => p.Key[2..]).ToList();
    }

    public static double RatingNudge(TitleFeatures title) => title.Rating is { } r ? (r - 6.5) * 0.12 : 0;

    private static IEnumerable<(string Feature, double Weight)> Features(TitleFeatures title)
    {
        foreach (var g in title.Genres)
        {
            yield return ("g:" + g, GenreWeight);
        }

        foreach (var p in title.People)
        {
            yield return ("p:" + p, PersonWeight);
        }

        foreach (var t in title.Tags)
        {
            yield return ("t:" + t, TagWeight);
        }

        foreach (var s in title.Studios)
        {
            yield return ("s:" + s, StudioWeight);
        }
    }
}
