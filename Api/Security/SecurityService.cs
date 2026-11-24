using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ArcfinSync.Api.Security;

/// <summary>
/// Once an hour: makes a new signing key when automatic rotation is due, and signs out Arcfin
/// devices that haven't picked up the current key for too long (and locks Arcfin on them, when
/// that's the rule). Both are off until an admin turns them on.
/// </summary>
public sealed class SecurityService : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);
    private readonly ISessionManager _sessions;
    private readonly IDeviceManager _devices;
    private readonly IServerApplicationHost _host;
    private readonly ILogger<SecurityService> _logger;

    public SecurityService(ISessionManager sessions, IDeviceManager devices, IServerApplicationHost host, ILogger<SecurityService> logger)
    {
        _sessions = sessions;
        _devices = devices;
        _host = host;
        _logger = logger;
    }

    /// <summary>When the next automatic rotation is due, or null when it's off or the admin's own key is in use.</summary>
    public static DateTime? NextRotation(SecuritySettings settings, PublicKeyInfo? key)
    {
        if (!settings.AutoRotate || key is null || key.Imported)
        {
            return null;
        }

        return key.Created + TimeSpan.FromDays(Math.Max(1, settings.RotateEveryDays));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);
                await RunOnceAsync().ConfigureAwait(false);
                await Task.Delay(Every, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Arcfin Sync security check failed");
            }
        }
    }

    /// <summary>Arcfin devices signed in that have never checked in: too old for these features.</summary>
    public static System.Collections.Generic.List<MediaBrowser.Model.Devices.DeviceInfo> OlderArcfinDevices(IDeviceManager devices, DeviceKeyStore store)
    {
        var checkedIn = store.All().Select(r => (r.UserId, r.DeviceId)).ToHashSet();
        return devices.GetDeviceInfos(new DeviceQuery()).Items
            .Where(d => string.Equals(d.AppName, OlderApps.ArcfinAppName, StringComparison.OrdinalIgnoreCase)
                        && d.Id is not null && d.LastUserId is { } user && !checkedIn.Contains((user, d.Id)))
            .ToList();
    }

    private async Task SignOutOlderAppsAsync(SecuritySettings settings, DeviceKeyStore store, CommandSigner signer, DateTime now)
    {
        var older = OlderAppStore.Current;
        foreach (var device in OlderArcfinDevices(_devices, store))
        {
            var user = device.LastUserId!.Value;
            var locked = signer.IsLocked(user, now);
            var firstSeen = older.FirstSeen(user, device.Id!, now);
            // Someone locked is signed out of an older Arcfin whether or not the rule is on: it can't show the lock.
            if ((settings.OlderApps != OlderAppActions.SignOut && !locked) || !OlderApps.ShouldSignOut(firstSeen, settings.OlderAppsSince, locked, now))
            {
                continue;
            }

            if (device.AccessToken is { } token)
            {
                await _sessions.Logout(token).ConfigureAwait(false);
                older.Forget(user, device.Id!);
                _logger.LogInformation("Arcfin Sync signed out {Device} of user {User}: Arcfin {Version} is too old for its security features{Locked}",
                                       device.Name, device.LastUserId, device.AppVersion, locked ? " and the user is locked" : string.Empty);
            }
        }
    }

    public async Task RunOnceAsync()
    {
        var settings = Plugin.Instance?.Configuration.Security;
        if (settings is null || !settings.Enabled)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var signer = CommandSigner.Current;
        if (NextRotation(settings, signer.PublicKey) is { } due && now >= due)
        {
            var key = signer.Generate();
            _logger.LogInformation("Arcfin Sync rotated its signing key; the new fingerprint is {Fingerprint}", key.Fingerprint);
        }

        var store = DeviceKeyStore.Current;
        await SignOutOlderAppsAsync(settings, store, signer, now).ConfigureAwait(false);
        // A wiped device that never came back online is signed out after a week anyway.
        foreach (var wipe in TrustStore.Current.Load().Wipes.Where(w => now - w.Sent > TimeSpan.FromDays(7)).ToList())
        {
            var lost = _devices.GetDevices(new DeviceQuery { UserId = wipe.UserId, DeviceId = wipe.DeviceId }).Items.FirstOrDefault();
            if (lost?.AccessToken is { } lostToken)
            {
                await _sessions.Logout(lostToken).ConfigureAwait(false);
            }

            TrustStore.Current.Change(file => file.Wipes.RemoveAll(w => w.UserId == wipe.UserId && w.DeviceId == wipe.DeviceId));
        }

        foreach (var record in store.All())
        {
            var rule = StalePolicy.For(settings, record.UserId);
            if (!StalePolicy.IsStale(record, rule, settings.StaleSince, now))
            {
                continue;
            }

            var device = _devices.GetDevices(new DeviceQuery { UserId = record.UserId, DeviceId = record.DeviceId }).Items.FirstOrDefault();
            if (device?.AccessToken is { } token)
            {
                await _sessions.Logout(token).ConfigureAwait(false);
            }

            if (rule.Action == StaleActions.Lock)
            {
                signer.Issue(CommandKind.Lock, record.UserId, _host.SystemId, now, record.DeviceId);
            }

            store.MarkSignedOut(record.UserId, record.DeviceId, rule.Action, now);
            _logger.LogInformation("Arcfin Sync signed out device {Device} of user {User}: it hadn't picked up the current key in {Days} days ({Action})",
                                   record.DeviceName, record.UserId, rule.Days, rule.Action);
        }
    }
}
