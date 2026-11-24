using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Jellyfin.Plugin.ArcfinSync.Api.Storage;
using Jellyfin.Plugin.ArcfinSync.Configuration;

namespace Jellyfin.Plugin.ArcfinSync.Api.Security;

/// <summary>
/// Trusted Arcfin devices, devices waiting to be trusted, and wipes not yet carried out. A new
/// device waits, locked, until one of the person's trusted devices (or an admin) approves it.
/// </summary>
public sealed class TrustStore
{
    private static readonly object Gate = new();
    private readonly string _path;

    public TrustStore(string folder)
    {
        _path = Path.Combine(folder, "trust.json");
    }

    public static TrustStore Current => new(Plugin.Instance!.DataFolderPath);

    public TrustFile Load()
    {
        lock (Gate)
        {
            return JsonFile.Read<TrustFile>(_path) ?? new TrustFile();
        }
    }

    public T Change<T>(Func<TrustFile, T> change)
    {
        lock (Gate)
        {
            var file = JsonFile.Read<TrustFile>(_path) ?? new TrustFile();
            var result = change(file);
            JsonFile.Write(_path, file);
            return result;
        }
    }

    public bool IsTrusted(Guid userId, string deviceId) => Load().Trusted.Any(t => t.UserId == userId && t.DeviceId == deviceId);

    /// <summary>Whether new devices need approving for this person, with their override applied.</summary>
    public static bool Required(SecuritySettings settings, Guid userId)
    {
        if (!settings.Enabled)
        {
            return false;
        }

        var own = settings.Users.FirstOrDefault(u => u.UserId == userId)?.TrustedDevices;
        return own switch
        {
            "on" => true,
            "off" => false,
            _ => settings.RequireTrustedDevices
        };
    }

    /// <summary>The request for this device, made now if there isn't one, with a fresh six-digit code.</summary>
    public static TrustRequest RequestFor(TrustFile file, Guid userId, string deviceId, string deviceName, DateTime now)
    {
        var request = file.Requests.FirstOrDefault(r => r.UserId == userId && r.DeviceId == deviceId);
        if (request is null)
        {
            request = new TrustRequest
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                DeviceId = deviceId,
                DeviceName = deviceName,
                Code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                Created = now
            };
            file.Requests.Add(request);
        }

        return request;
    }
}

public sealed class TrustFile
{
    public List<TrustedDevice> Trusted { get; set; } = new();

    public List<TrustRequest> Requests { get; set; } = new();

    public List<PendingWipe> Wipes { get; set; } = new();
}

public sealed class TrustedDevice
{
    public Guid UserId { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public DateTime Trusted { get; set; }

    /// <summary>automatic (there when it was turned on, or the person's first), a device's name, or admin.</summary>
    public string By { get; set; } = string.Empty;
}

public sealed class TrustRequest
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Shown on the waiting device and on the trusted one, to check they're the same.</summary>
    public string Code { get; set; } = string.Empty;

    public DateTime Created { get; set; }
}

public sealed class PendingWipe
{
    public Guid UserId { get; set; }

    public string DeviceId { get; set; } = string.Empty;

    public DateTime Sent { get; set; }
}

/// <summary>The rules sent to each person's devices, signed so nothing on the network can loosen them.</summary>
public static class UserPolicy
{
    public static Dictionary<string, string> For(SecuritySettings settings, Guid userId)
    {
        var pins = settings.Pins;
        var required = pins.RequiredFor == "everyone" || (pins.RequiredFor == "chosen" && pins.Users.Contains(userId));
        return new Dictionary<string, string>
        {
            ["pinRequired"] = required ? "true" : "false",
            ["pinMinLength"] = Math.Clamp(pins.MinLength, 4, PinRules.LongestPin).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["pinMaxLength"] = PinRules.LongestPin.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["pinMaxTries"] = pins.MaxTries is { } tries ? tries.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
            ["requireHttps"] = settings.RequireHttpsAway ? "true" : "false"
        };
    }

    public static bool Same(Dictionary<string, string>? a, Dictionary<string, string> b)
    {
        return a is not null && a.Count == b.Count && b.All(kv => a.TryGetValue(kv.Key, out var v) && v == kv.Value);
    }
}

public static class TrustAnswer
{
    /// <summary>Trusts the device (and lifts its waiting lock), or signs it out and keeps it locked.</summary>
    public static async System.Threading.Tasks.Task ApplyAsync(TrustRequest request, bool trust, string by, string serverId,
                                                               MediaBrowser.Controller.Session.ISessionManager sessions,
                                                               MediaBrowser.Controller.Devices.IDeviceManager devices)
    {
        var now = DateTime.UtcNow;
        TrustStore.Current.Change(file =>
        {
            file.Requests.RemoveAll(r => r.Id == request.Id);
            if (trust && !file.Trusted.Any(t => t.UserId == request.UserId && t.DeviceId == request.DeviceId))
            {
                file.Trusted.Add(new TrustedDevice { UserId = request.UserId, DeviceId = request.DeviceId, Trusted = now, By = by });
            }

            return 0;
        });

        if (trust)
        {
            CommandSigner.Current.Issue(CommandKind.Unlock, request.UserId, serverId, now, request.DeviceId, new Dictionary<string, string> { ["reason"] = "trust" });
            return;
        }

        var device = devices.GetDevices(new Jellyfin.Data.Queries.DeviceQuery { UserId = request.UserId, DeviceId = request.DeviceId }).Items.FirstOrDefault();
        if (device?.AccessToken is { } token)
        {
            await sessions.Logout(token).ConfigureAwait(false);
        }
    }
}
