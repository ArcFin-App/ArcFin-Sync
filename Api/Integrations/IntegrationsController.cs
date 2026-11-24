using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
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
            using var response = await client.SendAsync(new HttpMethod(method), path, Request.QueryString.Value, body, seerrUser, HttpContext.RequestAborted).ConfigureAwait(false);
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
