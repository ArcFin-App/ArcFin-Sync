using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArcfinSync.Api.Integrations;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Notifications;

/// <summary>
/// Looks at Seerr's newest requests every ten minutes, or straight away when Seerr's webhook
/// says something changed, and records approved, declined and available ones as events for the
/// person who asked. The webhook only wakes this up: what changed always comes from Seerr itself.
/// </summary>
public sealed class SeerrWatchService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim WakeSignal = new(0, 1);
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SeerrWatchService> _logger;

    public SeerrWatchService(IHttpClientFactory http, ILogger<SeerrWatchService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Checks soon; several calls before the check runs make one check.</summary>
    public static void Wake()
    {
        try
        {
            WakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await WakeSignal.WaitAsync(Every, stoppingToken).ConfigureAwait(false);
                // Seerr sends several webhooks for one change; let them settle into one check.
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                await CheckAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SeerrUnreachableException)
            {
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Couldn't check Seerr for request changes");
            }
        }
    }

    public async Task CheckAsync(CancellationToken token)
    {
        if (Plugin.Instance?.Configuration.Notifications.Enabled != true || !SeerrClient.IsConfigured)
        {
            return;
        }

        var client = new SeerrClient(_http);
        using var response = await client.SendAsync(HttpMethod.Get, "request", "?take=100&skip=0&sort=modified&filter=all", null, null, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var requests = SeerrRequests.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var states = new SeerrStateFile(Plugin.Instance.DataFolderPath);
        var changes = states.Update(requests);
        if (changes.Count == 0)
        {
            return;
        }

        var events = new List<ServerEvent>();
        foreach (var (request, kind) in changes)
        {
            events.Add(new ServerEvent
            {
                Id = $"seerr-{kind}.{request.Id}",
                Kind = $"seerr-{kind}",
                UserId = request.JellyfinUserId,
                Title = await TitleAsync(client, request, token).ConfigureAwait(false),
                RequestId = request.Id
            });
        }

        EventStore.Current.Add(events, DateTime.UtcNow);
    }

    private static async Task<string> TitleAsync(SeerrClient client, SeerrRequest request, CancellationToken token)
    {
        if (request.TmdbId is not { } tmdb || request.MediaType is not ("movie" or "tv"))
        {
            return string.Empty;
        }

        // The change is already recorded, so a title Seerr can't give now mustn't lose the event.
        try
        {
            using var response = await client.SendAsync(HttpMethod.Get, $"{request.MediaType}/{tmdb}", null, null, null, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty(request.MediaType == "movie" ? "title" : "name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception e) when (e is SeerrUnreachableException or JsonException or HttpRequestException)
        {
            return string.Empty;
        }
    }
}

public sealed record SeerrRequest(int Id, int Status, int MediaStatus, int? TmdbId, string? MediaType, Guid? JellyfinUserId);

public static class SeerrRequests
{
    public static List<SeerrRequest> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<SeerrRequest>();
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var r in results.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var id) || !id.TryGetInt32(out var requestId))
            {
                continue;
            }

            var media = r.TryGetProperty("media", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
            Guid? user = r.TryGetProperty("requestedBy", out var by) && by.ValueKind == JsonValueKind.Object
                         && by.TryGetProperty("jellyfinUserId", out var jf) && jf.ValueKind == JsonValueKind.String
                         && Guid.TryParse(jf.GetString(), out var parsed) ? parsed : null;
            list.Add(new SeerrRequest(requestId, Int(r, "status") ?? 0, Int(media, "status") ?? 0, Int(media, "tmdbId"),
                                      media.ValueKind == JsonValueKind.Object && media.TryGetProperty("mediaType", out var type) ? type.GetString() : null, user));
        }

        return list;
    }

    private static int? Int(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    /// <summary>
    /// What a request's new state is worth telling its requester, the same way Arcfin decides on
    /// a device: available beats approved, which beats declined. Null for nothing.
    /// </summary>
    public static string? Change(string? before, SeerrRequest now)
    {
        if (before is null)
        {
            return null;
        }

        var parts = before.Split('-');
        var oldStatus = parts.Length > 0 && int.TryParse(parts[0], out var s) ? s : 0;
        var oldMedia = parts.Length > 1 && int.TryParse(parts[1], out var mm) ? mm : 0;
        if (now.MediaStatus == 5 && oldMedia != 5)
        {
            return "available";
        }

        if (now.Status == 2 && oldStatus != 2)
        {
            return "approved";
        }

        return now.Status == 3 && oldStatus != 3 ? "declined" : null;
    }

    public static string State(SeerrRequest request) => $"{request.Status}-{request.MediaStatus}";
}

/// <summary>Each request's last seen state. The first check only records them, so nothing old is announced.</summary>
public sealed class SeerrStateFile
{
    private const int Limit = 2000;
    private static readonly object Gate = new();
    private readonly string _path;

    public SeerrStateFile(string folder)
    {
        _path = Path.Combine(folder, "seerr-states.json");
    }

    public List<(SeerrRequest Request, string Kind)> Update(IReadOnlyList<SeerrRequest> requests)
    {
        lock (Gate)
        {
            var saved = JsonFile.Read<Dictionary<string, string>>(_path);
            var states = saved ?? new Dictionary<string, string>();
            var changes = new List<(SeerrRequest, string)>();
            foreach (var request in requests)
            {
                var key = request.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                states.TryGetValue(key, out var before);
                if (saved is not null && request.JellyfinUserId is not null && SeerrRequests.Change(before, request) is { } kind)
                {
                    changes.Add((request, kind));
                }

                states[key] = SeerrRequests.State(request);
            }

            if (states.Count > Limit)
            {
                states = states.OrderByDescending(p => int.TryParse(p.Key, out var n) ? n : 0).Take(Limit).ToDictionary(p => p.Key, p => p.Value);
            }

            JsonFile.Write(_path, states);
            return changes;
        }
    }
}
