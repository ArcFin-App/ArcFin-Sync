using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Notifications;

/// <summary>
/// What happened on the server that Arcfin tells people about, in order. Each event gets the next
/// number, so a device asks for everything after the last number it saw. Kept for two weeks.
/// </summary>
public sealed class EventStore
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);
    private const int Limit = 2000;
    private static readonly object Gate = new();
    private readonly string _path;

    public EventStore(string folder)
    {
        _path = Path.Combine(folder, "events.json");
    }

    public static EventStore Current => new(Plugin.Instance!.DataFolderPath);

    /// <summary>The newest event's number, or 0 before the first.</summary>
    public long Cursor
    {
        get
        {
            lock (Gate)
            {
                return Read().Last;
            }
        }
    }

    public void Add(IEnumerable<ServerEvent> events, DateTime now)
    {
        var list = events.ToList();
        lock (Gate)
        {
            var file = Read();
            var added = false;
            foreach (var e in list)
            {
                e.Seq = ++file.Last;
                e.Created = now;
                file.Events.Add(e);
                added = true;
            }

            if (!added)
            {
                return;
            }

            file.Events = file.Events.Where(e => now - e.Created < KeepFor).TakeLast(Limit).ToList();
            JsonFile.Write(_path, file);
        }

        Added?.Invoke(list.Select(e => e.UserId).ToList());
    }

    /// <summary>After events are stored, with who they're for (null for everyone), so open Arcfins can be told.</summary>
    public static event Action<IReadOnlyList<Guid?>>? Added;

    /// <summary>For news that isn't stored, such as announcements, which come with every read.</summary>
    public static void Announce(IReadOnlyList<Guid?> users) => Added?.Invoke(users);

    /// <summary>This person's events after <paramref name="since"/>, oldest first.</summary>
    public List<ServerEvent> After(long since, Guid userId)
    {
        lock (Gate)
        {
            return Read().Events.Where(e => e.Seq > since && (e.UserId is null || e.UserId == userId)).ToList();
        }
    }

    private EventFile Read() => JsonFile.Read<EventFile>(_path) ?? new EventFile();

    private sealed class EventFile
    {
        public long Last { get; set; }

        public List<ServerEvent> Events { get; set; } = new();
    }
}

public sealed class ServerEvent
{
    public long Seq { get; set; }

    /// <summary>
    /// The same for the same news on every device, e.g. "seerr-approved.42", so a device that
    /// also found it by itself announces it once.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// seerr-approved, seerr-declined, seerr-available, seerr-pending, seerr-auto-approved,
    /// seerr-failed, issue-reported, issue-comment, issue-resolved, issue-reopened, announcement,
    /// maintenance or test.
    /// </summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Null for everyone.</summary>
    public Guid? UserId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? Body { get; set; }

    public int? RequestId { get; set; }

    public int? IssueId { get; set; }

    /// <summary>Seerr's issue type: 1 video, 2 audio, 3 subtitles, 4 other.</summary>
    public int? IssueType { get; set; }

    public int? ProblemSeason { get; set; }

    public int? ProblemEpisode { get; set; }

    /// <summary>Seerr's display name for whoever reported, commented, resolved or reopened.</summary>
    public string? Actor { get; set; }

    public DateTime? Starts { get; set; }

    public DateTime? Ends { get; set; }

    public DateTime Created { get; set; }
}
