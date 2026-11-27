using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Arcs;

/// <summary>
/// Arcs: a vertical feed of short scenes from the person's own library, picked for them. Every
/// call needs a sign-in; clips only ever come from what that person can see. Off until an
/// administrator turns it on, for everyone or all but chosen users.
/// </summary>
[ApiController]
[Route("ArcfinSync/Arcs")]
[Authorize]
public class ArcsController : ControllerBase
{
    private const int MaxEventsPerCall = 200;
    private static readonly TimeSpan FirstSegmentWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RequestWait = TimeSpan.FromSeconds(2);
    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IConfigurationManager _config;
    private readonly ILocalizationManager _localization;
    private readonly ArcsService _arcs;
    private readonly ArcsRequests _requests;

    public ArcsController(IAuthorizationContext authContext, IUserManager userManager, ILibraryManager libraryManager, IUserDataManager userData,
                          IChapterManager chapters, IMediaSegmentManager segments, IMediaEncoder mediaEncoder, IConfigurationManager config,
                          ILocalizationManager localization, IHttpClientFactory http, ILogger<ArcsController> logger)
    {
        _authContext = authContext;
        _userManager = userManager;
        _mediaEncoder = mediaEncoder;
        _config = config;
        _localization = localization;
        _arcs = new ArcsService(libraryManager, userManager, userData, chapters, segments, mediaEncoder, config, logger);
        _requests = new ArcsRequests(http, logger);
    }

    [HttpGet]
    public async Task<ActionResult<ArcsAvailabilityDto>> GetAvailability([FromQuery] bool spoilerSafe = true)
    {
        var (user, error) = await CallerAsync(checkUser: false).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var settings = ArcsService.Settings;
        return new ArcsAvailabilityDto
        {
            Enabled = ArcsService.IsAllowed(user!.Id),
            Sharing = settings.Sharing,
            Watermark = settings.Watermark,
            MinSeconds = settings.MinSeconds,
            MaxSeconds = settings.MaxSeconds,
            DefaultSeconds = settings.DefaultSeconds,
            Spoilers = settings.Spoilers,
            ClipOfTheDay = settings.ClipOfTheDay,
            Sending = settings.Sending,
            DialogueSearch = settings.DialogueSearch,
            Requests = ArcsRequests.Available,
            YouTubeTrailers = settings.YouTubeTrailers,
            Unseen = ArcsService.IsAllowed(user.Id) ? _arcs.UnseenCount(user, _arcs.SpoilerSafe(spoilerSafe)) : 0,
        };
    }

    [HttpGet("Feed")]
    public async Task<ActionResult<ArcFeedDto>> GetFeed(
        [FromQuery] string? feed,
        [FromQuery] int count = 10,
        [FromQuery] string? cursor = null,
        [FromQuery] bool spoilerSafe = true,
        [FromQuery] int? seconds = null,
        [FromQuery] string? seriesId = null,
        [FromQuery] string? exclude = null,
        [FromQuery] string? quality = null,
        [FromQuery] string? mood = null,
        [FromQuery] bool requests = false)
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        feed = string.IsNullOrEmpty(feed) ? ArcFeeds.ForYou : feed;
        if (!ArcFeeds.IsKnown(feed))
        {
            return BadRequest("Unknown feed.");
        }

        mood = string.IsNullOrEmpty(mood) ? null : mood;
        if (mood is not null && !ArcMoods.IsKnown(mood))
        {
            return BadRequest("Unknown mood.");
        }

        ArcCursor? at = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!ArcCursor.TryDecode(cursor, out var decoded))
            {
                return BadRequest("That cursor isn't valid.");
            }

            at = decoded;
        }

        Guid? series = null;
        if (feed == ArcFeeds.Series)
        {
            if (!Guid.TryParse(seriesId, out var id))
            {
                return BadRequest("The series feed needs a seriesId.");
            }

            series = id;
        }

        var excluded = (exclude ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(1000).ToHashSet(StringComparer.Ordinal);
        List<RequestCandidate>? pool = null;
        if (requests && ArcsRequests.Available && feed is ArcFeeds.ForYou or ArcFeeds.Discover)
        {
            var mine = ArcsService.MyRequests(ArcsStore.Current.Load(user!.Id), DateTime.UtcNow);
            pool = await _requests.PoolAsync(Viewer(user), () => _arcs.RequestSeeds(user), _arcs.LibraryTmdbIds, mine, RequestWait, HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }

        var extras = new FeedExtras(mood, pool, Sent: true);
        var (clips, next, resolved) = _arcs.Feed(user!, feed, Math.Clamp(count, 1, 30), at, _arcs.SpoilerSafe(spoilerSafe), _arcs.ClampSeconds(seconds), series, excluded, extras);
        _arcs.Prewarm(resolved, quality == "low" ? "low" : "high");
        return new ArcFeedDto { Clips = clips, Cursor = next };
    }

    [HttpGet("ClipOfTheDay")]
    public async Task<ActionResult<ArcClipDto>> GetClipOfTheDay()
    {
        var (user, error) = await CallerAsync(checkUser: false).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!ArcsService.Settings.ClipOfTheDay || !ArcsService.IsAllowed(user!.Id))
        {
            return NoContent();
        }

        return _arcs.ClipOfTheDay(user) is { } clip ? clip : NoContent();
    }

    [HttpGet("Clips/{clipId}/Video")]
    public async Task<ActionResult> GetVideo(
        [FromRoute] string clipId,
        [FromQuery] int? seconds = null,
        [FromQuery] string? quality = null,
        [FromQuery] string? purpose = null,
        [FromQuery] int? subtitle = null)
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!ClipKey.TryParse(clipId, out var key))
        {
            return NotFound();
        }

        var share = purpose == "share";
        if (share && ArcsService.Settings.Sharing == "Off")
        {
            return StatusCode(403, "Your server's admin has turned off sharing Arcs.");
        }

        if (_arcs.Clip(user!, key, _arcs.ClampSeconds(seconds)) is not { } clip)
        {
            return NotFound();
        }

        quality = quality == "low" ? "low" : "high";
        var request = ArcsService.RenderRequest(clip, quality, subtitle, _mediaEncoder.EscapeSubtitleFilterPath, out var problem);
        if (request is null)
        {
            return subtitle is not null && problem is not null && problem.Contains("subtitle", StringComparison.Ordinal) ? BadRequest(problem) : NotFound(problem);
        }

        var file = await _arcs.RenderAsync(clip, request, quality, subtitle, HttpContext.RequestAborted).ConfigureAwait(false);
        if (file is null)
        {
            return StatusCode(503, "This clip couldn't be made just now.");
        }

        var range = Request.Headers.Range.ToString();
        if (share && (range.Length == 0 || range.StartsWith("bytes=0-", StringComparison.Ordinal)))
        {
            ArcsEvents.Record(_arcs, user!.Id, new[]
            {
                new ArcEventDto { ClipId = clip.Id, ItemId = clip.Item.Id.ToString("N"), Kind = ArcEventKinds.Share, Feed = "share" }
            });
        }

        return PhysicalFile(file, "video/mp4", enableRangeProcessing: true);
    }

    /// <summary>
    /// The clip as HLS for the feed, streamed while it's being made. Its files are listed as
    /// hls/{key}/..., so they're fetched from <see cref="GetHlsFile"/> beside this playlist.
    /// </summary>
    [HttpGet("Clips/{clipId}/Stream.m3u8")]
    public async Task<ActionResult> GetStream([FromRoute] string clipId, [FromQuery] int? seconds = null, [FromQuery] string? quality = null)
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!ClipKey.TryParse(clipId, out var key) || _arcs.Clip(user!, key, _arcs.ClampSeconds(seconds)) is not { } clip)
        {
            return NotFound();
        }

        quality = quality == "low" ? "low" : "high";
        var request = ArcsService.RenderRequest(clip, quality, null, _mediaEncoder.EscapeSubtitleFilterPath, out var problem);
        if (request is null)
        {
            return NotFound(problem);
        }

        var playlist = await _arcs.PlaylistAsync(clip, request, quality, FirstSegmentWait, HttpContext.RequestAborted).ConfigureAwait(false);
        if (playlist is null)
        {
            Response.Headers.RetryAfter = "2";
            return StatusCode(503, "This clip couldn't be made just now.");
        }

        Response.Headers.CacheControl = "no-cache";
        return Content(ClipRenderer.ServedPlaylist(playlist, ArcsService.HlsKey(clip, request, quality)), "application/vnd.apple.mpegurl");
    }

    /// <summary>The init segment and media segments of a clip's HLS.</summary>
    [HttpGet("Clips/{clipId}/hls/{key}/{file}")]
    public async Task<ActionResult> GetHlsFile([FromRoute] string clipId, [FromRoute] string key, [FromRoute] string file)
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!ClipKey.TryParse(clipId, out var clip) || !_arcs.CanSee(user!, clip))
        {
            return NotFound();
        }

        if (ClipRenderer.HlsFile(ClipRenderer.CacheFolder(ArcsService.DataFolder), key, clip.ToString(), file) is not { } path)
        {
            return NotFound();
        }

        // The same name can hold another render's bytes after a prune or a fall back from the GPU.
        Response.Headers.CacheControl = "no-cache";
        return PhysicalFile(path, "video/mp4", enableRangeProcessing: true);
    }

    [HttpPost("Events")]
    public async Task<ActionResult> PostEvents([FromBody] ArcEventsBody body)
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var events = body?.Events ?? new List<ArcEventDto>();
        if (events.Count > MaxEventsPerCall)
        {
            return BadRequest($"Send at most {MaxEventsPerCall} events at a time.");
        }

        ArcsEvents.Record(_arcs, user!.Id, events);
        return NoContent();
    }

    [HttpGet("Hidden")]
    public async Task<ActionResult<ArcHiddenListDto>> GetHidden()
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var now = DateTime.UtcNow;
        var items = ArcsStore.Current.Load(user!.Id).ActiveHidden(now)
            .OrderByDescending(h => h.At)
            .Select(h => new ArcHiddenItemDto { Id = h.TitleId.ToString("N"), Name = _arcs.NameOf(h.TitleId) ?? string.Empty, Kind = h.Kind, Until = h.Until })
            .ToList();
        return new ArcHiddenListDto { Items = items };
    }

    [HttpDelete("Hidden/{id}")]
    public async Task<ActionResult> DeleteHidden([FromRoute] string id)
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!Guid.TryParse(id, out var titleId))
        {
            return NotFound();
        }

        ArcsStore.Current.Change(user!.Id, d => d.Hidden.RemoveAll(h => h.TitleId == titleId));
        ArcsService.Invalidate(user.Id);
        return NoContent();
    }

    /// <summary>"Reset my recommendations": forgets this person's Arcs events, likes and hidden titles.</summary>
    [HttpDelete("Me")]
    public async Task<ActionResult> DeleteMe()
    {
        var (user, error) = await CallerAsync(checkUser: false).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        ArcsStore.Current.Reset(user!.Id);
        ArcsService.Forget(user.Id);
        ArcsRequests.Forget(user.Id);
        return NoContent();
    }

    /// <summary>Scenes found by a line of dialogue. Empty when nothing is indexed yet or dialogue search is off.</summary>
    [HttpGet("Search")]
    public async Task<ActionResult<ArcSearchDto>> Search([FromQuery] string? q, [FromQuery] int count = 20, [FromQuery] int? seconds = null, [FromQuery] bool spoilerSafe = true)
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(q) || q.Length > 200)
        {
            return new ArcSearchDto();
        }

        return new ArcSearchDto { Clips = _arcs.Search(user!, q, Math.Clamp(count, 1, 50), _arcs.ClampSeconds(seconds), _arcs.SpoilerSafe(spoilerSafe)) };
    }

    /// <summary>A film's or show's best scenes.</summary>
    [HttpGet("Top")]
    public async Task<ActionResult<ArcSearchDto>> Top([FromQuery] string? itemId, [FromQuery] int count = 10, [FromQuery] int? seconds = null, [FromQuery] bool spoilerSafe = true)
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!Guid.TryParse(itemId, out var id))
        {
            return BadRequest("Best scenes need an itemId.");
        }

        return _arcs.Top(user!, id, Math.Clamp(count, 1, 30), _arcs.ClampSeconds(seconds), _arcs.SpoilerSafe(spoilerSafe)) is { } clips
            ? new ArcSearchDto { Clips = clips }
            : NotFound();
    }

    /// <summary>Who scenes can be sent to: the caller's friends who can get them.</summary>
    [HttpGet("People")]
    public async Task<ActionResult<ArcPeopleDto>> GetPeople()
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var result = new ArcPeopleDto();
        if (ArcsService.Settings.Sending == "Off")
        {
            return result;
        }

        result.People = FriendsOf(user!).Where(CanReceive).Select(Person).ToList();
        return result;
    }

    [HttpGet("Friends")]
    public async Task<ActionResult<ArcFriendsDto>> GetFriends()
    {
        var (user, error) = await SenderAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        return new ArcFriendsDto { Code = ArcsFriends.Current.CodeFor(user!.Id), Friends = FriendsOf(user).Select(Person).ToList() };
    }

    /// <summary>Adds the person whose friend code this is; both become each other's friends.</summary>
    [HttpPost("Friends")]
    public async Task<ActionResult<ArcPersonDto>> AddFriend([FromBody] ArcFriendCodeBody body)
    {
        var (user, error) = await SenderAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var (result, friend) = ArcsFriends.Current.Add(user!.Id, body?.Code, id => _userManager.GetUserById(id) is { } u && CanReceive(u), DateTime.UtcNow);
        return result switch
        {
            FriendAddResult.Added when _userManager.GetUserById(friend) is { } added => Person(added),
            FriendAddResult.TooManyTries => StatusCode(429, "Too many tries. Try again in a while."),
            FriendAddResult.Full => BadRequest($"One of you already has {ArcsFriends.MaxFriends} friends."),
            _ => NotFound("That code isn't right."),
        };
    }

    [HttpDelete("Friends/{id}")]
    public async Task<ActionResult> RemoveFriend([FromRoute] string id)
    {
        var (user, error) = await SenderAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (Guid.TryParse(id, out var friend))
        {
            ArcsFriends.Current.Remove(user!.Id, friend);
        }

        return NoContent();
    }

    /// <summary>A new friend code: the old one stops working, friends stay.</summary>
    [HttpPost("Friends/Code")]
    public async Task<ActionResult<ArcFriendCodeDto>> NewFriendCode()
    {
        var (user, error) = await SenderAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        return new ArcFriendCodeDto { Code = ArcsFriends.Current.NewCode(user!.Id) };
    }

    [HttpPost("Send")]
    public async Task<ActionResult> Send([FromBody] ArcSendBody body)
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (ArcsService.Settings.Sending == "Off")
        {
            return StatusCode(403, "Your server's admin has turned off sending Arcs.");
        }

        if (!ClipKey.TryParse(body?.ClipId, out var key) || !Guid.TryParse(body!.ToUserId, out var to))
        {
            return BadRequest("Send needs a scene and someone to send it to.");
        }

        var recipient = _userManager.GetUserById(to);
        if (recipient is null || recipient.Id == user!.Id || !CanReceive(recipient) || !ArcsFriends.Current.AreFriends(user.Id, recipient.Id))
        {
            return NotFound("They can't get Arcs.");
        }

        var seconds = ArcsService.Settings.DefaultSeconds;
        if (_arcs.Clip(user, key, seconds) is null)
        {
            return NotFound();
        }

        if (_arcs.Clip(recipient, key, seconds) is not { } theirs)
        {
            return NotFound("They can't see this title.");
        }

        if (ArcsInbox.Current.Send(user.Id, recipient.Id, key.ToString(), body.Note, DateTime.UtcNow) == ArcSendResult.DailyLimit)
        {
            return StatusCode(429, $"You've sent {ArcsInbox.DailyLimit} scenes today. Try again tomorrow.");
        }

        ArcsService.Invalidate(recipient.Id);
        if (Plugin.Instance!.Configuration.Notifications.Enabled)
        {
            Notifications.EventStore.Current.Add(new[] { ArcsSentNotice.Event(recipient.Id, user.Username, ArcsService.TitleLine(theirs.Item), body.Note, key.ToString()) },
                                                 DateTime.UtcNow);
        }

        return NoContent();
    }

    [HttpGet("Me/Stats")]
    public async Task<ActionResult<ArcMyStatsDto>> GetMyStats()
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        return _arcs.MyStats(user!);
    }

    /// <summary>Whether someone can get scenes: Arcs on for them and their account not disabled. Hidden from login screens doesn't matter.</summary>
    public static bool CanReceive(User user) => CanReceive(user, ArcsService.IsAllowed(user.Id));

    public static bool CanReceive(User user, bool arcsAllowed) => arcsAllowed && !user.HasPermission(PermissionKind.IsDisabled);

    private List<User> FriendsOf(User user) =>
        ArcsFriends.Current.FriendsOf(user.Id).Select(_userManager.GetUserById).OfType<User>()
            .OrderBy(u => u.Username, StringComparer.CurrentCultureIgnoreCase).ToList();

    private static ArcPersonDto Person(User user) => new() { Id = user.Id.ToString("N"), Name = user.Username };

    /// <summary>The caller, for friends: Arcs on for them and sending allowed.</summary>
    private async Task<(User? User, ActionResult? Error)> SenderAsync()
    {
        var (user, error) = await CallerAsync().ConfigureAwait(false);
        if (error is null && ArcsService.Settings.Sending == "Off")
        {
            return (null, StatusCode(403, "Your server's admin has turned off sending Arcs."));
        }

        return (user, error);
    }

    /// <summary>What request clips need to know about the person: language, country and parental controls.</summary>
    private RequestViewer Viewer(User user)
    {
        var server = _config.CommonConfiguration as ServerConfiguration;
        var language = (string.IsNullOrWhiteSpace(user.AudioLanguagePreference) ? null : _localization.FindLanguageInfo(user.AudioLanguagePreference)?.TwoLetterISOLanguageName)
                       ?? server?.PreferredMetadataLanguage;
        var country = string.IsNullOrWhiteSpace(server?.MetadataCountryCode) ? "US" : server!.MetadataCountryCode;
        var unrated = user.GetPreferenceValues<Jellyfin.Data.Enums.UnratedItem>(PreferenceKind.BlockUnratedItems);
        return new RequestViewer(
            user.Id,
            string.IsNullOrWhiteSpace(language) ? "en" : language.ToLowerInvariant(),
            country.ToUpperInvariant(),
            user.MaxParentalRatingScore,
            unrated.Contains(Jellyfin.Data.Enums.UnratedItem.Movie),
            unrated.Contains(Jellyfin.Data.Enums.UnratedItem.Series),
            (rating, region) => _localization.GetRatingScore(rating, region)?.Score);
    }

    private async Task<(User? User, ActionResult? Error)> CallerAsync(bool checkUser = true)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null)
        {
            return (null, Unauthorized());
        }

        if (!ArcsService.Settings.Enabled)
        {
            return (null, StatusCode(403, "Your server's admin has turned off Arcs."));
        }

        if (checkUser && !ArcsService.IsAllowed(user.Id))
        {
            return (null, StatusCode(403, "Your server's admin has turned off Arcs for you."));
        }

        return (user, null);
    }
}

/// <summary>Turns the app's events into stored ones: checked, tied to their film or show, and applied.</summary>
public static class ArcsEvents
{
    public static void Record(ArcsService arcs, Guid userId, IEnumerable<ArcEventDto> events)
    {
        var now = DateTime.UtcNow;
        var records = new List<ArcEventRecord>();
        foreach (var e in events)
        {
            if (e is null || e.Kind is not { } kind || !ArcEventKinds.All.Contains(kind))
            {
                continue;
            }

            if (RequestKey.TryParse(e.ClipId, out var request))
            {
                if (kind is ArcEventKinds.React or ArcEventKinds.StartEpisode or ArcEventKinds.WatchFromHere or ArcEventKinds.Share)
                {
                    continue;
                }

                // Only a request or a like says something about the library title it was found from.
                var seed = kind is ArcEventKinds.Request or ArcEventKinds.Like ? ArcsRequests.SeedOf(userId, request.ToString()) : null;
                records.Add(new ArcEventRecord
                {
                    ClipId = request.ToString(),
                    ItemId = seed ?? Guid.Empty,
                    TitleId = seed ?? Guid.Empty,
                    Kind = kind,
                    WatchedSeconds = e.WatchedSeconds is { } rw && double.IsFinite(rw) ? Math.Clamp(rw, 0, 3600) : null,
                    ClipSeconds = e.ClipSeconds is { } rc && double.IsFinite(rc) ? Math.Clamp(rc, 0, 3600) : null,
                    Feed = e.Feed is { Length: <= 32 } rf ? rf : null,
                    At = When(e.At, now),
                });
                continue;
            }

            string? reaction = null;
            if (kind == ArcEventKinds.React)
            {
                if (e.Reaction is not { } r || (r != ArcReactionKinds.None && !ArcReactionKinds.All.Contains(r)))
                {
                    continue;
                }

                reaction = r;
            }

            ClipKey.TryParse(e.ClipId, out var clip);
            var itemId = Guid.TryParse(e.ItemId, out var item) ? item : clip.ItemId;
            if (itemId == Guid.Empty)
            {
                continue;
            }

            if (clip.ItemId == Guid.Empty && !ArcEventKinds.TitleOnly.Contains(kind))
            {
                continue;
            }

            Guid? series = Guid.TryParse(e.SeriesId, out var s) ? s : null;
            var at = When(e.At, now);
            records.Add(new ArcEventRecord
            {
                ClipId = clip.ItemId == Guid.Empty ? string.Empty : clip.ToString(),
                ItemId = itemId,
                TitleId = arcs.TitleOf(itemId, series),
                Kind = kind,
                WatchedSeconds = e.WatchedSeconds is { } w && double.IsFinite(w) ? Math.Clamp(w, 0, 3600) : null,
                ClipSeconds = e.ClipSeconds is { } c && double.IsFinite(c) ? Math.Clamp(c, 0, 3600) : null,
                Feed = e.Feed is { Length: <= 32 } f ? f : null,
                Reaction = reaction,
                At = at,
            });
        }

        if (records.Count == 0)
        {
            return;
        }

        var store = ArcsStore.Current;
        store.Change(userId, data => ArcsStore.Apply(data, records, now));
        var reports = records.Where(r => r.Kind == ArcEventKinds.Report && r.ClipId.Length > 0).Select(r => r.ClipId).ToList();
        if (reports.Count > 0)
        {
            store.AddReports(userId, reports);
        }

        ArcsInbox.Current.MarkSeen(userId, records.Where(r => r.Kind is ArcEventKinds.View or ArcEventKinds.Rewatch && r.ClipId.Length > 0).Select(r => r.ClipId), now);
        ArcsService.Invalidate(userId);
    }

    private static DateTime When(DateTime? at, DateTime now) => at is { } when && when <= now.AddMinutes(5) && when > now.AddDays(-30) ? when.ToUniversalTime() : now;
}
