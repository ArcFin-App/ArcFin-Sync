using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;
using Jellyfin.Plugin.ArcfinSync.Configuration;

namespace Jellyfin.Plugin.ArcfinSync.Api.Security;

/// <summary>
/// Which signing key each Arcfin device last confirmed it holds, and when, so devices that stop
/// keeping up can be signed out. Only devices that have checked in at least once are here;
/// other apps and Arcfin devices that never did are left alone.
/// </summary>
public sealed class DeviceKeyStore
{
    private static readonly object Gate = new();
    private readonly string _path;

    public DeviceKeyStore(string folder)
    {
        _path = Path.Combine(folder, "device-keys.json");
    }

    public static DeviceKeyStore Current => new(Plugin.Instance!.DataFolderPath);

    public List<DeviceKeyRecord> All()
    {
        lock (Gate)
        {
            return Read();
        }
    }

    /// <summary>A device confirmed it holds <paramref name="fingerprint"/>; holding the current key counts as up to date.</summary>
    public DeviceKeyRecord CheckIn(Guid userId, string deviceId, string deviceName, string fingerprint, bool isCurrent, DateTime now,
                                   string? appVersion = null, string? build = null)
    {
        return Change(list =>
        {
            var record = list.FirstOrDefault(r => r.UserId == userId && r.DeviceId == deviceId);
            if (record is null)
            {
                record = new DeviceKeyRecord { UserId = userId, DeviceId = deviceId };
                list.Add(record);
            }

            record.DeviceName = deviceName;
            record.Fingerprint = fingerprint;
            record.AppVersion = appVersion ?? record.AppVersion;
            record.Build = build ?? record.Build;
            record.LastCheckIn = now;
            if (isCurrent)
            {
                record.LastCurrent = now;
                record.SignedOut = null;
                record.MismatchAt = null;
                record.MismatchFingerprint = null;
            }

            return record;
        });
    }

    public void ReportMismatch(Guid userId, string deviceId, string deviceName, string seenFingerprint, DateTime now)
    {
        Change(list =>
        {
            var record = list.FirstOrDefault(r => r.UserId == userId && r.DeviceId == deviceId);
            if (record is null)
            {
                record = new DeviceKeyRecord { UserId = userId, DeviceId = deviceId, DeviceName = deviceName, LastCheckIn = now, Fingerprint = "unverified" };
                list.Add(record);
            }

            record.MismatchAt = now;
            record.MismatchFingerprint = seenFingerprint;
            return 0;
        });
    }

    public void MarkSignedOut(Guid userId, string deviceId, string action, DateTime now)
    {
        Change(list =>
        {
            if (list.FirstOrDefault(r => r.UserId == userId && r.DeviceId == deviceId) is { } record)
            {
                record.SignedOut = now;
                record.Action = action;
            }

            return 0;
        });
    }

    public void Forget(Guid userId, string deviceId)
    {
        Change(list => list.RemoveAll(r => r.UserId == userId && r.DeviceId == deviceId));
    }

    private T Change<T>(Func<List<DeviceKeyRecord>, T> change)
    {
        lock (Gate)
        {
            var list = Read();
            var result = change(list);
            JsonFile.Write(_path, list);
            return result;
        }
    }

    private List<DeviceKeyRecord> Read() => JsonFile.Read<List<DeviceKeyRecord>>(_path) ?? new List<DeviceKeyRecord>();
}

public sealed class DeviceKeyRecord
{
    public Guid UserId { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    public string Fingerprint { get; set; } = string.Empty;

    public DateTime LastCheckIn { get; set; }

    /// <summary>The last time it checked in holding the key that was current then.</summary>
    public DateTime? LastCurrent { get; set; }

    /// <summary>When the stale-device rule signed it out, until it checks in with the current key again.</summary>
    public DateTime? SignedOut { get; set; }

    /// <summary>What was done: signOut or lock.</summary>
    public string? Action { get; set; }

    public string? AppVersion { get; set; }

    public string? Build { get; set; }

    /// <summary>When someone on the device said the key it got didn't match, and that key's fingerprint.</summary>
    public DateTime? MismatchAt { get; set; }

    public string? MismatchFingerprint { get; set; }
}

/// <summary>
/// Arcfin devices seen signed in that have never checked in with a key: versions from before
/// these security features. When each was first seen, so they get a day before the rule for
/// older apps signs them out.
/// </summary>
public sealed class OlderAppStore
{
    private static readonly object Gate = new();
    private readonly string _path;

    public OlderAppStore(string folder)
    {
        _path = Path.Combine(folder, "older-apps.json");
    }

    public static OlderAppStore Current => new(Plugin.Instance!.DataFolderPath);

    /// <summary>When this device was first seen without checking in, recording now if it's new.</summary>
    public DateTime FirstSeen(Guid userId, string deviceId, DateTime now)
    {
        lock (Gate)
        {
            var all = JsonFile.Read<Dictionary<string, DateTime>>(_path) ?? new Dictionary<string, DateTime>();
            var key = $"{userId:N}:{deviceId}";
            if (!all.TryGetValue(key, out var seen))
            {
                seen = now;
                all[key] = now;
                JsonFile.Write(_path, all);
            }

            return seen;
        }
    }

    public void Forget(Guid userId, string deviceId)
    {
        lock (Gate)
        {
            var all = JsonFile.Read<Dictionary<string, DateTime>>(_path) ?? new Dictionary<string, DateTime>();
            if (all.Remove($"{userId:N}:{deviceId}"))
            {
                JsonFile.Write(_path, all);
            }
        }
    }
}

public static class OlderApps
{
    public const string ArcfinAppName = "Arcfin";
    public static readonly TimeSpan Grace = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether to sign out an Arcfin device that has never checked in: a day after it was first
    /// seen (or after the rule was turned on, if later), or at once for someone who's locked,
    /// since an older Arcfin can't show the lock.
    /// </summary>
    public static bool ShouldSignOut(DateTime firstSeen, DateTime? ruleSince, bool userLocked, DateTime now)
    {
        if (userLocked)
        {
            return true;
        }

        var from = ruleSince is { } since && since > firstSeen ? since : firstSeen;
        return now - from > Grace;
    }
}

/// <summary>The stale-device rule for one user, with their override applied.</summary>
public sealed record StaleRule(string Action, int Days);

public static class StalePolicy
{
    public static StaleRule For(SecuritySettings settings, Guid userId)
    {
        var own = settings.Users.FirstOrDefault(u => u.UserId == userId);
        var action = own is null || own.StaleAction == StaleActions.Inherit ? settings.StaleAction : own.StaleAction;
        return new StaleRule(action, own?.StaleAfterDays ?? settings.StaleAfterDays);
    }

    /// <summary>
    /// Whether a device has gone too long without holding the current key. Counted from its last
    /// up-to-date check-in, or from when the rule was turned on if that's later, so turning it on
    /// doesn't sign everyone out at once. A device already signed out isn't counted again.
    /// </summary>
    public static bool IsStale(DeviceKeyRecord record, StaleRule rule, DateTime? since, DateTime now)
    {
        if (rule.Action == StaleActions.Off || rule.Days < 1 || record.SignedOut is not null)
        {
            return false;
        }

        var from = new[] { record.LastCurrent ?? record.LastCheckIn, since ?? DateTime.MinValue }.Max();
        return now - from > TimeSpan.FromDays(rule.Days);
    }
}
