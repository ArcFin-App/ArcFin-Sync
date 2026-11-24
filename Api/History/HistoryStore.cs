using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.History;

/// <summary>
/// Past watch parties and Pick Together sessions, for administrators: when, who hosted, who took
/// part and how it ended. Only kept when an admin turns it on, for as long as they choose.
/// </summary>
public sealed class HistoryStore
{
    public const string WatchParty = "watchParty";
    public const string PickTogether = "pickTogether";
    private const int Limit = 5000;

    private static readonly object Gate = new();
    private readonly string _path;

    public HistoryStore(string folder)
    {
        _path = Path.Combine(folder, "history.json");
    }

    public static HistoryStore Current => new(Plugin.Instance!.DataFolderPath);

    /// <summary>Keeps an entry when history is on, dropping ones older than the admin's choice.</summary>
    public static void Record(HistoryEntry entry)
    {
        var settings = Plugin.Instance?.Configuration.History;
        if (settings is null || !settings.Enabled)
        {
            return;
        }

        Current.Add(entry, settings.KeepDays, DateTime.UtcNow);
    }

    public void Add(HistoryEntry entry, int? keepDays, DateTime now)
    {
        lock (Gate)
        {
            var list = Prune(Read(), keepDays, now);
            list.Add(entry);
            if (list.Count > Limit)
            {
                list.RemoveRange(0, list.Count - Limit);
            }

            JsonFile.Write(_path, list);
        }
    }

    /// <summary>Newest first; old ones past the admin's choice are dropped as it's read.</summary>
    public List<HistoryEntry> All(int? keepDays, DateTime now)
    {
        lock (Gate)
        {
            var all = Read();
            var kept = Prune(all, keepDays, now);
            if (kept.Count != all.Count)
            {
                JsonFile.Write(_path, kept);
            }

            return kept.OrderByDescending(e => e.Ended).ToList();
        }
    }

    public void Clear()
    {
        lock (Gate)
        {
            JsonFile.Write(_path, new List<HistoryEntry>());
        }
    }

    /// <summary>Null keeps everything; otherwise entries that ended more than that many days ago go.</summary>
    public static List<HistoryEntry> Prune(List<HistoryEntry> list, int? keepDays, DateTime now)
    {
        return keepDays is { } days ? list.Where(e => now - e.Ended <= TimeSpan.FromDays(days)).ToList() : list;
    }

    private List<HistoryEntry> Read() => JsonFile.Read<List<HistoryEntry>>(_path) ?? new List<HistoryEntry>();
}

public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>watchParty or pickTogether.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public DateTime Created { get; set; }

    public DateTime? ScheduledFor { get; set; }

    /// <summary>When they started watching (a party's start, or Pick Together's Watch Together).</summary>
    public DateTime? StartedWatching { get; set; }

    public DateTime Ended { get; set; }

    public string HostName { get; set; } = string.Empty;

    public List<string> People { get; set; } = new();

    /// <summary>The party's title, or what a Pick Together session chose to watch together.</summary>
    public Guid? ItemId { get; set; }

    /// <summary>Pick Together's matches.</summary>
    public List<Guid> Matches { get; set; } = new();

    /// <summary>watched, ended (by the host), cancelled (by an admin), notStarted (its time passed), or left (everyone left).</summary>
    public string Outcome { get; set; } = string.Empty;
}
