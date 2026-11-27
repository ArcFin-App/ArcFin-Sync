using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.ArcfinSync.Api.Notifications;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>A scene one person sent another.</summary>
public sealed class ArcSent
{
    public string ClipId { get; set; } = string.Empty;

    public Guid FromUserId { get; set; }

    public string? Note { get; set; }

    public DateTime At { get; set; }

    public DateTime? SeenAt { get; set; }
}

public enum ArcSendResult
{
    Sent,
    DailyLimit,
}

/// <summary>
/// Scenes people sent each other: one inbox file per recipient and a log of who sent when, kept
/// apart from each person's Arcs history so resetting it neither empties their inbox nor the daily
/// limit. An inbox keeps at most 50 unseen scenes (the oldest go first); unseen ones go after 30
/// days, as do seen ones.
/// </summary>
public sealed class ArcsInbox
{
    public const int DailyLimit = 30;
    public const int UnseenCap = 50;
    public const int NoteLength = 140;
    private static readonly TimeSpan Keep = TimeSpan.FromDays(30);
    private static readonly object Gate = new();
    private readonly string _root;

    public ArcsInbox(string folder)
    {
        _root = Path.Combine(folder, "arcs");
    }

    public static ArcsInbox Current => new(Plugin.Instance!.DataFolderPath);

    /// <summary>A note as kept: plain text on one line, at most 140 characters, or null when empty.</summary>
    public static string? CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return null;
        }

        var text = new StringBuilder(note.Length);
        foreach (var c in note.Normalize(NormalizationForm.FormC))
        {
            text.Append(char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c);
        }

        var clean = string.Join(' ', text.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length > NoteLength)
        {
            clean = clean[..NoteLength];
            if (char.IsHighSurrogate(clean[^1]))
            {
                clean = clean[..^1];
            }
        }

        return clean.Length == 0 ? null : clean;
    }

    public ArcSendResult Send(Guid from, Guid to, string clipId, string? note, DateTime now)
    {
        lock (Gate)
        {
            var log = JsonFile.Read<Dictionary<Guid, List<DateTime>>>(LogPath) ?? new Dictionary<Guid, List<DateTime>>();
            foreach (var key in log.Keys.ToList())
            {
                log[key].RemoveAll(t => now - t >= TimeSpan.FromDays(1));
                if (log[key].Count == 0)
                {
                    log.Remove(key);
                }
            }

            var sent = log.GetValueOrDefault(from) ?? new List<DateTime>();
            if (sent.Count >= DailyLimit)
            {
                return ArcSendResult.DailyLimit;
            }

            sent.Add(now);
            log[from] = sent;
            JsonFile.Write(LogPath, log);

            var inbox = Read(to, now);
            inbox.RemoveAll(s => s.SeenAt is null && s.FromUserId == from && s.ClipId == clipId);
            inbox.Add(new ArcSent { ClipId = clipId, FromUserId = from, Note = CleanNote(note), At = now });
            var unseen = inbox.Where(s => s.SeenAt is null).OrderBy(s => s.At).ToList();
            foreach (var old in unseen.Take(Math.Max(0, unseen.Count - UnseenCap)))
            {
                inbox.Remove(old);
            }

            JsonFile.Write(InboxPath(to), inbox);
            return ArcSendResult.Sent;
        }
    }

    /// <summary>Scenes sent to this person they haven't watched yet, oldest first.</summary>
    public List<ArcSent> Unseen(Guid userId, DateTime now)
    {
        lock (Gate)
        {
            return Read(userId, now).Where(s => s.SeenAt is null).OrderBy(s => s.At).ToList();
        }
    }

    /// <summary>Marks the sent scenes among <paramref name="clipIds"/> seen. True when any were.</summary>
    public bool MarkSeen(Guid userId, IEnumerable<string> clipIds, DateTime now)
    {
        var ids = clipIds.ToHashSet(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return false;
        }

        lock (Gate)
        {
            var path = InboxPath(userId);
            if (!File.Exists(path))
            {
                return false;
            }

            var inbox = Read(userId, now);
            var changed = false;
            foreach (var s in inbox.Where(s => s.SeenAt is null && ids.Contains(s.ClipId)))
            {
                s.SeenAt = now;
                changed = true;
            }

            if (changed)
            {
                JsonFile.Write(path, inbox);
            }

            return changed;
        }
    }

    private List<ArcSent> Read(Guid userId, DateTime now)
    {
        var inbox = JsonFile.Read<List<ArcSent>>(InboxPath(userId)) ?? new List<ArcSent>();
        inbox.RemoveAll(s => now - (s.SeenAt ?? s.At) > Keep);
        return inbox;
    }

    private string InboxPath(Guid userId) => Path.Combine(_root, "inbox", $"{userId:N}.json");

    private string LogPath => Path.Combine(_root, "sends.json");
}

/// <summary>The notification someone gets when a scene is sent to them.</summary>
public static class ArcsSentNotice
{
    public const string Kind = "arcs-sent";

    /// <summary>The film, or the show with its season and episode: "Friends · S1 · E5".</summary>
    public static string TitleLine(string? name, int? season, int? episode)
    {
        var line = name ?? string.Empty;
        if (season is { } s)
        {
            line += string.Create(System.Globalization.CultureInfo.InvariantCulture, $" · S{s}");
        }

        if (episode is { } e)
        {
            line += string.Create(System.Globalization.CultureInfo.InvariantCulture, $" · E{e}");
        }

        return line;
    }

    public static ServerEvent Event(Guid recipient, string senderName, string titleLine, string? note, string clipId) => new()
    {
        Id = $"{Kind}.{Guid.NewGuid():N}",
        Kind = Kind,
        UserId = recipient,
        Title = senderName,
        Body = ArcsInbox.CleanNote(note) is { } clean ? titleLine + "\n" + clean : titleLine,
        ClipId = clipId,
    };
}
