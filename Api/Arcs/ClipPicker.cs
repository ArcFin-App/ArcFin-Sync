using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

public sealed record ChapterMark(long StartTicks, string? Name);

/// <summary>A stretch to keep clips out of: an intro, recap, credits, preview or advert.</summary>
/// <summary>A stretch clips stay out of; <paramref name="IsIntro"/> marks a title sequence, after which an episode's cold open ends.</summary>
public sealed record SkipRange(long StartTicks, long EndTicks, bool IsIntro = false);

/// <summary>A clip start; <paramref name="IsColdOpen"/> marks an episode's scene before its title sequence, which makes a good first look.</summary>
public sealed record ClipCandidate(long StartTicks, long EndTicks, string? ChapterName, bool IsColdOpen = false);

/// <summary>Where clips of one film or episode start and end. Free of Jellyfin types so it can be tested on its own.</summary>
public static class ClipPicker
{
    public const long TicksPerSecond = 10_000_000;
    private const long ChapterLeadIn = 2 * TicksPerSecond;
    private const long LineLeadIn = 4 * TicksPerSecond;
    private const double SkipHead = 0.06;
    private const double SkipTail = 0.08;
    /// <summary>Never less than this from the start: logos, recaps and title sequences sit there even when nothing marks them.</summary>
    private const long MinHeadTicks = 90 * TicksPerSecond;
    private static readonly double[] EvenPoints = { 0.15, 0.25, 0.35, 0.45, 0.55, 0.65 };
    private static readonly Regex SkipNames = new(
        @"\b(intro|opening|recap|previously|credits?|end ?credits|ending|epilogue|outro|preview|next (time|episode)|sponsor|advert|commercial|logos?|theme( song)?|title sequence|main titles?|opening titles?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The requested length, or the default, kept inside the admin's limits.</summary>
    public static int Seconds(int? requested, int min, int max, int fallback)
    {
        if (max < min)
        {
            max = min;
        }

        return Math.Clamp(requested ?? fallback, min, max);
    }

    /// <summary>
    /// Clip starts for one title: each chapter's start plus two seconds, as long as the chapter or
    /// the requested length, whichever is shorter (never under <paramref name="minSeconds"/>).
    /// Chapters inside an intro, recap, credits, preview or advert are skipped; with no segments
    /// the first 6 % and last 8 % are. Titles without usable chapters use the scene index, or
    /// points 20, 40 and 60 % in. Nothing ends after <paramref name="latestEndTicks"/> (the
    /// spoiler limit), and reported starts are never used.
    /// </summary>
    public static List<ClipCandidate> Candidates(
        long runtimeTicks,
        IReadOnlyList<ChapterMark> chapters,
        IReadOnlyList<SkipRange> segments,
        IReadOnlyList<long>? sceneStarts,
        int seconds,
        int minSeconds,
        long latestEndTicks,
        ISet<long>? reportedStarts = null,
        bool isEpisode = false)
    {
        var result = new List<ClipCandidate>();
        if (runtimeTicks <= 0 || seconds <= 0)
        {
            return result;
        }

        var skips = SkipRanges(runtimeTicks, segments, isEpisode);
        var length = seconds * TicksPerSecond;
        var shortest = Math.Min(minSeconds, seconds) * TicksPerSecond;
        var limit = Math.Min(runtimeTicks, latestEndTicks);
        bool Fits(long start, long end) => end <= limit && start >= 0 && !skips.Any(s => start < s.EndTicks && end > s.StartTicks)
                                            && reportedStarts?.Contains(start) != true
                                            && !result.Any(c => start < c.EndTicks && end > c.StartTicks);

        // An episode's cold open: the scene before its marked title sequence, past any recap.
        if (ColdOpen(runtimeTicks, segments, isEpisode) is { } cold)
        {
            var start = cold.StartTicks;
            var clipLength = Math.Min(cold.EndTicks - start, length) / TicksPerSecond * TicksPerSecond;
            if (clipLength >= Math.Min(10 * TicksPerSecond, shortest) && Fits(start, start + clipLength))
            {
                var name = chapters.Where(c => c.StartTicks <= start).OrderByDescending(c => c.StartTicks).FirstOrDefault()?.Name;
                result.Add(new ClipCandidate(start, start + clipLength, string.IsNullOrWhiteSpace(name) || SkipNames.IsMatch(name) ? null : name, true));
            }
        }

        foreach (var (chapter, chapterEnd) in ContentChapters(chapters, runtimeTicks, minSeconds, HeadTicks(runtimeTicks, segments, isEpisode)))
        {
            var start = chapter.StartTicks + ChapterLeadIn;
            var clipLength = Math.Max(shortest, Math.Min(chapterEnd - start, length)) / TicksPerSecond * TicksPerSecond;
            if (Fits(start, start + clipLength))
            {
                result.Add(new ClipCandidate(start, start + clipLength, string.IsNullOrWhiteSpace(chapter.Name) ? null : chapter.Name));
            }
        }

        // Few real chapters (or only markers some tools write for intros and credits): scene changes,
        // or points spread through the title, fill in.
        if (result.Count < 3)
        {
            var points = sceneStarts is { Count: > 0 }
                ? sceneStarts.OrderBy(s => s)
                : EvenPoints.Select(p => RoundToSecond((long)(runtimeTicks * p)));
            foreach (var start in points)
            {
                if (Fits(start, start + length))
                {
                    result.Add(new ClipCandidate(start, start + length, null));
                }
            }
        }

        return result.OrderBy(c => c.StartTicks).ToList();
    }

    /// <summary>
    /// The chapters that are scenes, each with where it ends: duplicates at the same time dropped,
    /// along with ones named for intros, recaps, credits and the like, ones too short for a clip,
    /// and ones at the very start (a single "Main" or "Prologue" chapter covering the opening).
    /// </summary>
    public static List<(ChapterMark Chapter, long EndTicks)> ContentChapters(IReadOnlyList<ChapterMark> chapters, long runtimeTicks, int minSeconds,
                                                                          long? headTicks = null)
    {
        var distinct = chapters.Where(c => c.StartTicks >= 0 && c.StartTicks < runtimeTicks)
            .GroupBy(c => c.StartTicks / TicksPerSecond)
            .Select(g => g.First())
            .OrderBy(c => c.StartTicks)
            .ToList();
        var head = headTicks ?? HeadTicks(runtimeTicks);
        var result = new List<(ChapterMark, long)>();
        for (var i = 0; i < distinct.Count; i++)
        {
            var chapter = distinct[i];
            var end = i + 1 < distinct.Count ? distinct[i + 1].StartTicks : runtimeTicks;
            if (chapter.Name is { Length: > 0 } name && SkipNames.IsMatch(name))
            {
                continue;
            }

            if (chapter.StartTicks < head || end - chapter.StartTicks < (minSeconds * TicksPerSecond) + ChapterLeadIn)
            {
                continue;
            }

            result.Add((chapter, end));
        }

        return result;
    }

    private static long HeadTicks(long runtimeTicks) => Math.Min(Math.Max((long)(runtimeTicks * SkipHead), MinHeadTicks), runtimeTicks / 4);

    /// <summary>
    /// How much of the start clips stay out of. An episode whose title sequence is marked needs
    /// none: the segments say where the intro and recap are, and what's before them is its cold
    /// open. Films, and episodes nothing marks, skip their opening (logos, recaps, titles).
    /// </summary>
    public static long HeadTicks(long runtimeTicks, IReadOnlyList<SkipRange> segments, bool isEpisode) =>
        isEpisode && segments.Any(s => s.IsIntro) ? 0 : HeadTicks(runtimeTicks);

    /// <summary>The stretch before an episode's marked title sequence, from after any recap, when there's one worth a clip.</summary>
    public static SkipRange? ColdOpen(long runtimeTicks, IReadOnlyList<SkipRange> segments, bool isEpisode)
    {
        if (!isEpisode || segments.Where(s => s.IsIntro).OrderBy(s => s.StartTicks).FirstOrDefault() is not { } intro || intro.StartTicks < 20 * TicksPerSecond)
        {
            return null;
        }

        // Past anything else marked before the intro (a "previously on" recap), plus the usual lead-in.
        var start = segments.Where(s => !s.IsIntro && s.StartTicks < intro.StartTicks).Select(s => s.EndTicks).DefaultIfEmpty(0).Max() + ChapterLeadIn;
        return intro.StartTicks - start >= 10 * TicksPerSecond && start < runtimeTicks ? new SkipRange(start, intro.StartTicks) : null;
    }

    /// <summary>
    /// What clips stay out of: the title's segments, its opening (6 %, at least 90 s; not for an
    /// episode whose intro is marked, so its cold open can be used) and always its last 8 %.
    /// Segments often mark only some of what's there, such as the credits but not the logos.
    /// </summary>
    public static List<SkipRange> SkipRanges(long runtimeTicks, IReadOnlyList<SkipRange> segments, bool isEpisode = false)
    {
        var result = segments.Where(s => s.EndTicks > s.StartTicks).ToList();
        if (HeadTicks(runtimeTicks, segments, isEpisode) is > 0 and var head)
        {
            result.Add(new SkipRange(0, head));
        }

        result.Add(new SkipRange((long)(runtimeTicks * (1 - SkipTail)), runtimeTicks));
        return result;
    }

    /// <summary>
    /// Picks good starts from a scene-change scan: inside the part clips may come from, at least a
    /// minute apart, spread over the title. Starts are whole seconds so clip ids stay short.
    /// </summary>
    public static List<long> SpreadSceneStarts(IEnumerable<double> sceneSeconds, double runtimeSeconds, int count)
    {
        var from = HeadTicks((long)(runtimeSeconds * TicksPerSecond)) / (double)TicksPerSecond;
        var to = runtimeSeconds * (1 - SkipTail) - 60;
        var usable = sceneSeconds.Where(s => s >= from && s <= to).OrderBy(s => s).ToList();
        var picked = new List<double>();
        if (usable.Count == 0 || count <= 0)
        {
            return new List<long>();
        }

        var step = (to - from) / count;
        for (var i = 0; i < count; i++)
        {
            var target = from + step * (i + 0.5);
            var best = usable.OrderBy(s => Math.Abs(s - target)).First();
            if (picked.All(p => Math.Abs(p - best) >= 60))
            {
                picked.Add(best);
            }
        }

        return picked.OrderBy(p => p).Select(p => (long)Math.Round(p) * TicksPerSecond).Distinct().ToList();
    }

    private static long RoundToSecond(long ticks) => ticks / TicksPerSecond * TicksPerSecond;

    /// <summary>
    /// A clip for a line of dialogue: from about four seconds before it, on a whole second, always
    /// the requested length. None when the line is itself in an intro, recap, credits or the like,
    /// or past the spoiler limit. A skipped stretch just before the line moves the start up to its
    /// end; one soon after the line (or the spoiler limit) moves the clip earlier so it ends there,
    /// and when it can't fit between the two there's no clip.
    /// </summary>
    public static ClipCandidate? AroundLine(long runtimeTicks, IReadOnlyList<SkipRange> segments, bool isEpisode, long lineStart, long lineEnd, int seconds,
                                            long latestEndTicks, ISet<long>? reportedStarts = null)
    {
        if (runtimeTicks <= 0 || seconds <= 0 || lineStart < 0 || lineStart >= runtimeTicks)
        {
            return null;
        }

        lineEnd = Math.Min(Math.Max(lineEnd, lineStart + TicksPerSecond), runtimeTicks);
        var skips = SkipRanges(runtimeTicks, segments, isEpisode);
        var limit = Math.Min(runtimeTicks, latestEndTicks);
        if (lineEnd > limit || skips.Any(s => lineStart < s.EndTicks && lineEnd > s.StartTicks))
        {
            return null;
        }

        var length = seconds * TicksPerSecond;
        var low = Math.Min(skips.Where(s => s.EndTicks <= lineStart).Select(s => (s.EndTicks + TicksPerSecond - 1) / TicksPerSecond * TicksPerSecond).DefaultIfEmpty(0).Max(),
                           RoundToSecond(lineStart));
        var high = Math.Min(limit, skips.Where(s => s.StartTicks >= lineEnd).Select(s => s.StartTicks).DefaultIfEmpty(long.MaxValue).Min());
        var start = Math.Max(RoundToSecond(Math.Max(0, lineStart - LineLeadIn)), low);
        if (start + length > high)
        {
            start = RoundToSecond(high - length);
        }

        var end = start + length;
        if (start < low || start > lineStart || end < lineEnd || reportedStarts?.Contains(start) == true)
        {
            return null;
        }

        return new ClipCandidate(start, end, null);
    }
}

/// <summary>Where the person has got to in a show.</summary>
public sealed record SeriesProgress(int Season, int Episode);

/// <summary>Spoiler rules: what a clip may show of something the person hasn't watched yet.</summary>
public static class SpoilerRules
{
    private const double UnwatchedFilmShare = 0.75;

    /// <summary>
    /// An episode they've played, or one at or before the furthest they've played in that show.
    /// A show they haven't started gives season 1 episodes 1 to 3 only. Specials count only once
    /// played, since they can sit anywhere in a story.
    /// </summary>
    public static bool AllowsEpisode(bool spoilerSafe, bool played, int? season, int? episode, SeriesProgress? furthest)
    {
        if (!spoilerSafe || played)
        {
            return true;
        }

        if (season is not { } s || episode is not { } e || s == 0)
        {
            return false;
        }

        if (furthest is null)
        {
            return s == 1 && e <= 3;
        }

        return s < furthest.Season || (s == furthest.Season && e <= furthest.Episode);
    }

    /// <summary>The latest a clip may end: anywhere in a watched film, never in an unwatched film's last quarter.</summary>
    public static long LatestEnd(bool spoilerSafe, bool isMovie, bool played, long runtimeTicks)
    {
        return spoilerSafe && isMovie && !played ? (long)(runtimeTicks * UnwatchedFilmShare) : runtimeTicks;
    }
}
