using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ArcfinSync.Api.Recommendations;

/// <summary>What the recommenders read from Jellyfin: a user's signals, titles' features, and what other people played lately.</summary>
public sealed class LibraryTitles
{
    private static readonly string[] PersonTypes = { "Actor", "Director" };

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userData;

    public LibraryTitles(IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userData)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userData = userData;
    }

    /// <summary>Movies and shows the user watched (a show counts once any episode was played), liked or made a favourite.</summary>
    public List<Signal> Signals(User user)
    {
        var watchedMovies = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            Recursive = true,
            IsPlayed = true,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = 150
        });
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true,
            IsPlayed = true,
            OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
            Limit = 600
        });
        var showsPlayed = new Dictionary<Guid, DateTime?>();
        foreach (var episode in episodes.OfType<MediaBrowser.Controller.Entities.TV.Episode>())
        {
            var played = _userData.GetUserData(user, episode)?.LastPlayedDate;
            if (episode.SeriesId != Guid.Empty && (!showsPlayed.TryGetValue(episode.SeriesId, out var last) || played > last))
            {
                showsPlayed[episode.SeriesId] = played;
            }
        }

        var favourites = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            IsFavorite = true,
            Limit = 150
        });

        var titles = watchedMovies.Concat(favourites)
            .Concat(showsPlayed.Keys.Take(80).Select(id => _libraryManager.GetItemById(id)).Where(i => i is not null && i.IsVisible(user))!)
            .DistinctBy(i => i!.Id).ToList();
        var features = Features(titles!).ToDictionary(f => f.Id);
        var signals = new List<Signal>();
        foreach (var item in titles)
        {
            var data = _userData.GetUserData(user, item!);
            var isShow = item is MediaBrowser.Controller.Entities.TV.Series;
            var watched = isShow ? showsPlayed.ContainsKey(item!.Id) : data?.Played == true;
            var lastPlayed = isShow ? showsPlayed.GetValueOrDefault(item!.Id) : data?.LastPlayedDate;
            signals.Add(new Signal(features[item!.Id], watched, data?.Likes, data?.IsFavorite == true, lastPlayed));
        }

        return signals;
    }

    public List<BaseItem> Unwatched(User user, int limit)
    {
        var found = new List<BaseItem>();
        foreach (var kind in new[] { BaseItemKind.Movie, BaseItemKind.Series })
        {
            found.AddRange(_libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { kind },
                Recursive = true,
                IsVirtualItem = false,
                IsPlayed = false,
                OrderBy = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) },
                Limit = limit
            }));
        }

        return found;
    }

    public List<TitleFeatures> Features(IReadOnlyList<BaseItem> items)
    {
        var people = items.Count == 0
            ? new Dictionary<Guid, List<string>>()
            : _libraryManager.GetPeopleNamesByItems(items.Select(i => i.Id).ToList(), PersonTypes)
                .ToDictionary(p => p.Key, p => p.Value.Take(8).ToList());
        return items.Select(i => new TitleFeatures(
            i.Id,
            i.Name ?? string.Empty,
            i is MediaBrowser.Controller.Entities.TV.Series,
            i.Genres ?? Array.Empty<string>(),
            people.GetValueOrDefault(i.Id) ?? new List<string>(),
            i.Tags ?? Array.Empty<string>(),
            i.Studios ?? Array.Empty<string>(),
            i.CommunityRating,
            i.ProductionYear,
            i.DateCreated)).ToList();
    }

    /// <summary>
    /// For each film or show, how many people other than <paramref name="user"/> played it in the
    /// last 30 days. Callers only use counts of two or more, so no one's viewing shows on its own.
    /// </summary>
    public Dictionary<Guid, int> PlayedByOthers(User user, DateTime now)
    {
        var counts = new Dictionary<Guid, int>();
        foreach (var other in _userManager.GetUsers().Where(u => u.Id != user.Id))
        {
            var recent = _libraryManager.GetItemList(new InternalItemsQuery(other)
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                Recursive = true,
                IsPlayed = true,
                OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
                Limit = 200
            });
            var titles = new HashSet<Guid>();
            foreach (var item in recent)
            {
                if (_userData.GetUserData(other, item)?.LastPlayedDate is not { } played || now - played > TimeSpan.FromDays(30))
                {
                    continue;
                }

                titles.Add(item is MediaBrowser.Controller.Entities.TV.Episode episode ? episode.SeriesId : item.Id);
            }

            foreach (var id in titles.Where(id => id != Guid.Empty))
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        return counts;
    }
}
