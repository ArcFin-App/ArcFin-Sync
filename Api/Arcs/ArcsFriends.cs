using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

public enum FriendAddResult
{
    Added,
    WrongCode,
    TooManyTries,
    Full,
}

/// <summary>
/// Who may send whom scenes: people become friends by entering each other's friend code, and
/// it works both ways. Kept in its own file, so resetting someone's Arcs history keeps their
/// friends and code. Wrong codes are limited to ten an hour per person so codes can't be guessed.
/// </summary>
public sealed class ArcsFriends
{
    public const int MaxFriends = 100;
    public const int TriesPerHour = 10;
    public const int CodeLength = 8;

    /// <summary>No 0/O, 1/I or L, so a code read out or copied by hand comes out right.</summary>
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    private static readonly object Gate = new();
    private readonly string _path;

    public ArcsFriends(string folder)
    {
        _path = Path.Combine(folder, "arcs", "friends.json");
    }

    public static ArcsFriends Current => new(Plugin.Instance!.DataFolderPath);

    /// <summary>A code as typed, without case, spaces or dashes; null when it can't be a code.</summary>
    public static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var text = new StringBuilder(CodeLength);
        foreach (var c in code)
        {
            if (c is '-' or ' ' or '‐' or '‑' or '‒' or '–')
            {
                continue;
            }

            var upper = char.ToUpperInvariant(c);
            if (!Alphabet.Contains(upper, StringComparison.Ordinal) || text.Length == CodeLength)
            {
                return null;
            }

            text.Append(upper);
        }

        return text.Length == CodeLength ? text.ToString() : null;
    }

    /// <summary>"K7Q2MX9P" as "K7Q2-MX9P".</summary>
    public static string Format(string code) => code.Length == CodeLength ? $"{code[..4]}-{code[4..]}" : code;

    /// <summary>The person's code, made the first time it's asked for.</summary>
    public string CodeFor(Guid userId)
    {
        lock (Gate)
        {
            var file = Read();
            if (!file.Codes.TryGetValue(userId, out var code))
            {
                code = Fresh(file);
                file.Codes[userId] = code;
                Write(file);
            }

            return Format(code);
        }
    }

    /// <summary>A new code; the old one stops working, friends stay.</summary>
    public string NewCode(Guid userId)
    {
        lock (Gate)
        {
            var file = Read();
            var code = Fresh(file);
            file.Codes[userId] = code;
            Write(file);
            return Format(code);
        }
    }

    public List<Guid> FriendsOf(Guid userId)
    {
        lock (Gate)
        {
            return Read().Links.GetValueOrDefault(userId)?.ToList() ?? new List<Guid>();
        }
    }

    public bool AreFriends(Guid a, Guid b) => FriendsOf(a).Contains(b);

    /// <summary>
    /// Adds the person whose code this is, both ways. Unknown codes, the caller's own and people who
    /// can't get scenes all come back as a wrong code, and count against the hourly tries.
    /// </summary>
    public (FriendAddResult Result, Guid Friend) Add(Guid userId, string? code, Func<Guid, bool> canReceive, DateTime now)
    {
        lock (Gate)
        {
            var file = Read();
            var tries = file.Failures.GetValueOrDefault(userId) ?? new List<DateTime>();
            tries.RemoveAll(t => now - t >= TimeSpan.FromHours(1));
            if (tries.Count >= TriesPerHour)
            {
                return (FriendAddResult.TooManyTries, Guid.Empty);
            }

            var normal = Normalize(code);
            var friend = normal is null ? Guid.Empty : file.Codes.FirstOrDefault(c => c.Value == normal).Key;
            if (friend == Guid.Empty || friend == userId || !canReceive(friend))
            {
                tries.Add(now);
                file.Failures[userId] = tries;
                Write(file);
                return (FriendAddResult.WrongCode, Guid.Empty);
            }

            var mine = Links(file, userId);
            var theirs = Links(file, friend);
            if (mine.Contains(friend))
            {
                return (FriendAddResult.Added, friend);
            }

            if (mine.Count >= MaxFriends || theirs.Count >= MaxFriends)
            {
                return (FriendAddResult.Full, Guid.Empty);
            }

            mine.Add(friend);
            theirs.Add(userId);
            Write(file);
            return (FriendAddResult.Added, friend);
        }
    }

    public void Remove(Guid userId, Guid friend)
    {
        lock (Gate)
        {
            var file = Read();
            var changed = Links(file, userId).Remove(friend) | Links(file, friend).Remove(userId);
            if (changed)
            {
                Write(file);
            }
        }
    }

    private static List<Guid> Links(FriendsFile file, Guid userId)
    {
        if (!file.Links.TryGetValue(userId, out var list))
        {
            file.Links[userId] = list = new List<Guid>();
        }

        return list;
    }

    private static string Fresh(FriendsFile file)
    {
        while (true)
        {
            var chars = new char[CodeLength];
            for (var i = 0; i < CodeLength; i++)
            {
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }

            var code = new string(chars);
            if (!file.Codes.ContainsValue(code))
            {
                return code;
            }
        }
    }

    private FriendsFile Read() => JsonFile.Read<FriendsFile>(_path) ?? new FriendsFile();

    private void Write(FriendsFile file)
    {
        foreach (var empty in file.Links.Where(l => l.Value.Count == 0).Select(l => l.Key).ToList())
        {
            file.Links.Remove(empty);
        }

        JsonFile.Write(_path, file);
    }

    private sealed class FriendsFile
    {
        public Dictionary<Guid, string> Codes { get; set; } = new();

        public Dictionary<Guid, List<Guid>> Links { get; set; } = new();

        public Dictionary<Guid, List<DateTime>> Failures { get; set; } = new();
    }
}
