using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>Fills a title's best scenes when too few were responded to.</summary>
public static class ArcsTop
{
    /// <summary>
    /// Up to <paramref name="count"/> clips from each source's candidates (an episode's, or a
    /// film's): every cold open first, in source order, then clips at named chapters, then the rest,
    /// taking one from each source in turn so a show's best scenes aren't all from one episode.
    /// </summary>
    public static List<T> Fill<T>(IReadOnlyList<List<T>> sources, int count, Func<T, ClipCandidate> candidate)
    {
        var result = new List<T>();
        if (count <= 0)
        {
            return result;
        }

        var tiers = new Func<T, bool>[]
        {
            c => candidate(c).IsColdOpen,
            c => !candidate(c).IsColdOpen && candidate(c).ChapterName is not null,
            c => !candidate(c).IsColdOpen && candidate(c).ChapterName is null,
        };
        foreach (var tier in tiers)
        {
            var queues = sources.Select(s => new Queue<T>(s.Where(tier))).ToList();
            while (result.Count < count && queues.Any(q => q.Count > 0))
            {
                foreach (var queue in queues.Where(q => q.Count > 0))
                {
                    result.Add(queue.Dequeue());
                    if (result.Count == count)
                    {
                        return result;
                    }
                }
            }
        }

        return result;
    }
}

/// <summary>One person's Arcs totals, from their own kept events, likes and reactions.</summary>
public sealed record ArcsTotalsResult(int Scenes, int Minutes, int Likes, int Shares, int WatchFromHere, List<(Guid TitleId, int Scenes)> Titles,
                                      List<string> Rewatched, string? TopReaction, DateTime? Since);

public static class ArcsTotals
{
    public static ArcsTotalsResult For(ArcsUserData data)
    {
        var events = data.Events.Where(e => e.ClipId.Length > 0 && !RequestKey.IsRequest(e.ClipId)).ToList();
        var views = events.Where(e => e.Kind is ArcEventKinds.View or ArcEventKinds.Rewatch).ToList();
        var seconds = views.Sum(e => e.WatchedSeconds ?? (e.Kind == ArcEventKinds.Rewatch ? e.ClipSeconds ?? 0 : 0));
        var titles = views.Where(e => e.TitleId != Guid.Empty).GroupBy(e => e.TitleId)
            .Select(g => (TitleId: g.Key, Scenes: g.Select(e => e.ClipId).Distinct(StringComparer.Ordinal).Count()))
            .OrderByDescending(t => t.Scenes).ThenBy(t => t.TitleId).ToList();
        var rewatched = events.Where(e => e.Kind == ArcEventKinds.Rewatch).GroupBy(e => e.ClipId, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(e => e.At)).Select(g => g.Key).ToList();
        var reaction = data.Reactions.GroupBy(r => r.Reaction, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => Array.IndexOf(ArcReactionKinds.All, g.Key)).Select(g => g.Key).FirstOrDefault();
        var dates = data.Events.Select(e => e.At).Concat(data.Likes.Select(l => l.At)).Concat(data.Reactions.Select(r => r.At)).ToList();
        return new ArcsTotalsResult(
            views.Select(e => e.ClipId).Distinct(StringComparer.Ordinal).Count(),
            (int)Math.Round(seconds / 60),
            data.Likes.Count,
            events.Count(e => e.Kind == ArcEventKinds.Share),
            events.Count(e => ArcEventKinds.IsWatchOn(e.Kind)),
            titles,
            rewatched,
            reaction,
            dates.Count == 0 ? null : dates.Min());
    }
}
