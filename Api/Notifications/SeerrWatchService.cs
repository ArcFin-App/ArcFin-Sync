using System;
using System.Collections.Concurrent;
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
/// person who asked. A webhook names the request and what Seerr says happened; the check then
/// reads that request from Seerr itself, so a webhook alone never makes an event.
/// </summary>
public sealed class SeerrWatchService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim WakeSignal = new(0, 1);

    /// <summary>Requests named by webhooks since the last check, with what Seerr said happened.</summary>
    private static readonly ConcurrentDictionary<int, string?> Named = new();
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SeerrWatchService> _logger;

    public SeerrWatchService(IHttpClientFactory http, ILogger<SeerrWatchService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Checks soon; several calls before the check runs make one check.</summary>
    public static void Wake(int? requestId = null, string? kind = null)
    {
        if (requestId is { } id)
        {
            Named[id] = kind;
        }

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
        var named = Named.Keys.ToList().Select(id => (Id: id, Removed: Named.TryRemove(id, out var kind), Kind: kind)).ToList();
        foreach (var (id, _, _) in named.Where(n => requests.All(r => r.Id != n.Id)))
        {
            // Older than Seerr's newest hundred: read it on its own.
            using var one = await client.SendAsync(HttpMethod.Get, $"request/{id}", null, null, null, token).ConfigureAwait(false);
            if (one.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await one.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                if (SeerrRequests.ParseOne(doc.RootElement) is { } request)
                {
                    requests.Add(request);
                }
            }
        }

        var states = new SeerrStateFile(Plugin.Instance.DataFolderPath);
        var changes = states.Update(requests, named.Where(n => n.Kind is not null).ToDictionary(n => n.Id, n => n.Kind!));
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
            if (ParseOne(r) is { } request)
            {
                list.Add(request);
            }
        }

        return list;
    }

    public static SeerrRequest? ParseOne(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("id", out var id) || !id.TryGetInt32(out var requestId))
        {
            return null;
        }

        var media = r.TryGetProperty("media", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        Guid? user = r.TryGetProperty("requestedBy", out var by) && by.ValueKind == JsonValueKind.Object
                     && by.TryGetProperty("jellyfinUserId", out var jf) && jf.ValueKind == JsonValueKind.String
                     && Guid.TryParse(jf.GetString(), out var parsed) ? parsed : null;
        return new SeerrRequest(requestId, Int(r, "status") ?? 0, Int(media, "status") ?? 0, Int(media, "tmdbId"),
                                media.ValueKind == JsonValueKind.Object && media.TryGetProperty("mediaType", out var type) ? type.GetString() : null, user);
    }

    /// <summary>Seerr's webhook notification_type, as the change it names; null for any other.</summary>
    public static string? KindOf(string? notificationType) => notificationType switch
    {
        "MEDIA_APPROVED" => "approved",
        "MEDIA_DECLINED" => "declined",
        "MEDIA_AVAILABLE" => "available",
        _ => null
    };

    /// <summary>Whether the request as Seerr has it now agrees with what a webhook said happened.</summary>
    public static bool Agrees(string kind, SeerrRequest now) => kind switch
    {
        "approved" => now.Status == 2,
        "declined" => now.Status == 3,
        "available" => now.MediaStatus == 5,
        _ => false
    };

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

    /// <param name="named">
    /// What webhooks said happened, by request. For a request not seen before, that's the change,
    /// when Seerr's own copy agrees; for one seen before, the states decide as usual.
    /// </param>
    public List<(SeerrRequest Request, string Kind)> Update(IReadOnlyList<SeerrRequest> requests, IReadOnlyDictionary<int, string>? named = null)
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
                var kind = before is null && named?.GetValueOrDefault(request.Id) is { } hinted && SeerrRequests.Agrees(hinted, request)
                    ? hinted
                    : saved is not null ? SeerrRequests.Change(before, request) : null;
                if (request.JellyfinUserId is not null && kind is not null)
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
