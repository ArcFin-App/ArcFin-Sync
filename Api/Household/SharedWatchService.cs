using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Household;

/// <summary>
/// What plays on a shared account (the one on the living-room TV) is copied to the people who
/// said they're watching: their position as it goes, and watched once it's finished. Only
/// titles each of them can see.
/// </summary>
public sealed class SharedWatchService : IHostedService
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(60);
    private readonly ISessionManager _sessions;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly ILogger<SharedWatchService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _lastCopy = new();

    public SharedWatchService(ISessionManager sessions, IUserManager users, IUserDataManager userData, ILogger<SharedWatchService> logger)
    {
        _sessions = sessions;
        _users = users;
        _userData = userData;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackProgress += OnProgress;
        _sessions.PlaybackStopped += OnStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackProgress -= OnProgress;
        _sessions.PlaybackStopped -= OnStopped;
        return Task.CompletedTask;
    }

    private void OnProgress(object? sender, PlaybackProgressEventArgs e)
    {
        var key = e.Session?.Id + ":" + e.Item?.Id;
        var now = DateTime.UtcNow;
        if (_lastCopy.TryGetValue(key, out var last) && now - last < Every)
        {
            return;
        }

        _lastCopy[key] = now;
        Copy(e.Users?.FirstOrDefault()?.Id, e.DeviceId, e.Item, e.PlaybackPositionTicks, finished: false);
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        _lastCopy.TryRemove(e.Session?.Id + ":" + e.Item?.Id, out _);
        Copy(e.Users?.FirstOrDefault()?.Id, e.DeviceId, e.Item, e.PlaybackPositionTicks, e.PlayedToCompletion);
    }

    private void Copy(Guid? sharedUser, string? deviceId, BaseItem? item, long? position, bool finished)
    {
        try
        {
            if (sharedUser is not { } shared || deviceId is null || item is null
                || Plugin.Instance?.Configuration.SharedAccounts.Any(a => a.UserId == shared) != true)
            {
                return;
            }

            foreach (var memberId in SharedWatchStore.Get(shared, deviceId, DateTime.UtcNow))
            {
                var member = _users.GetUserById(memberId);
                if (member is null || !item.IsVisible(member))
                {
                    continue;
                }

                var data = _userData.GetUserData(member, item);
                if (data is null)
                {
                    continue;
                }

                if (finished)
                {
                    data.Played = true;
                    data.PlayCount++;
                    data.PlaybackPositionTicks = 0;
                }
                else if (position is { } ticks && ticks > 0)
                {
                    data.PlaybackPositionTicks = ticks;
                }
                else
                {
                    continue;
                }

                data.LastPlayedDate = DateTime.UtcNow;
                _userData.SaveUserData(member, item, data, finished ? UserDataSaveReason.PlaybackFinished : UserDataSaveReason.PlaybackProgress, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't copy what was watched on a shared account");
        }
    }
}
