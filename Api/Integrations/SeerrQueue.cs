using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Integrations;

/// <summary>
/// Requests made while Seerr couldn't be reached, sent again every few minutes for three days.
/// A request Seerr itself turns down (already requested, over quota, no permission) isn't kept:
/// only "couldn't reach it" and Seerr's own server errors are worth another try.
/// </summary>
public sealed class SeerrQueue
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(3);
    private const int Limit = 500;
    private static readonly object Gate = new();
    private readonly string _path;

    public SeerrQueue(string folder)
    {
        _path = Path.Combine(folder, "seerr-queue.json");
    }

    public static SeerrQueue Current => new(Plugin.Instance!.DataFolderPath);

    public List<QueuedRequest> All()
    {
        lock (Gate)
        {
            return JsonFile.Read<List<QueuedRequest>>(_path) ?? new List<QueuedRequest>();
        }
    }

    public QueuedRequest Add(Guid userId, byte[] body, DateTime now)
    {
        lock (Gate)
        {
            var list = All();
            var entry = new QueuedRequest { Id = Guid.NewGuid(), UserId = userId, Body = Convert.ToBase64String(body), Created = now };
            list.Add(entry);
            if (list.Count > Limit)
            {
                list.RemoveRange(0, list.Count - Limit);
            }

            JsonFile.Write(_path, list);
            return entry;
        }
    }

    public void Update(Func<List<QueuedRequest>, List<QueuedRequest>> change)
    {
        lock (Gate)
        {
            JsonFile.Write(_path, change(All()));
        }
    }
}

public sealed class QueuedRequest
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>The request's JSON body, base64.</summary>
    public string Body { get; set; } = string.Empty;

    public DateTime Created { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

public sealed class SeerrQueueService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(3);
    private readonly IHttpClientFactory _http;
    private readonly ILogger<SeerrQueueService> _logger;

    public SeerrQueueService(IHttpClientFactory http, ILogger<SeerrQueueService> logger)
    {
        _http = http;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
                await SendQueuedAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Couldn't send queued Seerr requests");
            }
        }
    }

    public async Task<int> SendQueuedAsync(CancellationToken token)
    {
        if (Plugin.Instance is null || !SeerrClient.IsConfigured)
        {
            return 0;
        }

        var queue = SeerrQueue.Current;
        var pending = queue.All();
        if (pending.Count == 0)
        {
            return 0;
        }

        var client = new SeerrClient(_http);
        var done = new HashSet<Guid>();
        var failed = new Dictionary<Guid, string>();
        foreach (var entry in pending)
        {
            if (DateTime.UtcNow - entry.Created > SeerrQueue.KeepFor)
            {
                done.Add(entry.Id);
                _logger.LogWarning("Gave up on a queued Seerr request from {User} after three days", entry.UserId);
                continue;
            }

            try
            {
                var seerrUser = await client.SeerrUserAsync(entry.UserId, token).ConfigureAwait(false);
                if (seerrUser is null)
                {
                    done.Add(entry.Id);
                    continue;
                }

                using var response = await client.SendAsync(HttpMethod.Post, "request", null, Convert.FromBase64String(entry.Body), seerrUser, token).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500)
                {
                    failed[entry.Id] = $"Seerr answered {(int)response.StatusCode}.";
                }
                else
                {
                    done.Add(entry.Id);
                    _logger.LogInformation("Sent a queued Seerr request from {User}: {Status}", entry.UserId, (int)response.StatusCode);
                }
            }
            catch (SeerrUnreachableException e)
            {
                failed[entry.Id] = e.InnerException?.Message ?? e.Message;
                break;
            }
        }

        queue.Update(list =>
        {
            foreach (var entry in list.Where(e => failed.ContainsKey(e.Id)))
            {
                entry.Attempts++;
                entry.LastError = failed[entry.Id];
            }

            return list.Where(e => !done.Contains(e.Id)).ToList();
        });
        return done.Count;
    }
}
