using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Limits;

/// <summary>
/// Counts watch time from Jellyfin's own playback reports and stops playback that's past a
/// user's limit or inside their bedtime, with a message, on any client. The apps also ask
/// before they play, but this is what holds when they don't.
/// </summary>
public sealed class WatchLimitService : IHostedService
{
    /// <summary>A gap longer than this between reports isn't counted: the player was likely asleep.</summary>
    private static readonly TimeSpan LongestGap = TimeSpan.FromSeconds(90);

    private readonly ISessionManager _sessions;
    private readonly ILogger<WatchLimitService> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _lastReport = new();
    private readonly ConcurrentDictionary<string, DateTime> _stopped = new();

    /// <summary>Warnings already given, by user and day: "10" and "5" minutes left.</summary>
    private readonly ConcurrentDictionary<string, HashSet<int>> _warned = new();

    public WatchLimitService(ISessionManager sessions, ILogger<WatchLimitService> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnPlayback;
        _sessions.PlaybackProgress += OnPlayback;
        _sessions.PlaybackStopped += OnStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnPlayback;
        _sessions.PlaybackProgress -= OnPlayback;
        _sessions.PlaybackStopped -= OnStopped;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The time used for bedtimes and the daily count: the person's own time zone, else the one
    /// set for everyone, else the server's.
    /// </summary>
    public static DateTime LocalNow(WatchLimitRule? rule = null)
    {
        foreach (var zone in new[] { rule?.TimeZone, Plugin.Instance?.Configuration.Limits.TimeZone })
        {
            if (Zone(zone) is { } found)
            {
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, found);
            }
        }

        return DateTime.Now;
    }

    public static TimeZoneInfo? Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }

    public static WatchLimitRule? RuleFor(Guid userId)
    {
        return Plugin.Instance?.Configuration.Limits.Rules.FirstOrDefault(r => r.UserId == userId);
    }

    public static LimitState StateFor(Guid userId)
    {
        var rule = RuleFor(userId);
        var now = LocalNow(rule);
        return WatchLimits.Evaluate(rule, UsageStore.Current.Used(userId, now.Date), now);
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session?.Id is { } id)
        {
            _lastReport.TryRemove(id, out _);
        }
    }

    private void OnPlayback(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var session = e.Session;
            var user = e.Users?.FirstOrDefault();
            if (session is null || user is null || RuleFor(user.Id) is not { } rule)
            {
                return;
            }

            var utc = DateTime.UtcNow;
            var now = LocalNow(rule);
            var minutes = 0.0;
            if (_lastReport.TryGetValue(session.Id, out var last) && !e.IsPaused && utc - last <= LongestGap)
            {
                minutes = (utc - last).TotalMinutes;
            }

            _lastReport[session.Id] = utc;
            var used = minutes > 0 ? UsageStore.Current.Add(user.Id, now.Date, minutes) : UsageStore.Current.Used(user.Id, now.Date);
            var state = WatchLimits.Evaluate(rule, used, now);
            if (!state.Allowed)
            {
                _ = StopAsync(session.Id, state);
            }
            else if (Plugin.Instance?.Configuration.Limits.WarnBeforeEnd == true)
            {
                var given = _warned.GetOrAdd($"{user.Id:N}:{now:yyyyMMdd}", _ => new HashSet<int>());
                int? due;
                lock (given)
                {
                    due = WatchLimits.WarningDue(state, given);
                    if (due is { } at)
                    {
                        // A bigger threshold passed at the same time isn't said as well.
                        given.UnionWith(new[] { 5, 10 }.Where(t => t >= at));
                    }
                }

                if (due is { } warnAt)
                {
                    var bedtime = WatchLimits.MinutesUntilBedtime(rule, now) is { } bed && bed <= (state.MinutesLeft ?? int.MaxValue);
                    var text = bedtime ? $"Bedtime in {warnAt} minutes." : $"{warnAt} minutes of watch time left today.";
                    _ = _sessions.SendMessageCommand(null, session.Id, new MessageCommand { Header = "Watch time", Text = text, TimeoutMs = 8000 }, CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't check a watch-time limit");
        }
    }

    private async Task StopAsync(string sessionId, LimitState state)
    {
        // One stop per session a minute, however often the player reports.
        var utc = DateTime.UtcNow;
        if (_stopped.TryGetValue(sessionId, out var at) && utc - at < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _stopped[sessionId] = utc;
        var text = state.Reason == WatchLimits.Bedtime
            ? "It's bedtime. Watching is off until " + state.Until?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "."
            : "That's today's watch time used up.";
        try
        {
            await _sessions.SendMessageCommand(null, sessionId, new MessageCommand { Header = "Watch time", Text = text, TimeoutMs = 10000 }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Couldn't show the watch-time message");
        }

        await _sessions.SendPlaystateCommand(null, sessionId, new PlaystateRequest { Command = PlaystateCommand.Stop }, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Stopped playback on session {Session}: {Reason}", sessionId, state.Reason);
    }
}
