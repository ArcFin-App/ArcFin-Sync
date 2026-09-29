using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ArcfinSync.Configuration;

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
        LockedSetting? authMethodSetting = null;

        foreach (var setting in lockedSettings)
        {
            // seerrUsername is never taken from admin config directly, see below — a single
            // locked value would apply the same literal username to every user, which makes
            // no sense (unlike seerrURL, which genuinely is one shared value for everyone).
            if (setting.Key == "seerrUsername")
            {
                continue;
            }

            if (setting.Key == "seerrAuthMethod")
            {
                authMethodSetting = setting;
            }

            seenKeys.Add(setting.Key);
            var value = setting.IsLocked
                ? setting.PrefillValue
                : (userValues.TryGetValue(setting.Key, out var storedValue) ? storedValue : setting.PrefillValue);
            result.Add(new SyncedSettingDto { Key = setting.Key, Value = value, IsLocked = setting.IsLocked });
        }

        // seerrUsername's lock state is derived from Seerr Account Type instead: locked to
        // "jellyfin" means every user's Seerr username IS their Jellyfin username, no admin
        // choice involved, so it's forced. Locked to "local" (or not locked at all) means
        // each user has their own separate Seerr account, so it stays per-user editable.
        seenKeys.Add("seerrUsername");
        if (IsLockedToJellyfinAccount(authMethodSetting))
        {
            result.Add(new SyncedSettingDto { Key = "seerrUsername", Value = jellyfinUsername, IsLocked = true });
        }
        else
        {
            var storedUsername = userValues.TryGetValue("seerrUsername", out var su) ? su : null;
            result.Add(new SyncedSettingDto { Key = "seerrUsername", Value = storedUsername, IsLocked = false });
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

        return result;
    }

    public static void SaveUserSettings(Guid userId, Dictionary<string, string> values)
    {
        var lockedKeys = Plugin.Instance!.Configuration.LockedSettings
            .Where(s => s.IsLocked)
            .Select(s => s.Key)
            .ToHashSet();

        var authMethodSetting = Plugin.Instance!.Configuration.LockedSettings
            .FirstOrDefault(s => s.Key == "seerrAuthMethod");
        if (IsLockedToJellyfinAccount(authMethodSetting))
        {
            lockedKeys.Add("seerrUsername");
        }

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

    private static bool IsLockedToJellyfinAccount(LockedSetting? authMethodSetting)
    {
        return authMethodSetting is { IsLocked: true, PrefillValue: "jellyfin" };
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
