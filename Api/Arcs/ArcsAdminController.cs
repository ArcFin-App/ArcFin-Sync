using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>Arcs settings and totals for server administrators. The totals never show what one person did.</summary>
[ApiController]
[Route("ArcfinSync/Admin/Arcs")]
[Authorize(Policy = Policies.RequiresElevation)]
public class ArcsAdminController : ControllerBase
{
    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;
    private readonly ITaskManager _taskManager;
    private readonly ArcsService _arcs;

    public ArcsAdminController(IAuthorizationContext authContext, IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userData,
                               IChapterManager chapters, IMediaSegmentManager segments, IMediaEncoder mediaEncoder, IConfigurationManager config, ITaskManager taskManager,
                               ILogger<ArcsAdminController> logger)
    {
        _authContext = authContext;
        _userManager = userManager;
        _taskManager = taskManager;
        _arcs = new ArcsService(libraryManager, userManager, userData, chapters, segments, mediaEncoder, config, logger);
    }

    [HttpGet]
    public ActionResult<ArcsSettings> Get()
    {
        return ArcsService.Settings;
    }

    [HttpPut]
    public ActionResult<ArcsSettings> Put([FromBody] ArcsSettings settings)
    {
        if (ArcsSettingsRules.Clean(settings, id => _userManager.GetUserById(id) is not null, out var error) is not { } clean)
        {
            return BadRequest(error);
        }

        var plugin = Plugin.Instance!;
        plugin.Configuration.Arcs = clean;
        plugin.SaveConfiguration();
        ArcsService.InvalidateAll();
        ClipRenderer.Prune(ClipRenderer.CacheFolder(plugin.DataFolderPath), clean.CacheMb * 1024L * 1024L);
        return clean;
    }

    [HttpGet("Stats")]
    public async Task<ActionResult<ArcsStatsDto>> GetStats()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var admin = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (admin is null)
        {
            return Unauthorized();
        }

        var dataFolder = Plugin.Instance!.DataFolderPath;
        var aggregate = ArcsStore.Current.Aggregate();
        var stats = new ArcsStatsDto
        {
            Clips = aggregate.DistinctClips,
            IndexedTitles = SceneIndex.Load(dataFolder).Count,
            CacheMb = Math.Round(ClipRenderer.CacheSize(ClipRenderer.CacheFolder(dataFolder)) / 1048576.0, 1),
            Likes = aggregate.Likes,
            Shares = aggregate.Shares,
            WatchFromHere = aggregate.WatchFromHere,
            DialogueTitles = DialogueIndex.Load(dataFolder).Values.Count(e => e.Tracks.Count > 0),
        };
        stats.TopLiked = Top(aggregate.ClipLikers, admin, aggregate);
        stats.TopWatchFromHere = Top(aggregate.ClipWatchFromHere, admin, aggregate);
        return stats;
    }

    /// <summary>Rebuilds the scene index now.</summary>
    [HttpPost("Index")]
    public ActionResult RebuildIndex()
    {
        _taskManager.QueueIfNotRunning<ArcsSceneIndexTask>();
        return Accepted();
    }

    /// <summary>Reads new and changed subtitles into the dialogue index now.</summary>
    [HttpPost("DialogueIndex")]
    public ActionResult RebuildDialogueIndex()
    {
        _taskManager.QueueIfNotRunning<ArcsDialogueIndexTask>();
        return Accepted();
    }

    [HttpDelete("Cache")]
    public ActionResult ClearCache()
    {
        ClipRenderer.Clear(ClipRenderer.CacheFolder(Plugin.Instance!.DataFolderPath));
        return NoContent();
    }

    /// <summary>The ten clips the most people responded to, counting only clips at least two people did.</summary>
    private List<ArcClipDto> Top(Dictionary<string, HashSet<Guid>> byClip, Jellyfin.Database.Implementations.Entities.User admin, ArcsAggregate aggregate)
    {
        var result = new List<ArcClipDto>();
        foreach (var (clipId, _) in byClip.Where(c => c.Value.Count >= 2).OrderByDescending(c => c.Value.Count).ThenBy(c => c.Key, StringComparer.Ordinal))
        {
            if (_arcs.StatsClip(admin, clipId, aggregate) is { } dto)
            {
                result.Add(dto);
            }

            if (result.Count == 10)
            {
                break;
            }
        }

        return result;
    }
}

/// <summary>Checks an administrator's Arcs settings and fills in what's missing.</summary>
public static class ArcsSettingsRules
{
    public static readonly string[] SpoilerChoices = { "UserChoice", ArcsService.AlwaysSafe };
    public static readonly string[] SharingChoices = { "Allowed", "Off" };
    public static readonly string[] WatermarkChoices = { "Optional", "Forced", "Off" };
    public static readonly string[] SendingChoices = { "Everyone", "Off" };
    public const int MaxSceneScans = 8;

    public static int ClampSceneScans(int value) => Math.Clamp(value, 1, MaxSceneScans);

    public static ArcsSettings? Clean(ArcsSettings? input, Func<Guid, bool> userExists, out string? error)
    {
        error = null;
        if (input is null)
        {
            error = "No settings.";
            return null;
        }

        if (input.MinSeconds < 5 || input.MaxSeconds > 180 || input.MinSeconds > input.MaxSeconds)
        {
            error = "Clip lengths must be between 5 and 180 seconds, with the shortest no longer than the longest.";
            return null;
        }

        if (input.DefaultSeconds < input.MinSeconds || input.DefaultSeconds > input.MaxSeconds)
        {
            error = "The usual clip length must be between the shortest and longest.";
            return null;
        }

        var sending = string.IsNullOrEmpty(input.Sending) ? "Everyone" : input.Sending;
        if (!SpoilerChoices.Contains(input.Spoilers) || !SharingChoices.Contains(input.Sharing) || !WatermarkChoices.Contains(input.Watermark)
            || !SendingChoices.Contains(sending))
        {
            error = "Unknown choice for spoilers, sharing, sending or the watermark.";
            return null;
        }

        var weights = input.Weights ?? new ArcsWeights();
        double Weight(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 5) : 1;
        return new ArcsSettings
        {
            Enabled = input.Enabled,
            DisabledUsers = (input.DisabledUsers ?? new List<Guid>()).Distinct().Where(userExists).ToList(),
            Libraries = (input.Libraries ?? new List<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList(),
            MinSeconds = input.MinSeconds,
            MaxSeconds = input.MaxSeconds,
            DefaultSeconds = input.DefaultSeconds,
            Spoilers = input.Spoilers,
            Sharing = input.Sharing,
            Watermark = input.Watermark,
            ClipOfTheDay = input.ClipOfTheDay,
            BannedItems = (input.BannedItems ?? new List<Guid>()).Where(id => id != Guid.Empty).Distinct().ToList(),
            CacheMb = Math.Clamp(input.CacheMb, 100, 1024 * 1024),
            Prewarm = Math.Clamp(input.Prewarm, 0, 10),
            UseSceneDetection = input.UseSceneDetection,
            SceneScans = ClampSceneScans(input.SceneScans),
            RequestsInFeed = input.RequestsInFeed,
            YouTubeTrailers = input.YouTubeTrailers,
            Sending = sending,
            DialogueSearch = input.DialogueSearch,
            Weights = new ArcsWeights
            {
                History = Weight(weights.History),
                Likes = Weight(weights.Likes),
                WatchTime = Weight(weights.WatchTime),
                WatchFromHere = Weight(weights.WatchFromHere),
                Shares = Weight(weights.Shares),
                Favorites = Weight(weights.Favorites),
                Similar = Weight(weights.Similar),
                Popular = Weight(weights.Popular),
                Exploration = double.IsFinite(weights.Exploration) ? Math.Clamp(weights.Exploration, 0, 0.5) : 0.15,
                Fresh = Weight(weights.Fresh),
            },
        };
    }
}
