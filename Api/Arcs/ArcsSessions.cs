using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>A feed cursor: which session it belongs to and which page comes next. Opaque to the app.</summary>
public readonly record struct ArcCursor(int Seed, int Page)
{
    public string Encode()
    {
        var raw = string.Create(CultureInfo.InvariantCulture, $"a1.{Seed}.{Page}");
        return Convert.ToBase64String(Encoding.ASCII.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? value, out ArcCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return false;
        }

        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var parts = Encoding.ASCII.GetString(Convert.FromBase64String(padded)).Split('.');
            if (parts.Length != 3 || parts[0] != "a1"
                || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seed)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page > 10_000)
            {
                return false;
            }

            cursor = new ArcCursor(seed, page);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>One served page: its clip ids and titles, and the ranking version it was made from.</summary>
public sealed record ArcPage(int Version, List<string> ClipIds, List<Guid> TitleIds, object? Data = null);

/// <summary>
/// One run through a feed. Asking for the same page again gives the same clips unless the person
/// did something since (then the page is made again from the new ranking, leaving out what earlier
/// pages showed), so a refill never repeats and new likes show up straight away.
/// </summary>
public sealed class ArcSession
{
    public ArcSession(int seed, DateTime now)
    {
        Seed = seed;
        Touched = now;
    }

    public int Seed { get; }

    public DateTime Touched { get; set; }

    public List<ArcPage> Pages { get; } = new();

    /// <summary>The stored page, if it still stands for <paramref name="version"/> and holds nothing in <paramref name="exclude"/>.</summary>
    public ArcPage? Stored(int page, int version, ISet<string> exclude)
    {
        if (page < Pages.Count && Pages[page].Version == version && !Pages[page].ClipIds.Any(exclude.Contains))
        {
            return Pages[page];
        }

        return null;
    }

    /// <summary>Clip ids shown before <paramref name="page"/>.</summary>
    public HashSet<string> ShownBefore(int page) => Pages.Take(page).SelectMany(p => p.ClipIds).ToHashSet(StringComparer.Ordinal);

    /// <summary>How often each title was shown before <paramref name="page"/>.</summary>
    public Dictionary<Guid, int> TitlesBefore(int page)
    {
        return Pages.Take(page).SelectMany(p => p.TitleIds).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>How many clips the pages before <paramref name="page"/> showed.</summary>
    public int CountBefore(int page) => Pages.Take(page).Sum(p => p.ClipIds.Count);

    public string? LastClipBefore(int page) => page > 0 && page - 1 < Pages.Count ? Pages[page - 1].ClipIds.LastOrDefault() : null;

    public Guid? LastTitleBefore(int page) => page > 0 && page - 1 < Pages.Count ? Pages[page - 1].TitleIds.LastOrDefault() : null;

    /// <summary>Keeps <paramref name="made"/> as page <paramref name="page"/>; later pages are dropped, as they followed the old one.</summary>
    public void Store(int page, ArcPage made)
    {
        if (page < Pages.Count)
        {
            Pages.RemoveRange(page, Pages.Count - page);
        }

        while (Pages.Count < page)
        {
            Pages.Add(new ArcPage(-1, new List<string>(), new List<Guid>()));
        }

        Pages.Add(made);
    }

    /// <summary>The random numbers for one page: the same for the same session and page.</summary>
    public Random RandomFor(int page) => new(unchecked((Seed * 486_187_739) + (page * 16_777_619)));
}
