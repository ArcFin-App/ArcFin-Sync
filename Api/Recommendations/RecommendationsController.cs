using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ArcfinSync.Api.Recommendations;

/// <summary>
/// Rows picked for the signed-in user from their own history, likes and favourites, out of
/// titles they can see and haven't watched: Top Picks, Because You Watched, and Popular on
/// this server (only titles at least two other people played lately, so one person's viewing
/// is never shown to anyone). Worked out on the server, kept for half an hour.
/// </summary>
[ApiController]
[Route("ArcfinSync/Recommendations")]
[Authorize]
public class RecommendationsController : ControllerBase
{
    private const int PoolLimit = 2000;
    private const int RowSize = 20;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);
    private static readonly ConcurrentDictionary<Guid, (DateTime At, RecommendationsDto Rows)> Cache = new();

    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userData;
    private readonly LibraryTitles _titles;

    public RecommendationsController(IAuthorizationContext authContext, IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userData)
    {
        _titles = new LibraryTitles(userManager, libraryManager, userData);
        _authContext = authContext;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _userData = userData;
    }

    [HttpGet]
    public async Task<ActionResult<RecommendationsDto>> Get([FromQuery] bool refresh = false)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null)
        {
            return Unauthorized();
        }

        if (Plugin.Instance?.Configuration.RecommendationsEnabled != true)
        {
            return StatusCode(403, "Your server's admin has turned off recommendations.");
        }

        var now = DateTime.UtcNow;
        if (!refresh && Cache.TryGetValue(user.Id, out var cached) && now - cached.At < CacheFor)
        {
            return cached.Rows;
        }

        var rows = Build(user, now);
        Cache[user.Id] = (now, rows);
        return rows;
    }

    /// <summary>Drops a user's cached rows, e.g. after they rate something.</summary>
    public static void Forget(Guid userId) => Cache.TryRemove(userId, out _);

    private RecommendationsDto Build(User user, DateTime now)
    {
        var signals = _titles.Signals(user);
        var seen = signals.Where(s => s.Watched).Select(s => s.Title.Id).ToHashSet();
        var disliked = signals.Where(s => s.Likes == false).Select(s => s.Title.Id).ToHashSet();
        var candidates = _titles.Features(_titles.Unwatched(user, PoolLimit).Where(i => !seen.Contains(i.Id) && !disliked.Contains(i.Id)).ToList());
        var profile = Recommender.Profile(signals, now);

        var result = new RecommendationsDto();
        if (signals.Count > 0)
        {
            var picks = Recommender.Top(candidates.Select(c => (c, Recommender.Score(profile, c))), RowSize);
            if (picks.Count >= 4)
            {
                result.Rows.Add(new RecommendationRowDto { Id = "forYou", Kind = "forYou", ItemIds = picks.Select(p => p.Id.ToString("N")).ToList() });
            }
        }

        var used = result.Rows.SelectMany(r => r.ItemIds).ToHashSet();
        var seeds = signals.Where(s => s.Watched && s.Likes != false && s.LastPlayed is not null)
            .OrderByDescending(s => (s.Likes == true || s.Favorite ? 1 : 0, s.LastPlayed)).Select(s => s.Title)
            .DistinctBy(t => t.Id).Take(2).ToList();
        foreach (var seed in seeds)
        {
            var similar = Recommender.Top(candidates.Where(c => !used.Contains(c.Id.ToString("N"))).Select(c => (c, Recommender.Similarity(seed, c))), RowSize);
            if (similar.Count >= 4)
            {
                result.Rows.Add(new RecommendationRowDto
                {
                    Id = "because:" + seed.Id.ToString("N"),
                    Kind = "becauseYouWatched",
                    SeedId = seed.Id.ToString("N"),
                    SeedName = seed.Name,
                    ItemIds = similar.Select(p => p.Id.ToString("N")).ToList()
                });
                used.UnionWith(similar.Select(p => p.Id.ToString("N")));
            }
        }

        var popular = Popular(user, now, seen);
        if (popular.Count >= 4)
        {
            result.Rows.Add(new RecommendationRowDto { Id = "popular", Kind = "popularOnServer", ItemIds = popular });
        }

        return result;
    }

    /// <summary>Titles at least two other people on the server played in the last 30 days, that this user can see and hasn't watched.</summary>
    private List<string> Popular(User user, DateTime now, HashSet<Guid> seen)
    {
        return _titles.PlayedByOthers(user, now).Where(c => c.Value >= 2 && !seen.Contains(c.Key))
            .OrderByDescending(c => c.Value)
            .Select(c => _libraryManager.GetItemById(c.Key))
            .Where(i => i is not null && i.IsVisible(user) && _userData.GetUserData(user, i)?.Played != true)
            .Take(RowSize).Select(i => i!.Id.ToString("N")).ToList();
    }
}

public sealed class RecommendationsDto
{
    public List<RecommendationRowDto> Rows { get; set; } = new();
}

public sealed class RecommendationRowDto
{
    public string Id { get; set; } = string.Empty;

    /// <summary>forYou, becauseYouWatched or popularOnServer; the app writes the row's title in its own language.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? SeedId { get; set; }

    public string? SeedName { get; set; }

    public List<string> ItemIds { get; set; } = new();
}
