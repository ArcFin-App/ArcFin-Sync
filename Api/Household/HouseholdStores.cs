using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;
using Jellyfin.Plugin.ArcfinSync.Configuration;

namespace Jellyfin.Plugin.ArcfinSync.Api.Household;

/// <summary>
/// Which titles each Arcfin device keeps downloaded, as it last said, so caps hold across all
/// of a person's devices. A title on two devices counts once, at the size of its largest copy.
/// </summary>
public sealed class DownloadInventoryStore
{
    private static readonly object Gate = new();
    private readonly string _path;

    public DownloadInventoryStore(string folder)
    {
        _path = Path.Combine(folder, "downloads.json");
    }

    public static DownloadInventoryStore Current => new(Plugin.Instance!.DataFolderPath);

    public void Report(Guid userId, string deviceId, IEnumerable<DownloadedTitle> titles, DateTime now, IEnumerable<DownloadedTitle>? songs = null)
    {
        lock (Gate)
        {
            var all = Read();
            var key = $"{userId:N}:{deviceId}";
            all.TryGetValue(key, out var before);
            all[key] = new DeviceDownloads
            {
                UserId = userId,
                DeviceId = deviceId,
                Titles = Clean(titles),
                Songs = songs is null ? before?.Songs ?? new List<DownloadedTitle>() : Clean(songs),
                Updated = now
            };
            JsonFile.Write(_path, all);
        }
    }

    private static List<DownloadedTitle> Clean(IEnumerable<DownloadedTitle> titles)
    {
        return titles.Where(t => !string.IsNullOrWhiteSpace(t.ItemId)).GroupBy(t => Key(t.ItemId))
            .Select(g => new DownloadedTitle
            {
                ItemId = g.Key,
                AlbumId = g.Select(t => t.AlbumId).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)) is { } album ? Key(album) : null,
                Bytes = Math.Max(0, g.Max(t => t.Bytes))
            }).Take(50000).ToList();
    }

    /// <summary>Distinct songs and albums across the person's devices, and their space counted once each.</summary>
    public MusicTotals Music(Guid userId)
    {
        lock (Gate)
        {
            var songs = new Dictionary<string, (string? Album, long Bytes)>(StringComparer.Ordinal);
            foreach (var song in Read().Values.Where(d => d.UserId == userId).SelectMany(d => d.Songs))
            {
                var bytes = songs.TryGetValue(song.ItemId, out var had) ? Math.Max(had.Bytes, song.Bytes) : song.Bytes;
                songs[song.ItemId] = (song.AlbumId ?? had.Album, bytes);
            }

            return new MusicTotals(songs.Count, songs.Values.Select(s => s.Album).Where(a => a is not null).Distinct().Count(), songs.Values.Sum(s => s.Bytes),
                                   songs.Keys.ToHashSet(), songs.Values.Select(s => s.Album).Where(a => a is not null).Select(a => a!).ToHashSet());
        }
    }

    /// <summary>Distinct titles across the person's devices, and their space counted once each.</summary>
    public (int Titles, long Bytes) Totals(Guid userId) => Totals(Distinct(userId));

    /// <summary>Whether this title is already downloaded on one of the person's devices.</summary>
    public long? Existing(Guid userId, string itemId) => Distinct(userId).TryGetValue(Key(itemId), out var bytes) ? bytes : null;

    private Dictionary<string, long> Distinct(Guid userId)
    {
        lock (Gate)
        {
            var titles = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var device in Read().Values.Where(d => d.UserId == userId))
            {
                foreach (var title in device.Titles)
                {
                    titles[title.ItemId] = Math.Max(titles.GetValueOrDefault(title.ItemId), title.Bytes);
                }
            }

            return titles;
        }
    }

    private static (int, long) Totals(Dictionary<string, long> titles) => (titles.Count, titles.Values.Sum());

    private static string Key(string itemId) => itemId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private Dictionary<string, DeviceDownloads> Read() => JsonFile.Read<Dictionary<string, DeviceDownloads>>(_path) ?? new Dictionary<string, DeviceDownloads>();

    private sealed class DeviceDownloads
    {
        public Guid UserId { get; set; }

        public string DeviceId { get; set; } = string.Empty;

        public List<DownloadedTitle> Titles { get; set; } = new();

        public List<DownloadedTitle> Songs { get; set; } = new();

        public DateTime Updated { get; set; }
    }
}

public sealed class DownloadedTitle
{
    public string ItemId { get; set; } = string.Empty;

    /// <summary>For a song, its album, so albums can be counted.</summary>
    public string? AlbumId { get; set; }

    public long Bytes { get; set; }
}

public sealed record MusicTotals(int Songs, int Albums, long Bytes, HashSet<string> SongIds, HashSet<string> AlbumIds);

public sealed record MusicCaps(int? MaxAlbums, int? MaxSongs, long? MaxBytes);

public sealed record DownloadCaps(int? MaxTitles, long? MaxBytes);

public static class DownloadLimits
{
    /// <summary>The person's caps, with their override applied, or null when nothing caps them.</summary>
    public static DownloadCaps? For(DownloadLimitSettings settings, Guid userId)
    {
        if (!settings.Enabled)
        {
            return null;
        }

        var own = settings.Users.FirstOrDefault(u => u.UserId == userId);
        if (own?.Mode == "off")
        {
            return null;
        }

        var titles = own?.Mode == "custom" ? own.MaxTitles : settings.MaxTitles;
        var gigabytes = own?.Mode == "custom" ? own.MaxGigabytes : settings.MaxGigabytes;
        return titles is null && gigabytes is null ? null : new DownloadCaps(titles, gigabytes is { } gb ? (long)(gb * 1_000_000_000) : null);
    }

    /// <summary>The person's music caps, with their override applied, or null when nothing caps them.</summary>
    public static MusicCaps? MusicFor(DownloadLimitSettings settings, Guid userId)
    {
        if (!settings.MusicEnabled)
        {
            return null;
        }

        var own = settings.Users.FirstOrDefault(u => u.UserId == userId);
        if (own?.MusicMode == "off")
        {
            return null;
        }

        var custom = own?.MusicMode == "custom";
        var albums = custom ? own!.MusicMaxAlbums : settings.MusicMaxAlbums;
        var songs = custom ? own!.MusicMaxSongs : settings.MusicMaxSongs;
        var gigabytes = custom ? own!.MusicMaxGigabytes : settings.MusicMaxGigabytes;
        return albums is null && songs is null && gigabytes is null ? null : new MusicCaps(albums, songs, gigabytes is { } gb ? (long)(gb * 1_000_000_000) : null);
    }

    /// <summary>
    /// Whether these songs fit: songs and albums already on another of the person's devices
    /// don't count again, and whichever cap is reached first stops it.
    /// </summary>
    public static bool MusicFits(MusicCaps? caps, MusicTotals used, IEnumerable<DownloadedTitle> adding)
    {
        if (caps is null)
        {
            return true;
        }

        var fresh = adding.Where(s => !used.SongIds.Contains(s.ItemId.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant())).ToList();
        var newAlbums = fresh.Select(s => s.AlbumId?.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant())
            .Where(a => a is not null && !used.AlbumIds.Contains(a)).Distinct().Count();
        var bytes = fresh.Sum(s => Math.Max(0, s.Bytes));
        return (caps.MaxSongs is not { } s || used.Songs + fresh.Count <= s)
               && (caps.MaxAlbums is not { } a || used.Albums + newAlbums <= a)
               && (caps.MaxBytes is not { } b || used.Bytes + bytes <= b);
    }

    /// <summary>
    /// Whether one more title of <paramref name="bytes"/> fits: whichever cap is reached first
    /// stops it. A title already on another of the person's devices (<paramref name="existing"/>
    /// is its size there) adds no title, and only any extra size.
    /// </summary>
    public static bool Fits(DownloadCaps? caps, int usedTitles, long usedBytes, long bytes, long? existing = null)
    {
        if (caps is null)
        {
            return true;
        }

        var addTitles = existing is null ? 1 : 0;
        var addBytes = Math.Max(0, Math.Max(0, bytes) - (existing ?? 0));
        return (caps.MaxTitles is not { } t || usedTitles + addTitles <= t) && (caps.MaxBytes is not { } b || usedBytes + addBytes <= b);
    }
}

/// <summary>Who's watching on a shared account's device right now, as Arcfin there was told.</summary>
public sealed class SharedWatchStore
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (List<Guid> Members, DateTime At)> Watching = new();

    public static void Set(Guid sharedUser, string deviceId, List<Guid> members, DateTime now)
    {
        lock (Gate)
        {
            Watching[$"{sharedUser:N}:{deviceId}"] = (members, now);
        }
    }

    /// <summary>The members picked on this device, for twelve hours after they were picked.</summary>
    public static List<Guid> Get(Guid sharedUser, string deviceId, DateTime now)
    {
        lock (Gate)
        {
            return Watching.TryGetValue($"{sharedUser:N}:{deviceId}", out var entry) && now - entry.At < TimeSpan.FromHours(12)
                ? entry.Members
                : new List<Guid>();
        }
    }
}

public static class Notices
{
    /// <summary>Saved times come back from the XML file without a kind; they were saved in UTC.</summary>
    public static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    public static DateTime? Utc(DateTime? value) => value is { } v ? Utc(v) : null;

    public static Announcement InUtc(Announcement a) => new()
    {
        Id = a.Id, Title = a.Title, Message = a.Message, Style = a.Style, Starts = Utc(a.Starts), Ends = Utc(a.Ends), Users = a.Users
    };

    public static MaintenanceWindow InUtc(MaintenanceWindow m) => new() { Id = m.Id, Starts = Utc(m.Starts), Ends = Utc(m.Ends), Message = m.Message };

    /// <summary>Announcements showing now for this person: started, not ended, and for everyone or them.</summary>
    public static List<Announcement> ActiveFor(IEnumerable<Announcement> all, Guid userId, DateTime now)
    {
        return all.Select(InUtc).Where(a => (a.Starts is null || a.Starts <= now) && (a.Ends is null || a.Ends > now)
                              && (a.Users.Count == 0 || a.Users.Contains(userId)))
            .OrderByDescending(a => a.Style == "warning").ThenByDescending(a => a.Starts).ToList();
    }

    /// <summary>Maintenance that hasn't ended, soonest first.</summary>
    public static List<MaintenanceWindow> Upcoming(IEnumerable<MaintenanceWindow> all, DateTime now)
    {
        return all.Select(InUtc).Where(m => m.Ends > now).OrderBy(m => m.Starts).ToList();
    }
}
