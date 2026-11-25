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
/// Looks at Seerr's newest requests and issues every ten minutes, or straight away when Seerr's
/// webhook says something changed, and records events for the people Seerr itself would tell:
/// approved, declined and available requests for whoever asked; new, automatically approved
/// and failed requests for the people who manage requests; and reported issues, comments,
/// resolved and reopened issues for whoever reported them and the people who manage issues.
/// A webhook names the request or issue and what happened; the check then reads it from Seerr
/// itself, so a webhook alone never makes an event.
/// </summary>
public sealed class SeerrWatchService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim WakeSignal = new(0, 1);

    /// <summary>Requests named by webhooks since the last check, with what Seerr said happened.</summary>
    private static readonly ConcurrentDictionary<int, string?> Named = new();

    /// <summary>Issues named by webhooks since the last check.</summary>
    private static readonly ConcurrentDictionary<int, bool> NamedIssues = new();
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SeerrWatchService> _logger;

    public SeerrWatchService(IHttpClientFactory http, ILogger<SeerrWatchService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Checks soon; several calls before the check runs make one check.</summary>
    public static void Wake(int? requestId = null, string? kind = null, int? issueId = null)
    {
        if (requestId is { } id)
        {
            Named[id] = kind;
        }

        if (issueId is { } issue)
        {
            NamedIssues[issue] = true;
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
        var events = new List<ServerEvent>();
        var permissions = await client.UserPermissionsAsync(token).ConfigureAwait(false);
        await CheckRequestsAsync(client, permissions, events, token).ConfigureAwait(false);
        await CheckIssuesAsync(client, permissions, events, token).ConfigureAwait(false);
        EventStore.Current.Add(events, DateTime.UtcNow);
    }

    private async Task CheckRequestsAsync(SeerrClient client, IReadOnlyDictionary<Guid, int> permissions, List<ServerEvent> events, CancellationToken token)
    {
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
            if (await ReadOneAsync(client, $"request/{id}", token).ConfigureAwait(false) is { } doc)
            {
                using (doc)
                {
                    if (SeerrRequests.ParseOne(doc.RootElement) is { } request)
                    {
                        requests.Add(request);
                    }
                }
            }
        }

        var changes = new SeerrStateFile(Plugin.Instance!.DataFolderPath)
            .Update(requests, named.Where(n => n.Kind is not null).ToDictionary(n => n.Id, n => n.Kind!));
        foreach (var (request, kind) in changes)
        {
            var people = SeerrRecipients.ForRequest(kind, request, permissions);
            if (people.Count == 0)
            {
                continue;
            }

            var title = await TitleAsync(client, request.TmdbId, request.MediaType, token).ConfigureAwait(false);
            events.AddRange(people.Select(person => new ServerEvent
            {
                Id = $"seerr-{kind}.{request.Id}", Kind = $"seerr-{kind}", UserId = person, Title = title, RequestId = request.Id
            }));
        }
    }

    private async Task CheckIssuesAsync(SeerrClient client, IReadOnlyDictionary<Guid, int> permissions, List<ServerEvent> events, CancellationToken token)
    {
        using var response = await client.SendAsync(HttpMethod.Get, "issue", "?take=50&skip=0&sort=modified&filter=all", null, null, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return;
        }

        var issues = SeerrIssues.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var named = NamedIssues.Keys.ToList().Where(id => NamedIssues.TryRemove(id, out _)).ToList();
        var states = new IssueStateFile(Plugin.Instance!.DataFolderPath);
        // Seerr's list leaves comments out; read each issue that changed (or a webhook named) in full.
        foreach (var id in states.NeedingComments(issues, named))
        {
            if (await ReadOneAsync(client, $"issue/{id}", token).ConfigureAwait(false) is not { } doc)
            {
                continue;
            }

            using (doc)
            {
                if (SeerrIssues.ParseOne(doc.RootElement) is { } full)
                {
                    issues.RemoveAll(i => i.Id == id);
                    issues.Add(full);
                }
            }
        }

        foreach (var change in states.Update(issues))
        {
            var people = SeerrRecipients.ForIssue(change, permissions);
            if (people.Count == 0)
            {
                continue;
            }

            var issue = change.Issue;
            var id = change.Kind switch
            {
                "comment" => $"issue-comment.{change.Comment!.Id}",
                "reported" => $"issue-reported.{issue.Id}",
                _ => $"issue-{change.Kind}.{issue.Id}.{change.Count}"
            };
            var actor = change.Kind switch
            {
                "reported" => issue.ReportedBy.Name,
                "comment" => change.Comment?.By.Name,
                _ => issue.ModifiedBy.Name
            };
            var body = change.Comment?.Message is { Length: > 0 } text ? (text.Length > 200 ? text[..200] + "…" : text) : null;
            var title = await TitleAsync(client, issue.TmdbId, issue.MediaType, token).ConfigureAwait(false);
            events.AddRange(people.Select(person => new ServerEvent
            {
                Id = id, Kind = $"issue-{change.Kind}", UserId = person, Title = title, Body = body, IssueId = issue.Id, IssueType = issue.IssueType,
                ProblemSeason = issue.ProblemSeason, ProblemEpisode = issue.ProblemEpisode, Actor = actor
            }));
        }
    }

    private static async Task<JsonDocument?> ReadOneAsync(SeerrClient client, string path, CancellationToken token)
    {
        try
        {
            using var response = await client.SendAsync(HttpMethod.Get, path, null, null, null, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> TitleAsync(SeerrClient client, int? tmdbId, string? mediaType, CancellationToken token)
    {
        if (tmdbId is not { } tmdb || mediaType is not ("movie" or "tv"))
        {
            return string.Empty;
        }

        // The change is already recorded, so a title Seerr can't give now mustn't lose the event.
        try
        {
            using var response = await client.SendAsync(HttpMethod.Get, $"{mediaType}/{tmdb}", null, null, null, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            return doc.RootElement.TryGetProperty(mediaType == "movie" ? "title" : "name", out var name) && name.ValueKind == JsonValueKind.String
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
        "MEDIA_PENDING" => "pending",
        "MEDIA_AUTO_APPROVED" => "auto-approved",
        "MEDIA_FAILED" => "failed",
        _ => null
    };

    /// <summary>Whether the request as Seerr has it now agrees with what a webhook said happened.</summary>
    public static bool Agrees(string kind, SeerrRequest now) => kind switch
    {
        "approved" => now.Status == 2,
        "declined" => now.Status == 3,
        "available" => now.MediaStatus == 5,
        "pending" => now.Status == 1,
        "auto-approved" => now.Status == 2,
        "failed" => now.Status == 4,
        _ => false
    };

    private static int? Int(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    /// <summary>
    /// What a request's new state is worth telling, the same way Arcfin decides on a device:
    /// available beats approved, which beats declined, then failed. A request seen for the first
    /// time is new: pending, or automatically approved. Null for nothing.
    /// </summary>
    public static string? Change(string? before, SeerrRequest now)
    {
        if (before is null)
        {
            return now.Status switch { 1 => "pending", 2 => "auto-approved", _ => null };
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

        if (now.Status == 3 && oldStatus != 3)
        {
            return "declined";
        }

        return now.Status == 4 && oldStatus != 4 ? "failed" : null;
    }

    public static string State(SeerrRequest request) => $"{request.Status}-{request.MediaStatus}";
}

/// <summary>
/// Each request's last seen state. The first check only records them, so nothing old is
/// announced. Who's told about a change is <see cref="SeerrRecipients.ForRequest"/>'s call.
/// </summary>
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
                if (kind is not null)
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
