using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArcfinSync.Api.Security;

namespace Jellyfin.Plugin.ArcfinSync.Api.Integrations;

/// <summary>
/// Calls Seerr with the key kept in the vault, as the Jellyfin user who asked: Seerr's
/// X-Api-User header makes each call run with that person's own Seerr permissions and limits,
/// never the admin's. The key never leaves the server.
/// </summary>
public sealed class SeerrClient
{
    public const string HttpClientName = "ArcfinSync.Seerr";
    public const string ApiKeyName = "seerr.apiKey";
    public const string TmdbKeyName = "tmdb.key";

    /// <summary>Jellyfin user ID ("N" form) to Seerr user ID, refreshed every ten minutes.</summary>
    private static readonly ConcurrentDictionary<string, int> Users = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Jellyfin user ID ("N" form) to that person's Seerr permission bits, loaded with <see cref="Users"/>.</summary>
    private static readonly ConcurrentDictionary<string, int> Permissions = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _usersLoaded = DateTime.MinValue;
    private static readonly SemaphoreSlim UsersGate = new(1, 1);

    private readonly IHttpClientFactory _http;

    public SeerrClient(IHttpClientFactory http)
    {
        _http = http;
    }

    /// <summary>What happened the last time the server talked to Seerr, for the dashboard.</summary>
    public static SeerrHealth Health { get; private set; } = new(null, null, null);

    public static string? BaseUrl => Plugin.Instance?.Configuration.Integrations.SeerrUrl?.TrimEnd('/');

    public static bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && SecretVault.Current.Has(ApiKeyName);

    public static void ForgetUsers()
    {
        Users.Clear();
        Permissions.Clear();
        _usersLoaded = DateTime.MinValue;
    }

    /// <summary>Sends one call to Seerr. <paramref name="seerrUserId"/> null calls as the key's own (admin) user.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? query, byte[]? body, int? seerrUserId, CancellationToken token)
        => SendAsync(BaseUrl, SecretVault.Current.Get(ApiKeyName), method, path, query, body, seerrUserId, token);

    private async Task<HttpResponseMessage> SendAsync(string? baseUrl, string? key, HttpMethod method, string path, string? query, byte[]? body, int? seerrUserId, CancellationToken token)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(baseUrl))
        {
            throw new InvalidOperationException("No Seerr address or key.");
        }

        var request = new HttpRequestMessage(method, $"{baseUrl}/api/v1/{path}{query}");
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        if (seerrUserId is { } id)
        {
            request.Headers.TryAddWithoutValidation("X-Api-User", id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        var client = _http.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(20);
        try
        {
            var response = await client.SendAsync(request, token).ConfigureAwait(false);
            Record(response.StatusCode is HttpStatusCode.Unauthorized && seerrUserId is null ? "keyRejected"
                   : (int)response.StatusCode >= 500 ? "error" : "ok", (int)response.StatusCode >= 500 ? $"Seerr answered {(int)response.StatusCode}." : null);
            return response;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
        {
            Record("unreachable", e.Message);
            throw new SeerrUnreachableException(e);
        }
    }

    /// <summary>
    /// The caller's Seerr user, by their Jellyfin account. With "add Seerr users" on, someone
    /// without one gets one, imported from Jellyfin the way Seerr's own Jellyfin sign-in does.
    /// </summary>
    public async Task<int?> SeerrUserAsync(Guid jellyfinUserId, CancellationToken token)
    {
        var id = jellyfinUserId.ToString("N");
        if (DateTime.UtcNow - _usersLoaded < TimeSpan.FromMinutes(10) && Users.TryGetValue(id, out var cached))
        {
            return cached;
        }

        await UsersGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await LoadUsersAsync(token).ConfigureAwait(false);
            if (Users.TryGetValue(id, out var found))
            {
                return found;
            }

            if (Plugin.Instance?.Configuration.Integrations.AddSeerrUsers != true)
            {
                return null;
            }

            var body = JsonSerializer.SerializeToUtf8Bytes(new { jellyfinUserIds = new[] { id } });
            using var response = await SendAsync(HttpMethod.Post, "user/import-from-jellyfin", null, body, null, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await LoadUsersAsync(token).ConfigureAwait(false);
            return Users.TryGetValue(id, out var added) ? added : null;
        }
        finally
        {
            UsersGate.Release();
        }
    }

    /// <summary>
    /// Every Seerr user linked to a Jellyfin account, with their Seerr permission bits, from the
    /// same list <see cref="SeerrUserAsync"/> keeps (read again after ten minutes).
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> UserPermissionsAsync(CancellationToken token)
    {
        if (DateTime.UtcNow - _usersLoaded >= TimeSpan.FromMinutes(10) || Permissions.IsEmpty)
        {
            await UsersGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await LoadUsersAsync(token).ConfigureAwait(false);
            }
            finally
            {
                UsersGate.Release();
            }
        }

        var result = new Dictionary<Guid, int>();
        foreach (var (jf, bits) in Permissions)
        {
            if (Guid.TryParse(jf, out var id))
            {
                result[id] = bits;
            }
        }

        return result;
    }

    /// <summary>Checks the address and key: the key's own user must come back.</summary>
    public async Task<SeerrHealth> TestAsync(string url, string key, CancellationToken token)
    {
        var saved = url == BaseUrl && key == SecretVault.Current.Get(ApiKeyName);
        try
        {
            using var response = await SendAsync(url, key, HttpMethod.Get, "auth/me", null, null, null, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Record("keyRejected", "Seerr refused the key.");
            }
            else if (!response.IsSuccessStatusCode)
            {
                Record("error", $"Seerr answered {(int)response.StatusCode}.");
            }
            else if (saved)
            {
                ForgetUsers();
                await LoadUsersAsync(token).ConfigureAwait(false);
            }
        }
        catch (SeerrUnreachableException)
        {
        }
        catch (OperationCanceledException)
        {
            Record("unreachable", "Seerr didn't answer within 15 seconds.");
        }

        return Health;
    }

    private async Task LoadUsersAsync(CancellationToken token)
    {
        var skip = 0;
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var permissions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (skip < 10000)
        {
            using var response = await SendAsync(HttpMethod.Get, "user", $"?take=100&skip={skip}", null, null, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    Record("keyRejected", "Seerr refused the key.");
                }

                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false));
            var results = doc.RootElement.TryGetProperty("results", out var r) ? r.EnumerateArray().ToList() : new List<JsonElement>();
            foreach (var user in results)
            {
                if (user.TryGetProperty("jellyfinUserId", out var jf) && jf.ValueKind == JsonValueKind.String
                    && user.TryGetProperty("id", out var sid) && sid.TryGetInt32(out var seerrId))
                {
                    var key = jf.GetString()!.Replace("-", string.Empty, StringComparison.Ordinal);
                    found[key] = seerrId;
                    permissions[key] = user.TryGetProperty("permissions", out var bits) && bits.TryGetInt32(out var value) ? value : 0;
                }
            }

            if (results.Count < 100)
            {
                break;
            }

            skip += 100;
        }

        Users.Clear();
        foreach (var (jf, sid) in found)
        {
            Users[jf] = sid;
        }

        Permissions.Clear();
        foreach (var (jf, bits) in permissions)
        {
            Permissions[jf] = bits;
        }

        _usersLoaded = DateTime.UtcNow;
    }

    private static void Record(string state, string? message) => Health = new SeerrHealth(state, message, DateTime.UtcNow);
}

public sealed record SeerrHealth(string? State, string? Message, DateTime? Checked);

public sealed class SeerrUnreachableException : Exception
{
    public SeerrUnreachableException(Exception inner)
        : base("Seerr can't be reached.", inner)
    {
    }
}
