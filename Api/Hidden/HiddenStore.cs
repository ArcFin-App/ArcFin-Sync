using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;

namespace Jellyfin.Plugin.ArcfinSync.Api.Hidden;

/// <summary>
/// What each user has hidden from Continue Watching and Next Up, the same on all their devices.
/// Hiding keeps the saved position and watched state; Jellyfin itself has no way to hide a title
/// from either row. Continue Watching holds movies and episodes; Next Up holds shows.
/// </summary>
public sealed class HiddenStore
{
    public const string ContinueWatching = "continueWatching";
    public const string NextUp = "nextUp";
    private const int Limit = 2000;

    private static readonly object Gate = new();
    private readonly string _root;

    public HiddenStore(string folder)
    {
        _root = Path.Combine(folder, "hidden");
    }

    public static HiddenStore Current => new(Plugin.Instance!.DataFolderPath);

    public static bool IsRow(string row) => row is ContinueWatching or NextUp;

    public HiddenLists Load(Guid userId)
    {
        lock (Gate)
        {
            return JsonFile.Read<HiddenLists>(PathFor(userId)) ?? new HiddenLists();
        }
    }

    public HiddenLists Hide(Guid userId, string row, Guid itemId, DateTime now)
    {
        return Change(userId, lists =>
        {
            var list = row == NextUp ? lists.NextUp : lists.ContinueWatching;
            list.RemoveAll(e => e.ItemId == itemId);
            list.Add(new HiddenEntry { ItemId = itemId, Hidden = now });
            if (list.Count > Limit)
            {
                list.RemoveRange(0, list.Count - Limit);
            }
        });
    }

    public HiddenLists Unhide(Guid userId, string row, Guid itemId)
    {
        return Change(userId, lists => (row == NextUp ? lists.NextUp : lists.ContinueWatching).RemoveAll(e => e.ItemId == itemId));
    }

    /// <summary>
    /// A title hidden from Continue Watching comes back once it's been played again after it was
    /// hidden: hiding is for "not now", not "never".
    /// </summary>
    public HiddenLists DropReplayed(Guid userId, Func<Guid, DateTime?> lastPlayed)
    {
        return Change(userId, lists =>
        {
            lists.ContinueWatching.RemoveAll(e => lastPlayed(e.ItemId) is { } played && played > e.Hidden);
            lists.NextUp.RemoveAll(e => lastPlayed(e.ItemId) is { } played && played > e.Hidden);
        });
    }

    private HiddenLists Change(Guid userId, Action<HiddenLists> change)
    {
        lock (Gate)
        {
            var path = PathFor(userId);
            var lists = JsonFile.Read<HiddenLists>(path) ?? new HiddenLists();
            change(lists);
            JsonFile.Write(path, lists);
            return lists;
        }
    }

    private string PathFor(Guid userId) => Path.Combine(_root, $"{userId:N}.json");
}

public sealed class HiddenLists
{
    public List<HiddenEntry> ContinueWatching { get; set; } = new();

    public List<HiddenEntry> NextUp { get; set; } = new();
}

public sealed class HiddenEntry
{
    public Guid ItemId { get; set; }

    public DateTime Hidden { get; set; }
}
