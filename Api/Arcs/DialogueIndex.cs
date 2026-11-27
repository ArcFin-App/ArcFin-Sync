using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>One subtitle line: when it shows and what it says, tags taken out.</summary>
public sealed record SubtitleCue(int StartMs, int EndMs, string Text);

/// <summary>One language's subtitle lines for a film or episode, stored once: parallel lists of starts, lengths and text.</summary>
public sealed class DialogueTrack
{
    /// <summary>The language as Jellyfin has it (often three letters), or "und".</summary>
    public string Lang { get; set; } = "und";

    public List<int> Start { get; set; } = new();

    public List<int> Dur { get; set; } = new();

    public List<string> Text { get; set; } = new();
}

/// <summary>A film's or episode's file in the dialogue index.</summary>
public sealed class DialogueItem
{
    public Guid ItemId { get; set; }

    public List<DialogueTrack> Tracks { get; set; } = new();
}

/// <summary>A title's line in the index's list: what was indexed, and a word filter per track so a search reads only files that may match.</summary>
public sealed class DialogueEntry
{
    public Guid ItemId { get; set; }

    /// <summary>The media file and the subtitle streams picked, as they were when indexed; anything else means it's indexed again.</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>When ffmpeg last couldn't read the title's subtitles; tried again a week later.</summary>
    public DateTime? FailedAt { get; set; }

    public List<DialogueTrackInfo> Tracks { get; set; } = new();
}

public sealed class DialogueTrackInfo
{
    public string Lang { get; set; } = "und";

    /// <summary>A Bloom filter of the track's words, base64.</summary>
    public string Filter { get; set; } = string.Empty;
}

/// <summary>A line that matched: which cue (and the next one too, for a match across two), and whether the words came in order.</summary>
public sealed record DialogueMatch(int Cue, bool Pair, bool Phrase, int StartMs, int EndMs, string Line);

/// <summary>Text the dialogue index and search agree on: lowercase, no accents or apostrophes, words split on anything else.</summary>
public static class DialogueText
{
    private static readonly Regex Tags = new(@"<[^>]*>|\{[^}]*\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>A cue's text for showing: markup and ASS overrides out, line breaks as spaces.</summary>
    public static string Clean(string text)
    {
        var plain = Tags.Replace(text.Replace("\\N", " ", StringComparison.Ordinal).Replace("\\n", " ", StringComparison.Ordinal), string.Empty);
        return string.Join(' ', plain.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        var space = true;
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark || c is '\'' or '’' or '‘' or '`')
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                result.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space)
            {
                result.Append(' ');
                space = true;
            }
        }

        return result.ToString().TrimEnd().Replace("ß", "ss", StringComparison.Ordinal);
    }

    public static string[] Words(string normalized) => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Cues from SRT text (what ffmpeg writes for any text subtitle). Numbers are optional, either
    /// decimal mark works, and cues with no text left are dropped.
    /// </summary>
    public static List<SubtitleCue> ParseSrt(string srt)
    {
        var cues = new List<SubtitleCue>();
        var lines = srt.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var arrow = lines[i].IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0 || !TryTime(lines[i][..arrow], out var start) || !TryTime(lines[i][(arrow + 3)..].Trim().Split(' ')[0], out var end))
            {
                continue;
            }

            var text = new StringBuilder();
            while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !lines[i + 1].Contains("-->", StringComparison.Ordinal))
            {
                text.Append(' ').Append(lines[++i].Trim());
            }

            var clean = Clean(text.ToString());
            if (clean.Length > 0 && Normalize(clean).Length > 0 && end > start)
            {
                cues.Add(new SubtitleCue(start, end, clean.Length > 300 ? clean[..300] : clean));
            }
        }

        return cues.OrderBy(c => c.StartMs).ToList();
    }

    private static bool TryTime(string value, out int ms)
    {
        ms = 0;
        var parts = value.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
        {
            return false;
        }

        if (!int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(parts[^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var total = ((hours * 3600.0) + (minutes * 60.0) + seconds) * 1000;
        if (total is < 0 or > int.MaxValue)
        {
            return false;
        }

        ms = (int)Math.Round(total);
        return true;
    }
}

/// <summary>A small Bloom filter over a track's words, about ten bits a word, so a search skips files that can't match.</summary>
public sealed class WordFilter
{
    private const int Hashes = 7;
    private readonly byte[] _bits;

    private WordFilter(byte[] bits)
    {
        _bits = bits;
    }

    public static WordFilter For(int words)
    {
        var bytes = 64;
        while (bytes * 8 < words * 10 && bytes < 64 * 1024)
        {
            bytes *= 2;
        }

        return new WordFilter(new byte[bytes]);
    }

    public static WordFilter? FromBase64(string value)
    {
        try
        {
            var bits = Convert.FromBase64String(value);
            return bits.Length > 0 && (bits.Length & (bits.Length - 1)) == 0 ? new WordFilter(bits) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public string ToBase64() => Convert.ToBase64String(_bits);

    public void Add(string word)
    {
        foreach (var bit in Bits(word))
        {
            _bits[bit >> 3] |= (byte)(1 << (bit & 7));
        }
    }

    public bool MightHave(string word) => Bits(word).All(bit => (_bits[bit >> 3] & (1 << (bit & 7))) != 0);

    private IEnumerable<int> Bits(string word)
    {
        // FNV-1a, split in two for double hashing.
        var hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(word))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        var h1 = (uint)hash;
        var h2 = (uint)(hash >> 32) | 1;
        var size = (uint)(_bits.Length * 8);
        for (uint i = 0; i < Hashes; i++)
        {
            yield return (int)((h1 + (i * h2)) % size);
        }
    }
}

/// <summary>Matches a search against a track: every word in one cue, or across two cues in a row.</summary>
public static class DialogueMatcher
{
    public static List<DialogueMatch> Find(IReadOnlyList<SubtitleCue> cues, IReadOnlyList<string[]> words, IReadOnlyList<string> normalized, string[] query)
    {
        var result = new List<DialogueMatch>();
        if (query.Length == 0)
        {
            return result;
        }

        var phrase = " " + string.Join(' ', query) + " ";
        var sets = words.Select(w => w.ToHashSet(StringComparer.Ordinal)).ToList();
        var single = new bool[cues.Count];
        for (var i = 0; i < cues.Count; i++)
        {
            if (query.All(sets[i].Contains))
            {
                single[i] = true;
                result.Add(new DialogueMatch(i, false, (" " + normalized[i] + " ").Contains(phrase, StringComparison.Ordinal), cues[i].StartMs, cues[i].EndMs, cues[i].Text));
            }
        }

        for (var i = 0; i + 1 < cues.Count; i++)
        {
            if (single[i] || single[i + 1] || !query.All(q => sets[i].Contains(q) || sets[i + 1].Contains(q)))
            {
                continue;
            }

            var joined = " " + normalized[i] + " " + normalized[i + 1] + " ";
            result.Add(new DialogueMatch(i, true, joined.Contains(phrase, StringComparison.Ordinal), cues[i].StartMs, cues[i + 1].EndMs, cues[i].Text + " " + cues[i + 1].Text));
        }

        return result.OrderBy(m => m.StartMs).ToList();
    }
}

/// <summary>Language codes compared the way people write them: two or three letters, either three-letter form.</summary>
public static class LanguageCodes
{
    private static readonly Dictionary<string, string> Bibliographic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ger"] = "deu", ["fre"] = "fra", ["dut"] = "nld", ["chi"] = "zho", ["cze"] = "ces", ["gre"] = "ell", ["per"] = "fas", ["rum"] = "ron",
        ["slo"] = "slk", ["alb"] = "sqi", ["arm"] = "hye", ["baq"] = "eus", ["bur"] = "mya", ["geo"] = "kat", ["ice"] = "isl", ["mac"] = "mkd",
        ["mao"] = "mri", ["may"] = "msa", ["tib"] = "bod", ["wel"] = "cym",
    };

    public static string Canonical(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "und";
        }

        var value = code.Trim().ToLowerInvariant();
        var dash = value.IndexOfAny(new[] { '-', '_' });
        if (dash > 0)
        {
            value = value[..dash];
        }

        if (value.Length == 2)
        {
            try
            {
                var three = CultureInfo.GetCultureInfo(value).ThreeLetterISOLanguageName;
                if (three.Length == 3)
                {
                    value = three;
                }
            }
            catch (CultureNotFoundException)
            {
            }
        }

        return Bibliographic.TryGetValue(value, out var terminologic) ? terminologic : value;
    }

    public static bool Same(string? a, string? b) => Canonical(a) == Canonical(b);
}

/// <summary>
/// The dialogue index on disk: arcs/dialogue/index.json lists each title with a word filter per
/// track, and arcs/dialogue/{itemId}.json holds its lines. The list is read once and kept until it
/// changes; title files are kept for the last few hundred searched.
/// </summary>
public static class DialogueIndex
{
    private const int TitleCacheSize = 300;
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<Guid, LoadedItem> Loaded = new();
    private static (string Path, DateTime Written, Dictionary<Guid, DialogueEntry> Entries, Dictionary<Guid, WordFilter?[]> Filters)? _cache;

    public static string Folder(string dataFolder) => Path.Combine(dataFolder, "arcs", "dialogue");

    private static string ListPath(string dataFolder) => Path.Combine(Folder(dataFolder), "index.json");

    private static string ItemPath(string dataFolder, Guid itemId) => Path.Combine(Folder(dataFolder), $"{itemId:N}.json");

    public static Dictionary<Guid, DialogueEntry> Load(string dataFolder) => LoadWithFilters(dataFolder).Entries;

    public static (Dictionary<Guid, DialogueEntry> Entries, Dictionary<Guid, WordFilter?[]> Filters) LoadWithFilters(string dataFolder)
    {
        var path = ListPath(dataFolder);
        lock (Gate)
        {
            var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (_cache is { } cached && cached.Path == path && cached.Written == written)
            {
                return (cached.Entries, cached.Filters);
            }

            var entries = (JsonFile.Read<List<DialogueEntry>>(path) ?? new List<DialogueEntry>()).GroupBy(e => e.ItemId).ToDictionary(g => g.Key, g => g.Last());
            var filters = entries.ToDictionary(e => e.Key, e => e.Value.Tracks.Select(t => WordFilter.FromBase64(t.Filter)).ToArray());
            _cache = (path, written, entries, filters);
            return (entries, filters);
        }
    }

    public static void SaveList(string dataFolder, IEnumerable<DialogueEntry> entries)
    {
        lock (Gate)
        {
            JsonFile.Write(ListPath(dataFolder), entries.ToList());
            _cache = null;
        }
    }

    /// <summary>Writes a title's lines and returns its line in the list, with a word filter per track.</summary>
    public static DialogueEntry SaveItem(string dataFolder, Guid itemId, string signature, IReadOnlyList<(string Lang, List<SubtitleCue> Cues)> tracks)
    {
        var entry = new DialogueEntry { ItemId = itemId, Signature = signature };
        var path = ItemPath(dataFolder, itemId);
        var kept = tracks.Where(t => t.Cues.Count > 0).ToList();
        if (kept.Count == 0)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            Loaded.TryRemove(itemId, out _);
            return entry;
        }

        var item = new DialogueItem { ItemId = itemId };
        foreach (var (lang, cues) in kept)
        {
            var words = cues.SelectMany(c => DialogueText.Words(DialogueText.Normalize(c.Text))).Distinct(StringComparer.Ordinal).ToList();
            var filter = WordFilter.For(words.Count);
            words.ForEach(filter.Add);
            entry.Tracks.Add(new DialogueTrackInfo { Lang = lang, Filter = filter.ToBase64() });
            item.Tracks.Add(new DialogueTrack
            {
                Lang = lang,
                Start = cues.Select(c => c.StartMs).ToList(),
                Dur = cues.Select(c => c.EndMs - c.StartMs).ToList(),
                Text = cues.Select(c => c.Text).ToList(),
            });
        }

        JsonFile.Write(path, item);
        Loaded.TryRemove(itemId, out _);
        return entry;
    }

    public static void RemoveItem(string dataFolder, Guid itemId)
    {
        var path = ItemPath(dataFolder, itemId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Loaded.TryRemove(itemId, out _);
    }

    /// <summary>The track a person searches in: their subtitle language when the title has it, else the first.</summary>
    public static int TrackFor(IReadOnlyList<string> languages, string? preferred)
    {
        if (languages.Count == 0)
        {
            return -1;
        }

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            for (var i = 0; i < languages.Count; i++)
            {
                if (LanguageCodes.Same(languages[i], preferred))
                {
                    return i;
                }
            }
        }

        return 0;
    }

    /// <summary>One track of a title, with its words worked out, read from disk once and kept for a while.</summary>
    public static LoadedTrack? Track(string dataFolder, Guid itemId, int track)
    {
        var path = ItemPath(dataFolder, itemId);
        if (!File.Exists(path))
        {
            return null;
        }

        var written = File.GetLastWriteTimeUtc(path);
        if (!Loaded.TryGetValue(itemId, out var loaded) || loaded.Written != written)
        {
            if (JsonFile.Read<DialogueItem>(path) is not { } item)
            {
                return null;
            }

            if (Loaded.Count >= TitleCacheSize)
            {
                Loaded.Clear();
            }

            loaded = new LoadedItem(written, item.Tracks.Select(LoadedTrack.From).ToList());
            Loaded[itemId] = loaded;
        }

        return track >= 0 && track < loaded.Tracks.Count ? loaded.Tracks[track] : null;
    }

    private sealed record LoadedItem(DateTime Written, List<LoadedTrack> Tracks);
}

public sealed record LoadedTrack(List<SubtitleCue> Cues, List<string> Normalized, List<string[]> Words)
{
    public static LoadedTrack From(DialogueTrack track)
    {
        var count = new[] { track.Start.Count, track.Dur.Count, track.Text.Count }.Min();
        var cues = Enumerable.Range(0, count).Select(i => new SubtitleCue(track.Start[i], track.Start[i] + track.Dur[i], track.Text[i])).ToList();
        var normalized = cues.Select(c => DialogueText.Normalize(c.Text)).ToList();
        return new LoadedTrack(cues, normalized, normalized.Select(DialogueText.Words).ToList());
    }
}
