using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Notifications;

/// <summary>
/// Tells Arcfin apps that are open now that there's news for them, over the connection they
/// already keep to Jellyfin, so they read the feed at once instead of at their next check. Only
/// Arcfin's own sessions get it: the message is a display command with a header Arcfin treats as
/// "read the feed" and never shows, which other apps would show as text.
/// </summary>
public sealed class LiveNudgeService : IHostedService
{
    public const string Header = "ArcfinSync.Events";
    private const string ArcfinClient = "Arcfin";
    private readonly ISessionManager _sessions;
    private readonly ILogger<LiveNudgeService> _logger;

    public LiveNudgeService(ISessionManager sessions, ILogger<LiveNudgeService> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EventStore.Added += OnAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        EventStore.Added -= OnAdded;
        return Task.CompletedTask;
    }

    private void OnAdded(IReadOnlyList<Guid?> users) => _ = NudgeAsync(users);

    private async Task NudgeAsync(IReadOnlyList<Guid?> users)
    {
        var everyone = users.Any(u => u is null);
        var wanted = users.Where(u => u is not null).Select(u => u!.Value).ToHashSet();
        foreach (var session in Targets(_sessions.Sessions, everyone, wanted))
        {
            try
            {
                var command = new GeneralCommand { Name = GeneralCommandType.DisplayMessage };
                command.Arguments["Header"] = Header;
                command.Arguments["Text"] = string.Empty;
                await _sessions.SendGeneralCommand(null, session.Id, command, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "Couldn't tell an open Arcfin about new events");
            }
        }
    }

    /// <summary>Open Arcfin sessions of the people the news is for.</summary>
    public static IEnumerable<SessionInfo> Targets(IEnumerable<SessionInfo> sessions, bool everyone, IReadOnlySet<Guid> users)
        => sessions.Where(s => s.IsActive && string.Equals(s.Client, ArcfinClient, StringComparison.Ordinal)
                               && (everyone || users.Contains(s.UserId)));
}
