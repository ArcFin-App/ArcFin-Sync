using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArcfinSync.Api.Security;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Integrations;

/// <summary>
/// Seerr and TMDB for the apps, through the server, with keys that stay on it. Every call needs a
/// sign-in to this server; Seerr calls run as the caller's own Seerr account.
/// </summary>
[ApiController]
[Route("ArcfinSync")]
[Authorize]
public class IntegrationsController : ControllerBase
{
    private const int MaxBody = 64 * 1024;
    private static readonly Regex TmdbPath = new(@"^(trending|movie|tv|person|search|discover|genre|collection|configuration|watch|find)(/[a-z0-9_]+){0,4}$",
                                                 RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<IntegrationsController> _logger;

    public IntegrationsController(IAuthorizationContext authContext, IUserManager userManager, IHttpClientFactory http, ILogger<IntegrationsController> logger)
    {
        _authContext = authContext;
        _userManager = userManager;
        _http = http;
        _logger = logger;
    }

    /// <summary>Which services the server answers for, so the app knows to use them instead of its own sign-in.</summary>
    [HttpGet("Integrations")]
    public ActionResult<IntegrationsDto> GetIntegrations()
    {
        return new IntegrationsDto { Seerr = SeerrClient.IsConfigured, Tmdb = SecretVault.Current.Has(SeerrClient.TmdbKeyName) };
    }

    [HttpGet("Seerr/api/v1/{**path}")]
    [HttpPost("Seerr/api/v1/{**path}")]
    [HttpDelete("Seerr/api/v1/{**path}")]
    [RequestSizeLimit(MaxBody)]
    public async Task<ActionResult> Seerr([FromRoute] string path)
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null)
        {
            return Unauthorized();
        }

        if (!SeerrClient.IsConfigured)
        {
            return NotFound(new { message = "Seerr isn't set up on this server." });
        }

        var method = Request.Method;
        if (!SeerrRoutes.IsAllowed(method, path))
        {
            return StatusCode(403, new { message = "The server doesn't pass that Seerr call on." });
        }

        byte[]? body = null;
        if (!HttpMethods.IsGet(method) && Request.ContentLength is > 0)
        {
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer, HttpContext.RequestAborted).ConfigureAwait(false);
            body = buffer.ToArray();
        }

        var client = new SeerrClient(_http);
        int? seerrUser;
        try
        {
            seerrUser = await client.SeerrUserAsync(user.Id, HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (SeerrUnreachableException)
        {
            return Queue(user.Id, method, path, body) ?? StatusCode(502, new { message = "The server can't reach Seerr right now." });
        }

        if (seerrUser is null)
        {
            return StatusCode(403, new { message = "You don't have a Seerr account yet. Ask your server's admin to add you." });
        }

        try
        {
            if (HttpMethods.IsGet(method) && SeerrAnswers.Keeps(path))
            {
                // Not tied to this call's cancellation: others asking the same may share the answer.
                var answer = await SeerrAnswers.GetAsync($"{seerrUser}|{path}{Request.QueryString.Value}", async () =>
                {
                    using var shared = await client.SendAsync(HttpMethod.Get, path, Request.QueryString.Value, null, seerrUser, CancellationToken.None).ConfigureAwait(false);
                    return new SeerrAnswer((int)shared.StatusCode, shared.Content.Headers.ContentType?.ToString(),
                                           await shared.Content.ReadAsByteArrayAsync(CancellationToken.None).ConfigureAwait(false));
                }).WaitAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                return Passthrough(answer.Body, answer.ContentType, answer.Status);
            }

            using var response = await client.SendAsync(new HttpMethod(method), path, Request.QueryString.Value, body, seerrUser, HttpContext.RequestAborted).ConfigureAwait(false);
            if (!HttpMethods.IsGet(method) && response.IsSuccessStatusCode)
            {
                SeerrAnswers.Clear();
            }

            if ((int)response.StatusCode >= 500 && Queue(user.Id, method, path, body) is { } queued)
            {
                return queued;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _logger.LogWarning("Seerr refused the server's key");
                return StatusCode(502, new { message = "Seerr refused the server's key. Ask your server's admin to check it." });
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            return Passthrough(bytes, response.Content.Headers.ContentType?.ToString(), (int)response.StatusCode);
        }
        catch (SeerrUnreachableException)
        {
            return Queue(user.Id, method, path, body) ?? StatusCode(502, new { message = "The server can't reach Seerr right now." });
        }
    }

    /// <summary>
    /// One page of Seerr's requests, as the person sees them in Seerr, with each title's name and
    /// artwork filled in ("arcfin" on each row), so a client needs one call per page instead of one
    /// per request. The first page also starts filling in the rest, so later pages are ready.
    /// </summary>
    [HttpGet("Seerr/Requests")]
    public Task<ActionResult> SeerrRequests([FromQuery] string? filter, [FromQuery] string? sort, [FromQuery] int take = 20, [FromQuery] int skip = 0)
    {
        take = Math.Clamp(take, 1, SeerrTitles.MaxTake);
        skip = Math.Max(0, skip);
        var query = $"?take={take}&skip={skip}&filter={Uri.EscapeDataString(filter ?? "all")}&sort={Uri.EscapeDataString(sort ?? "added")}";
        return FilledPage("request", query, skip == 0 ? (client, seerrUser, total) => WarmRequests(client, seerrUser, filter, sort, take, total) : null);
    }

    /// <summary>One page of the person's Seerr watchlist (20 a page, as Seerr serves it), with titles and artwork filled in.</summary>
    [HttpGet("Seerr/Watchlist")]
    public Task<ActionResult> SeerrWatchlist([FromQuery] int page = 1)
        => FilledPage("discover/watchlist", $"?page={Math.Clamp(page, 1, 500)}", null);

    /// <summary>Age ratings for up to 100 titles ("movie:603,tv:1399") in a country, as { "movie:603": "15" }; empty where TMDB has none.</summary>
    [HttpGet("Seerr/Ratings")]
    public async Task<ActionResult> SeerrRatings([FromQuery] string? titles, [FromQuery] string? region)
    {
        var (user, client, problem) = await SeerrCaller().ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var wanted = (titles ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Split(':')).Where(p => p.Length == 2 && p[0] is "movie" or "tv" && int.TryParse(p[1], out _))
            .Select(p => (p[0], int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture))).Distinct().Take(SeerrTitles.MaxTake).ToList();
        var country = (region ?? "US").Trim().ToUpperInvariant();
        var cards = await SeerrTitles.CardsAsync(client!, wanted, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(wanted.ToDictionary(t => SeerrTitles.Key(t.Item1, t.Item2), t => cards.TryGetValue(SeerrTitles.Key(t.Item1, t.Item2), out var c) ? c.Rating(country) : (string?)null));
    }

    private async Task<(Jellyfin.Database.Implementations.Entities.User? User, SeerrClient? Client, ActionResult? Problem)> SeerrCaller()
    {
        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var user = auth.IsAuthenticated ? _userManager.GetUserById(auth.UserId) : null;
        if (user is null)
        {
            return (null, null, Unauthorized());
        }

        return SeerrClient.IsConfigured ? (user, new SeerrClient(_http), null) : (user, null, NotFound(new { message = "Seerr isn't set up on this server." }));
    }

    private async Task<ActionResult> FilledPage(string path, string query, Action<SeerrClient, int, int>? afterwards)
    {
        var (user, client, problem) = await SeerrCaller().ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        try
        {
            var seerrUser = await client!.SeerrUserAsync(user!.Id, HttpContext.RequestAborted).ConfigureAwait(false);
            if (seerrUser is null)
            {
                return StatusCode(403, new { message = "You don't have a Seerr account yet. Ask your server's admin to add you." });
            }

            using var response = await client.SendAsync(HttpMethod.Get, path, query, null, seerrUser, HttpContext.RequestAborted).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || System.Text.Json.Nodes.JsonNode.Parse(json) is not { } page)
            {
                return Passthrough(System.Text.Encoding.UTF8.GetBytes(json), response.Content.Headers.ContentType?.ToString(), (int)response.StatusCode);
            }

            var cards = await SeerrTitles.CardsAsync(client, SeerrTitles.Titles(page).OfType<(string, int)>(), HttpContext.RequestAborted).ConfigureAwait(false);
            SeerrTitles.Fill(page, cards);
            afterwards?.Invoke(client, seerrUser.Value, page["pageInfo"]?["results"] is System.Text.Json.Nodes.JsonValue total && total.TryGetValue<int>(out var t) ? t : 0);
            return Content(page.ToJsonString(), "application/json");
        }
        catch (SeerrUnreachableException)
        {
            return StatusCode(502, new { message = "The server can't reach Seerr right now." });
        }
    }

    /// <summary>Fills in the titles of the rest of the person's requests in the background, so later pages answer at once.</summary>
    private void WarmRequests(SeerrClient client, int seerrUser, string? filter, string? sort, int take, int total)
    {
        if (total <= take || Interlocked.CompareExchange(ref _warming, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                for (var skip = take; skip < Math.Min(total, 1000); skip += SeerrTitles.MaxTake)
                {
                    var query = $"?take={SeerrTitles.MaxTake}&skip={skip}&filter={Uri.EscapeDataString(filter ?? "all")}&sort={Uri.EscapeDataString(sort ?? "added")}";
                    using var response = await client.SendAsync(HttpMethod.Get, "request", query, null, seerrUser, CancellationToken.None).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode || System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)) is not { } page)
                    {
                        return;
                    }

                    await SeerrTitles.CardsAsync(client, SeerrTitles.Titles(page).OfType<(string, int)>(), CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (error is HttpRequestException or SeerrUnreachableException or System.Text.Json.JsonException or InvalidOperationException or TaskCanceledException)
            {
                _logger.LogDebug(error, "Arcfin Sync: filling in Seerr request titles stopped");
            }
            finally
            {
                Interlocked.Exchange(ref _warming, 0);
            }
        });
    }

    private static int _warming;

    [HttpGet("Tmdb/3/{**path}")]
    public async Task<ActionResult> Tmdb([FromRoute] string path)
    {
        var key = SecretVault.Current.Get(SeerrClient.TmdbKeyName);
        if (key is null)
        {
            return NotFound(new { message = "TMDB isn't set up on this server." });
        }

        if (!TmdbPath.IsMatch(path))
        {
            return StatusCode(403, new { message = "The server doesn't pass that TMDB call on." });
        }

        var query = string.Join("&", Request.Query.Where(q => !q.Key.Equals("api_key", StringComparison.OrdinalIgnoreCase))
            .SelectMany(q => q.Value.Select(v => Uri.EscapeDataString(q.Key) + "=" + Uri.EscapeDataString(v ?? string.Empty))));
        var bearer = key.Length > 40;
        if (!bearer)
        {
            query = (query.Length > 0 ? query + "&" : string.Empty) + "api_key=" + Uri.EscapeDataString(key);
        }

        var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.themoviedb.org/3/{path}" + (query.Length > 0 ? "?" + query : string.Empty));
        request.Headers.Accept.ParseAdd("application/json");
        if (bearer)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        }

        var client = _http.CreateClient(SeerrClient.HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(20);
        try
        {
            using var response = await client.SendAsync(request, HttpContext.RequestAborted).ConfigureAwait(false);
            var bytes = await response.Content.ReadAsByteArrayAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            return Passthrough(bytes, "application/json", (int)response.StatusCode);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(502, new { message = "The server can't reach TMDB right now." });
        }
    }

    private static ContentResult Passthrough(byte[] bytes, string? contentType, int status)
    {
        return new ContentResult { Content = System.Text.Encoding.UTF8.GetString(bytes), ContentType = contentType ?? "application/json", StatusCode = status };
    }

    /// <summary>A new request that couldn't reach Seerr is kept and sent later; anything else isn't.</summary>
    private ActionResult? Queue(Guid userId, string method, string path, byte[]? body)
    {
        if (!SeerrRoutes.IsNewRequest(method, path) || body is null)
        {
            return null;
        }

        SeerrQueue.Current.Add(userId, body, DateTime.UtcNow);
        return StatusCode(202, new { queued = true, message = "Seerr can't be reached right now. The server will send your request as soon as it can." });
    }
}

public sealed class IntegrationsDto
{
    public bool Seerr { get; set; }

    public bool Tmdb { get; set; }
}
