using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ArcfinSync.Api.PickTogether;

/// <summary>
/// Pick Together: people on this server swipe through titles until they agree on one. Sessions
/// live in memory and end after six hours without activity. People join with the session's
/// code (typed, or from a QR code in the app); nothing about the server is shared, and every
/// call needs a sign-in to this server. An administrator can turn the
/// feature off for everyone or for chosen users; every call checks it.
/// </summary>
[ApiController]
[Route("ArcfinSync/PickTogether")]
public class PickTogetherController : ControllerBase
{
    private const string CodeLetters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int PoolLimit = 1500;
    private static readonly TimeSpan Expiry = TimeSpan.FromHours(6);
    private static readonly ConcurrentDictionary<string, PickSession> Sessions = new();

    /// <summary>Codes that matched no session, per user, so codes can't be guessed by trying them all.</summary>
    private static readonly ConcurrentDictionary<Guid, List<DateTime>> WrongCodes = new();
    private const int WrongCodeLimit = 10;
    private static readonly TimeSpan WrongCodeWindow = TimeSpan.FromMinutes(10);

    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;

    public PickTogetherController(IAuthorizationContext authContext, IUserManager userManager, ILibraryManager libraryManager)
    {
        _authContext = authContext;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <summary>Whether the caller may use Pick Together.</summary>
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<PickAvailabilityDto>> GetAvailability()
    {
        var (user, _, error) = await CallerAsync(checkAccess: false).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        return new PickAvailabilityDto { Enabled = IsAllowed(user!.Id) };
    }

    [HttpPost("Sessions")]
    [Authorize]
    public async Task<ActionResult<PickSessionDto>> Create([FromBody] PickCreateBody body)
    {
        var (user, key, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (body is null || (!body.Movies && !body.Shows))
        {
            return BadRequest("Pick movies, shows or both.");
        }

        var options = new PickOptions(body.Movies, body.Shows, (body.Genres ?? new List<string>()).Where(g => !string.IsNullOrWhiteSpace(g)).Take(20).ToList(),
                                      body.IncludeWatched, ParseOrder(body.Order), ParseRule(body.Rule), body.SameOrder);
        var items = Query(user!, options);
        if (items.Count == 0)
        {
            return NotFound("Nothing on this server fits those choices. Try fewer genres, or include watched titles.");
        }

        Sweep();
        var now = DateTime.UtcNow;
        LeaveOtherSessions(key!, null, now);
        var pool = items.Select(i => new PickTitle(i.Id, i.CommunityRating ?? 0, i.DateCreated)).ToList();
        string code;
        PickSession session;
        do
        {
            code = NewCode();
            session = new PickSession(code, key!, options, pool, now);
        }
        while (!Sessions.TryAdd(code, session));

        lock (session)
        {
            session.Join(key!, user!.Id, user.Username, items.Select(i => i.Id).ToHashSet(), now).DeviceName = DeviceName();
            return ToDto(session, key!, user, now);
        }
    }

    [HttpPost("Sessions/{code}/Join")]
    [Authorize]
    public async Task<ActionResult<PickSessionDto>> Join([FromRoute] string code)
    {
        var (user, key, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        var now = DateTime.UtcNow;
        if (TooManyWrongCodes(user!.Id, now))
        {
            return StatusCode(429, "Too many codes that didn't work. Wait a few minutes, then try again.");
        }

        if (!Sessions.TryGetValue(Normalize(code), out var session))
        {
            WrongCodes.AddOrUpdate(user.Id, _ => new List<DateTime> { now }, (_, list) => { lock (list) { list.Add(now); } return list; });
            return NotFound("There's no session with that code. It may have ended.");
        }

        LeaveOtherSessions(key!, session.Code, now);
        var eligible = Query(user, session.Options).Select(i => i.Id).ToHashSet();
        lock (session)
        {
            try
            {
                session.Join(key!, user.Id, user.Username, eligible, now).DeviceName = DeviceName();
            }
            catch (InvalidOperationException e)
            {
                return Conflict(e.Message);
            }

            return ToDto(session, key!, user, now);
        }
    }

    /// <summary>The caller's view of the session. Polling it also marks the caller as present.</summary>
    [HttpGet("Sessions/{code}")]
    [Authorize]
    public async Task<ActionResult<PickSessionDto>> Get([FromRoute] string code)
    {
        var (user, key, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!Sessions.TryGetValue(Normalize(code), out var session))
        {
            return NotFound("This session has ended.");
        }

        var now = DateTime.UtcNow;
        lock (session)
        {
            if (!session.People.ContainsKey(key!))
            {
                return NotFound(session.Removed.Contains(key!)
                    ? "The person who started this session removed you from it."
                    : "You're not in this session.");
            }

            session.Touch(key!, now);
            session.CheckMatches(now);
            return ToDto(session, key!, user!, now);
        }
    }

    [HttpPost("Sessions/{code}/Swipe")]
    [Authorize]
    public async Task<ActionResult<PickSessionDto>> Swipe([FromRoute] string code, [FromBody] PickSwipeBody body)
    {
        var (user, key, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (body is null || !Sessions.TryGetValue(Normalize(code), out var session))
        {
            return NotFound("This session has ended.");
        }

        var now = DateTime.UtcNow;
        lock (session)
        {
            session.Swipe(key!, body.ItemId, body.Like, now);
            return ToDto(session, key!, user!, now);
        }
    }

    /// <summary>The caller wants to watch this match with everyone, in sync over SyncPlay.</summary>
    [HttpPost("Sessions/{code}/Together")]
    [Authorize]
    public async Task<ActionResult<PickSessionDto>> WatchTogether([FromRoute] string code, [FromBody] PickTogetherBody body)
    {
        return await WithSession(code, (session, key, now) => session.JoinTogether(key, body?.ItemId ?? Guid.Empty, now)).ConfigureAwait(false);
    }

    [HttpDelete("Sessions/{code}/Together")]
    [Authorize]
    public async Task<ActionResult<PickSessionDto>> NotTogether([FromRoute] string code)
    {
        return await WithSession(code, (session, key, now) => session.LeaveTogether(key, now)).ConfigureAwait(false);
    }

    /// <summary>The leader's device made the SyncPlay group; the others join it from here.</summary>
    [HttpPost("Sessions/{code}/Together/Group")]
    [Authorize]
    public async Task<ActionResult<PickSessionDto>> TogetherGroup([FromRoute] string code, [FromBody] PickTogetherGroupBody body)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.GroupId) || body.GroupId.Length > 64)
        {
            return BadRequest("No group.");
        }

        return await WithSession(code, (session, key, now) => session.SetTogetherGroup(key, body.GroupId, body.PlayItemId, body.StartTicks, now)).ConfigureAwait(false);
    }

    /// <summary>The host stops new people joining, even with the code, or lets them again.</summary>
    [HttpPost("Sessions/{code}/Lock")]
    [Authorize]
    public async Task<ActionResult<PickSessionDto>> Lock([FromRoute] string code, [FromBody] PickLockBody body)
    {
        return await WithSession(code, (session, key, now) =>
        {
            if (session.HostKey == key)
            {
                session.Locked = body?.Locked ?? true;
            }
        }).ConfigureAwait(false);
    }

    /// <summary>The host takes someone out of the session; that device can't join it again.</summary>
    [HttpDelete("Sessions/{code}/People/{personId}")]
    [Authorize]
    public async Task<ActionResult<PickSessionDto>> RemovePerson([FromRoute] string code, [FromRoute] string personId)
    {
        return await WithSession(code, (session, key, now) =>
        {
            var target = session.People.Keys.FirstOrDefault(k => PersonId(session, k) == personId);
            if (target is not null)
            {
                session.Remove(key, target, now);
            }
        }).ConfigureAwait(false);
    }

    private async Task<ActionResult<PickSessionDto>> WithSession(string code, Action<PickSession, string, DateTime> change)
    {
        var (user, key, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!Sessions.TryGetValue(Normalize(code), out var session))
        {
            return NotFound("This session has ended.");
        }

        var now = DateTime.UtcNow;
        lock (session)
        {
            if (!session.People.ContainsKey(key!))
            {
                return NotFound("You're not in this session.");
            }

            change(session, key!, now);
            return ToDto(session, key!, user!, now);
        }
    }

    /// <summary>Starts a new round for everyone, with titles nobody has been dealt yet.</summary>
    [HttpPost("Sessions/{code}/Restart")]
    [Authorize]
    public async Task<ActionResult<PickSessionDto>> Restart([FromRoute] string code)
    {
        var (user, key, error) = await CallerAsync().ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (!Sessions.TryGetValue(Normalize(code), out var session))
        {
            return NotFound("This session has ended.");
        }

        var now = DateTime.UtcNow;
        lock (session)
        {
            if (!session.People.ContainsKey(key!))
            {
                return NotFound("You're not in this session.");
            }

            if (session.HostKey != key)
            {
                return StatusCode(403, "Only the person who started the session can deal new titles.");
            }

            session.Restart(now);
            return ToDto(session, key!, user!, now);
        }
    }

    [HttpDelete("Sessions/{code}/Me")]
    [Authorize]
    public async Task<ActionResult> Leave([FromRoute] string code)
    {
        var (_, key, error) = await CallerAsync(checkAccess: false).ConfigureAwait(false);
        if (error is not null)
        {
            return error;
        }

        if (Sessions.TryGetValue(Normalize(code), out var session))
        {
            lock (session)
            {
                session.Leave(key!, DateTime.UtcNow);
                if (session.People.Count == 0)
                {
                    Sessions.TryRemove(session.Code, out _);
                }
            }
        }

        return NoContent();
    }

    private async Task<(User? User, string? Key, ActionResult? Error)> CallerAsync(bool checkAccess = true)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null)
        {
            return (null, null, Unauthorized());
        }

        if (checkAccess && !IsAllowed(user.Id))
        {
            return (null, null, StatusCode(403, "Your server's admin has turned off Pick Together for you."));
        }

        _deviceName = auth.Device ?? string.Empty;
        return (user, $"{user.Id:N}:{auth.DeviceId}", null);
    }

    private string _deviceName = string.Empty;

    private string DeviceName() => _deviceName.Length > 40 ? _deviceName[..40] : _deviceName;

    /// <summary>A device is in one session at a time: starting or joining another leaves the old one.</summary>
    private static void LeaveOtherSessions(string key, string? keep, DateTime now)
    {
        foreach (var other in Sessions.Values)
        {
            if (other.Code == keep)
            {
                continue;
            }

            lock (other)
            {
                if (other.People.ContainsKey(key))
                {
                    other.Leave(key, now);
                    if (other.People.Count == 0)
                    {
                        Sessions.TryRemove(other.Code, out _);
                    }
                }
            }
        }
    }

    private static bool TooManyWrongCodes(Guid userId, DateTime now)
    {
        if (!WrongCodes.TryGetValue(userId, out var list))
        {
            return false;
        }

        lock (list)
        {
            list.RemoveAll(t => now - t > WrongCodeWindow);
            return list.Count >= WrongCodeLimit;
        }
    }

    /// <summary>
    /// How a person is named to others: an id that says nothing about their device, so the
    /// host can remove them without device IDs being handed out.
    /// </summary>
    private static string PersonId(PickSession session, string key)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(session.Code + "|" + key));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static bool IsAllowed(Guid userId)
    {
        var settings = Plugin.Instance!.Configuration.PickTogether;
        return settings.Enabled && !settings.DisabledUsers.Contains(userId);
    }

    /// <summary>Titles the user can see that fit the options; unwatched ones unless watched titles count.</summary>
    private List<BaseItem> Query(User user, PickOptions options)
    {
        var kinds = new List<BaseItemKind>();
        if (options.Movies)
        {
            kinds.Add(BaseItemKind.Movie);
        }

        if (options.Shows)
        {
            kinds.Add(BaseItemKind.Series);
        }

        // Each type on its own: the played filter means something different for a show (every
        // episode watched), and a combined query can mix the two up.
        var found = new List<BaseItem>();
        foreach (var kind in kinds)
        {
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { kind },
                Recursive = true,
                IsVirtualItem = false,
                Genres = options.Genres.ToArray(),
                IsPlayed = options.IncludeWatched ? null : false,
                OrderBy = new[] { (ItemSortBy.CommunityRating, SortOrder.Descending) },
                Limit = PoolLimit
            };
            found.AddRange(_libraryManager.GetItemList(query));
        }

        return found;
    }

    private PickSessionDto ToDto(PickSession session, string key, User user, DateTime now)
    {
        var present = session.Present(now).Select(p => p.Key).ToHashSet();
        return new PickSessionDto
        {
            Code = session.Code,
            Round = session.Round,
            IsHost = session.HostKey == key,
            Locked = session.Locked,
            Options = new PickOptionsDto
            {
                Movies = session.Options.Movies,
                Shows = session.Options.Shows,
                Genres = session.Options.Genres.ToList(),
                IncludeWatched = session.Options.IncludeWatched,
                Order = session.Options.Order.ToString(),
                Rule = session.Options.Rule.ToString(),
                SameOrder = session.Options.SameOrder
            },
            People = session.People.Values.OrderBy(p => p.Key == key ? 0 : 1).Select(p => new PickPersonDto
            {
                Id = PersonId(session, p.Key),
                IsHost = p.Key == session.HostKey,
                // Two devices on one shared profile: say which is which.
                Name = session.People.Values.Count(o => o.UserId == p.UserId) > 1 && p.DeviceName.Length > 0 ? $"{p.Name} ({p.DeviceName})" : p.Name,
                IsMe = p.Key == key,
                IsAway = !present.Contains(p.Key),
                Swiped = p.Swiped.Count,
                Total = p.Order.Count(id => session.Playable(id, now))
            }).ToList(),
            Cards = session.Next(key, 8, now).Select(id => Card(id, user)).Where(c => c is not null).Select(c => c!).ToList(),
            Remaining = session.Remaining(key, now),
            // Card() leaves out a match this person can't see, like an earlier one from before a
            // restricted profile joined.
            Matches = session.Matches.Select(m => (Match: m, Card: Card(m.Id, user))).Where(x => x.Card is not null)
                .Select(x => new PickMatchDto { Item = x.Card!, Round = x.Match.Round, At = x.Match.At }).ToList(),
            Together = session.Together is not { } plan ? null : new PickTogetherDto
            {
                ItemId = plan.ItemId,
                IAmIn = plan.In.Contains(key),
                IAmLeader = plan.Leader == key,
                Ready = session.TogetherReady(now),
                Waiting = session.Present(now).Where(p => !plan.In.Contains(p.Key)).Select(p => p.Name).ToList(),
                GroupId = plan.GroupId,
                PlayItemId = plan.PlayItemId,
                StartTicks = plan.StartTicks
            }
        };
    }

    private PickCardDto? Card(Guid id, User user)
    {
        var item = _libraryManager.GetItemById(id);
        if (item is null || !item.IsVisible(user))
        {
            return null;
        }

        var overview = item.Overview ?? string.Empty;
        return new PickCardDto
        {
            Id = item.Id,
            Name = item.Name,
            Type = item is MediaBrowser.Controller.Entities.TV.Series ? "Series" : "Movie",
            Year = item.ProductionYear,
            Rating = item.CommunityRating,
            OfficialRating = item.OfficialRating,
            Overview = overview.Length > 400 ? overview[..400].TrimEnd() + "…" : overview,
            RuntimeMinutes = item.RunTimeTicks is long ticks && ticks > 0 ? (int)(ticks / TimeSpan.TicksPerMinute) : null,
            Genres = (item.Genres ?? Array.Empty<string>()).Take(3).ToList()
        };
    }

    private static void Sweep()
    {
        var cutoff = DateTime.UtcNow - Expiry;
        foreach (var pair in Sessions)
        {
            if (pair.Value.LastActivity < cutoff)
            {
                Sessions.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string NewCode() => new string(Enumerable.Range(0, 6).Select(_ => CodeLetters[RandomNumberGenerator.GetInt32(CodeLetters.Length)]).ToArray());

    private static string Normalize(string code) => (code ?? string.Empty).Trim().ToUpperInvariant();

    private static PickOrder ParseOrder(string? value) => Enum.TryParse<PickOrder>(value, true, out var order) ? order : PickOrder.TopRated;

    private static PickRule ParseRule(string? value) => Enum.TryParse<PickRule>(value, true, out var rule) ? rule : PickRule.Everyone;
}
