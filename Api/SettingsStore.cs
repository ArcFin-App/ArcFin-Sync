using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ArcfinSync.Api;

/// <summary>
/// Per-user files in the plugin's data folder: users/{userId}.json (stored values, the 0.3.x
/// format), users/{userId}.meta.json (who changed them last) and devices/{userId}.json.
/// File names only ever come from a user ID (a Guid), never from client input.
/// </summary>
public sealed class SettingsStore
{
    private static readonly object FileLock = new();

    private readonly string _root;

    public SettingsStore(string root)
    {
        _root = root;
    }

    public static SettingsStore Current => new(Plugin.Instance!.DataFolderPath);

    /// <summary>Runs a read-modify-write of one user's files under the store lock.</summary>
    public T WithUser<T>(Guid userId, Func<UserFiles, T> body)
    {
        RequireUser(userId);
        lock (FileLock)
        {
            var files = new UserFiles(this, userId);
            var result = body(files);
            files.Flush();
            return result;
        }
    }

    public Dictionary<string, string?> LoadValues(Guid userId)
    {
        RequireUser(userId);
        lock (FileLock)
        {
            return Read<Dictionary<string, string?>>(ValuesPath(userId)) ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        }
    }

    public Dictionary<string, DeviceRecord> LoadDevices(Guid userId)
    {
        RequireUser(userId);
        lock (FileLock)
        {
            return Read<Dictionary<string, DeviceRecord>>(DevicesPath(userId)) ?? new Dictionary<string, DeviceRecord>(StringComparer.Ordinal);
        }
    }

    public SyncMeta LoadMeta(Guid userId)
    {
        RequireUser(userId);
        lock (FileLock)
        {
            return Read<SyncMeta>(MetaPath(userId)) ?? new SyncMeta();
        }
    }

    /// <summary>How many device records there are across all users.</summary>
    public int CountDevices()
    {
        var dir = Path.Combine(_root, "devices");
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        lock (FileLock)
        {
            return Directory.EnumerateFiles(dir, "*.json")
                .Sum(f => Read<Dictionary<string, DeviceRecord>>(f)?.Count ?? 0);
        }
    }

    private static void RequireUser(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("No user.", nameof(userId));
        }
    }

    private string ValuesPath(Guid userId) => Path.Combine(_root, "users", $"{userId}.json");

    private string MetaPath(Guid userId) => Path.Combine(_root, "users", $"{userId}.meta.json");

    private string DevicesPath(Guid userId) => Path.Combine(_root, "devices", $"{userId}.json");

    private static T? Read<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
    }

    /// <summary>Writes beside the target, then moves over it, so a reader never sees half a file.</summary>
    private static void WriteAtomic(string path, object value)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    public sealed class UserFiles
    {
        private readonly SettingsStore _store;
        private readonly Guid _userId;
        private Dictionary<string, string?>? _values;
        private Dictionary<string, DeviceRecord>? _devices;
        private SyncMeta? _meta;
        private bool _valuesDirty;
        private bool _devicesDirty;
        private bool _metaDirty;

        internal UserFiles(SettingsStore store, Guid userId)
        {
            _store = store;
            _userId = userId;
        }

        public Dictionary<string, string?> Values => _values ??= Read<Dictionary<string, string?>>(_store.ValuesPath(_userId))
                                                                  ?? new Dictionary<string, string?>(StringComparer.Ordinal);

        public Dictionary<string, DeviceRecord> Devices => _devices ??= Read<Dictionary<string, DeviceRecord>>(_store.DevicesPath(_userId))
                                                                         ?? new Dictionary<string, DeviceRecord>(StringComparer.Ordinal);

        public SyncMeta Meta => _meta ??= Read<SyncMeta>(_store.MetaPath(_userId)) ?? new SyncMeta();

        public void ValuesChanged() => _valuesDirty = true;

        public void DevicesChanged() => _devicesDirty = true;

        public void MetaChanged() => _metaDirty = true;

        internal void Flush()
        {
            if (_valuesDirty)
            {
                WriteAtomic(_store.ValuesPath(_userId), Values);
            }

            if (_devicesDirty)
            {
                WriteAtomic(_store.DevicesPath(_userId), Devices);
            }

            if (_metaDirty)
            {
                WriteAtomic(_store.MetaPath(_userId), Meta);
            }
        }
    }
}

public sealed class DeviceRecord
{
    public string DeviceId { get; set; } = string.Empty;

    public string? CustomName { get; set; }

    public string JellyfinDeviceName { get; set; } = string.Empty;

    public string Platform { get; set; } = string.Empty;

    public string? Model { get; set; }

    public string? AppVersion { get; set; }

    public string SyncMode { get; set; } = "automatic";

    public DateTime FirstSeen { get; set; }

    public DateTime LastSeen { get; set; }

    public Dictionary<string, string?> Settings { get; set; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public string DisplayName => string.IsNullOrEmpty(CustomName) ? JellyfinDeviceName : CustomName;
}

public sealed class SyncMeta
{
    public DateTime? UpdatedAt { get; set; }

    public string? UpdatedByDeviceId { get; set; }

    public string? UpdatedByDeviceName { get; set; }
}
