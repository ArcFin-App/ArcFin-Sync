using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ArcfinSync.Api;

public class StatusDto
{
    public bool Installed { get; set; }

    public string Version { get; set; } = string.Empty;

    public int ApiVersion { get; set; }

    public List<string> Features { get; set; } = new();
}

public class SyncedSettingDto
{
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }

    public bool IsLocked { get; set; }
}

public class SyncSettingDto : SyncedSettingDto
{
    public string Source { get; set; } = "user";
}

public class SyncStateDto
{
    public List<SyncSettingDto> Settings { get; set; } = new();

    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedByDeviceId { get; set; }

    public string? UpdatedByDeviceName { get; set; }
}

public class SyncPutBody
{
    public Dictionary<string, string?>? Values { get; set; }

    public bool Replace { get; set; }
}

public class CurrentDeviceBody
{
    public string? Name { get; set; }

    public bool RenameCurrent { get; set; }

    public string? Platform { get; set; }

    public string? Model { get; set; }

    public string? AppVersion { get; set; }

    public string? SyncMode { get; set; }

    public Dictionary<string, string?>? Settings { get; set; }
}

public class DeviceNameBody
{
    public string? Name { get; set; }
}

public class DeviceDto
{
    public string DeviceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? CustomName { get; set; }

    public string JellyfinDeviceName { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string? Model { get; set; }

    public string? AppVersion { get; set; }

    public string SyncMode { get; set; } = string.Empty;

    public DateTime LastSeen { get; set; }

    public bool IsCurrent { get; set; }

    public Dictionary<string, string?> Settings { get; set; } = new();

    public static DeviceDto From(DeviceRecord record, string? currentDeviceId) => new()
    {
        DeviceId = record.DeviceId,
        Name = record.DisplayName,
        CustomName = string.IsNullOrEmpty(record.CustomName) ? null : record.CustomName,
        JellyfinDeviceName = record.JellyfinDeviceName,
        Platform = record.Platform,
        Model = record.Model,
        AppVersion = record.AppVersion,
        SyncMode = record.SyncMode,
        LastSeen = record.LastSeen,
        IsCurrent = currentDeviceId is not null && string.Equals(record.DeviceId, currentDeviceId, StringComparison.Ordinal),
        Settings = record.Settings
    };

    public static List<DeviceDto> List(Dictionary<string, DeviceRecord> devices, string? currentDeviceId)
    {
        return devices.Values.OrderByDescending(d => d.LastSeen).Select(d => From(d, currentDeviceId)).ToList();
    }
}

public class AdminUserDto
{
    public Guid UserId { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsAdministrator { get; set; }

    public List<DeviceDto> Devices { get; set; } = new();

    public Dictionary<string, string?> Settings { get; set; } = new();
}
