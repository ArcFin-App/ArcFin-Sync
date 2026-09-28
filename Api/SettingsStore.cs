using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.ArcfinSync.Api;

public static class SettingsStore
{
    private static readonly object FileLock = new();

    public static List<SyncedSettingDto> GetMergedSettings(Guid userId, string? jellyfinUsername = null)
    {
        var userValues = LoadUserSettings(userId);
        var lockedSettings = Plugin.Instance!.Configuration.LockedSettings;

        var result = new List<SyncedSettingDto>();
        var seenKeys = new HashSet<string>();

        foreach (var setting in lockedSettings)
        {
            seenKeys.Add(setting.Key);
            var value = setting.IsLocked
                ? setting.PrefillValue
                : (userValues.TryGetValue(setting.Key, out var storedValue) ? storedValue : setting.PrefillValue);

            // Lowest-priority fallback: only fills a genuinely blank seerrUsername, never
            // overrides an admin lock/prefill or a value the user already chose themselves.
            if (setting.Key == "seerrUsername" && !setting.IsLocked && string.IsNullOrEmpty(value)
                && Plugin.Instance!.Configuration.SeerrUsernameFollowsJellyfin && !string.IsNullOrEmpty(jellyfinUsername))
            {
                value = jellyfinUsername;
            }

            result.Add(new SyncedSettingDto { Key = setting.Key, Value = value, IsLocked = setting.IsLocked });
        }

        foreach (var kvp in userValues)
        {
            if (seenKeys.Contains(kvp.Key))
            {
                continue;
            }

            result.Add(new SyncedSettingDto
            {
                Key = kvp.Key,
                Value = kvp.Value,
                IsLocked = false
            });
        }

        if (!seenKeys.Contains("seerrUsername") && Plugin.Instance!.Configuration.SeerrUsernameFollowsJellyfin
            && !string.IsNullOrEmpty(jellyfinUsername) && !userValues.ContainsKey("seerrUsername"))
        {
            result.Add(new SyncedSettingDto { Key = "seerrUsername", Value = jellyfinUsername, IsLocked = false });
        }

        return result;
    }

    public static void SaveUserSettings(Guid userId, Dictionary<string, string> values)
    {
        var lockedKeys = Plugin.Instance!.Configuration.LockedSettings
            .Where(s => s.IsLocked)
            .Select(s => s.Key)
            .ToHashSet();

        var existing = LoadUserSettings(userId);

        foreach (var kvp in values)
        {
            if (lockedKeys.Contains(kvp.Key))
            {
                continue;
            }

            existing[kvp.Key] = kvp.Value;
        }

        var path = GetUserFilePath(userId);
        lock (FileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(existing));
        }
    }

    private static Dictionary<string, string> LoadUserSettings(Guid userId)
    {
        var path = GetUserFilePath(userId);
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>();
        }

        lock (FileLock)
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
    }

    private static string GetUserFilePath(Guid userId)
    {
        return Path.Combine(Plugin.Instance!.DataFolderPath, "users", $"{userId}.json");
    }
}
