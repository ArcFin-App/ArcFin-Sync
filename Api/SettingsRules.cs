using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Configuration;

namespace Jellyfin.Plugin.ArcfinSync.Api;

public sealed record EffectiveRule(string Key, string? Value, bool Locked);

/// <summary>
/// How stored values, admin defaults and locks combine. No I/O here, so it can be tested alone.
/// </summary>
public static class SettingsRules
{
    public const int MaxKeys = 512;
    public const int MaxKeyLength = 128;
    public const int MaxValueLength = 4096;
    public const int MaxNameLength = 64;
    public const int MaxDevicesPerUser = 50;
    public const int MaxDeviceIdLength = 256;
    public const int MaxFieldLength = 128;
    public const int MaxStoredKeys = 1024;

    public const string SeerrUsername = "seerrUsername";
    public const string SeerrAuthMethod = "seerrAuthMethod";

    public static readonly string[] Platforms = { "iOS", "iPadOS", "macOS", "tvOS" };
    public static readonly string[] SyncModes = { "automatic", "ask", "off" };

    /// <summary>Size limits for a key/value payload; null when it's within them.</summary>
    public static string? CheckPayload(IReadOnlyDictionary<string, string?>? values)
    {
        if (values is null)
        {
            return null;
        }

        if (values.Count > MaxKeys)
        {
            return $"At most {MaxKeys} settings per request.";
        }

        foreach (var (key, value) in values)
        {
            if (key.Length == 0 || key.Length > MaxKeyLength)
            {
                return "A setting key is empty or too long.";
            }

            if (value is not null && value.Length > MaxValueLength)
            {
                return $"The value of {key} is longer than {MaxValueLength} characters.";
            }
        }

        return null;
    }

    /// <summary>
    /// Keeps the schema keys of the given scope (any scope when null) whose values fit their type.
    /// Anything else is dropped rather than refused: the app and the plugin can be a version apart.
    /// </summary>
    public static Dictionary<string, string?> FilterToSchema(SettingsSchema schema, IReadOnlyDictionary<string, string?>? values, string? scope,
                                                             bool keepUnknown = false)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (values is null)
        {
            return result;
        }

        foreach (var (key, value) in values)
        {
            var setting = schema.Find(key);
            if (setting is null)
            {
                if (keepUnknown)
                {
                    result[key] = value;
                }

                continue;
            }

            if (scope is not null && setting.Scope != scope)
            {
                continue;
            }

            if (value is not null && !SettingsSchema.IsValidValue(setting, value))
            {
                continue;
            }

            result[key] = value;
        }

        return result;
    }

    /// <summary>
    /// The global rules with this user's overrides applied, in the global list's order and then
    /// the user's extra keys. An override replaces the lock outright (so it can also unlock);
    /// its default falls back to the global one.
    /// </summary>
    public static List<EffectiveRule> RulesForUser(SettingsPolicy policy, Guid userId)
    {
        var overrides = policy.Users.FirstOrDefault(u => u.UserId == userId)?.Settings ?? new List<PolicySetting>();
        var overrideByKey = new Dictionary<string, PolicySetting>(StringComparer.Ordinal);
        foreach (var o in overrides)
        {
            overrideByKey.TryAdd(o.Key, o);
        }

        var result = new List<EffectiveRule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var global in policy.Settings)
        {
            if (!seen.Add(global.Key))
            {
                continue;
            }

            if (overrideByKey.TryGetValue(global.Key, out var o))
            {
                result.Add(new EffectiveRule(global.Key, o.DefaultValue ?? global.DefaultValue, o.Locked));
            }
            else
            {
                result.Add(new EffectiveRule(global.Key, global.DefaultValue, global.Locked));
            }
        }

        foreach (var o in overrides)
        {
            if (seen.Add(o.Key))
            {
                result.Add(new EffectiveRule(o.Key, o.DefaultValue, o.Locked));
            }
        }

        return result;
    }

    /// <summary>
    /// Locked to the Jellyfin account type means each user's Seerr username is their Jellyfin
    /// username, so it's forced. Otherwise each user keeps their own.
    /// </summary>
    public static bool IsSeerrUsernameForced(IEnumerable<EffectiveRule> rules)
    {
        var authMethod = rules.LastOrDefault(r => r.Key == SeerrAuthMethod);
        return authMethod is { Locked: true, Value: "jellyfin" };
    }

    public static HashSet<string> LockedKeys(List<EffectiveRule> rules)
    {
        var locked = rules.Where(r => r.Locked).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        if (IsSeerrUsernameForced(rules))
        {
            locked.Add(SeerrUsername);
        }

        return locked;
    }

    /// <summary>
    /// GET Settings exactly as 0.3.x built it: one row per rule in order (null values included),
    /// then seerrUsername, then the user's other stored values.
    /// </summary>
    public static List<SyncedSettingDto> LegacyMerge(List<EffectiveRule> rules, IReadOnlyDictionary<string, string?> userValues, string? jellyfinUsername)
    {
        var result = new List<SyncedSettingDto>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            // Never one literal Seerr username for everyone; see seerrAuthMethod below.
            if (rule.Key == SeerrUsername)
            {
                continue;
            }

            seenKeys.Add(rule.Key);
            var value = rule.Locked
                ? rule.Value
                : (userValues.TryGetValue(rule.Key, out var stored) ? stored : rule.Value);
            result.Add(new SyncedSettingDto { Key = rule.Key, Value = value, IsLocked = rule.Locked });
        }

        seenKeys.Add(SeerrUsername);
        if (IsSeerrUsernameForced(rules))
        {
            result.Add(new SyncedSettingDto { Key = SeerrUsername, Value = jellyfinUsername, IsLocked = true });
        }
        else
        {
            var storedUsername = userValues.TryGetValue(SeerrUsername, out var su) ? su : null;
            result.Add(new SyncedSettingDto { Key = SeerrUsername, Value = storedUsername, IsLocked = false });
        }

        foreach (var (key, value) in userValues)
        {
            if (seenKeys.Contains(key))
            {
                continue;
            }

            result.Add(new SyncedSettingDto { Key = key, Value = value, IsLocked = false });
        }

        return result;
    }

    /// <summary>
    /// Effective values for every schema key: locked → policy value, else the user's stored
    /// value (synced keys only), else the policy default; keys with no value at all are left out.
    /// </summary>
    public static List<SyncSettingDto> SyncMerge(SettingsSchema schema, List<EffectiveRule> rules, IReadOnlyDictionary<string, string?> userValues, string? jellyfinUsername)
    {
        var ruleByKey = new Dictionary<string, EffectiveRule>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            ruleByKey.TryAdd(rule.Key, rule);
        }

        var forced = IsSeerrUsernameForced(rules);
        var result = new List<SyncSettingDto>();
        foreach (var setting in schema.Settings)
        {
            var key = setting.Key;
            if (key == SeerrUsername)
            {
                if (forced)
                {
                    result.Add(new SyncSettingDto { Key = key, Value = jellyfinUsername, IsLocked = true, Source = "locked" });
                }
                else if (userValues.TryGetValue(key, out var su) && su is not null)
                {
                    result.Add(new SyncSettingDto { Key = key, Value = su, IsLocked = false, Source = "user" });
                }

                continue;
            }

            ruleByKey.TryGetValue(key, out var r);
            if (r is { Locked: true })
            {
                result.Add(new SyncSettingDto { Key = key, Value = r.Value, IsLocked = true, Source = "locked" });
            }
            else if (setting.Scope == SettingsSchema.ScopeSynced && userValues.TryGetValue(key, out var stored) && stored is not null)
            {
                result.Add(new SyncSettingDto { Key = key, Value = stored, IsLocked = false, Source = "user" });
            }
            else if (r?.Value is not null)
            {
                result.Add(new SyncSettingDto { Key = key, Value = r.Value, IsLocked = false, Source = "default" });
            }
        }

        return result;
    }

    /// <summary>
    /// Applies incoming values to the stored map. Locked keys are never stored; a null value
    /// clears the key. With <paramref name="replace"/>, every unlocked synced key not in the
    /// payload is cleared too. Keys that aren't in the schema are kept as they were.
    /// Returns whether anything changed.
    /// </summary>
    public static bool Apply(SettingsSchema schema, Dictionary<string, string?> stored, IReadOnlyDictionary<string, string?> incoming,
                             HashSet<string> lockedKeys, bool replace)
    {
        var changed = false;
        foreach (var (key, value) in incoming)
        {
            if (lockedKeys.Contains(key))
            {
                continue;
            }

            if (value is null)
            {
                changed |= stored.Remove(key);
            }
            else if (stored.TryGetValue(key, out var old) ? old != value : stored.Count < MaxStoredKeys)
            {
                stored[key] = value;
                changed = true;
            }
        }

        if (replace)
        {
            foreach (var setting in schema.Settings)
            {
                if (setting.Scope == SettingsSchema.ScopeSynced && !lockedKeys.Contains(setting.Key) && !incoming.ContainsKey(setting.Key))
                {
                    changed |= stored.Remove(setting.Key);
                }
            }
        }

        return changed;
    }

    public static SettingsPolicy FromLegacy(IEnumerable<LockedSetting> legacy)
    {
        var policy = new SettingsPolicy();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in legacy)
        {
            if (string.IsNullOrEmpty(l.Key) || !seen.Add(l.Key))
            {
                continue;
            }

            policy.Settings.Add(new PolicySetting { Key = l.Key, DefaultValue = l.PrefillValue, Locked = l.IsLocked });
        }

        return policy;
    }

    public static List<LockedSetting> ToLegacy(SettingsPolicy policy)
    {
        return policy.Settings.Select(s => new LockedSetting { Key = s.Key, IsLocked = s.Locked, PrefillValue = s.DefaultValue }).ToList();
    }

    /// <summary>
    /// Brings <see cref="PluginConfiguration.Policy"/> up to date with the legacy list: built from
    /// it after an upgrade, and the global part re-imported when something wrote only the legacy
    /// list (0.3.x after a downgrade, or the generic plugin configuration API). User overrides stay.
    /// Returns the policy to use and whether the configuration needs saving.
    /// </summary>
    public static (SettingsPolicy Policy, bool Changed) Reconcile(SettingsPolicy? policy, List<LockedSetting>? legacy)
    {
        legacy ??= new List<LockedSetting>();
        if (policy is null)
        {
            return (FromLegacy(legacy), true);
        }

        policy.Settings ??= new List<PolicySetting>();
        policy.Users ??= new List<UserPolicyOverride>();
        if (LegacyMatches(ToLegacy(policy), legacy))
        {
            return (policy, false);
        }

        var reimported = FromLegacy(legacy);
        reimported.Users = policy.Users;
        return (reimported, true);
    }

    private static bool LegacyMatches(List<LockedSetting> derived, List<LockedSetting> legacy)
    {
        if (derived.Count != legacy.Count)
        {
            return false;
        }

        for (var i = 0; i < derived.Count; i++)
        {
            if (derived[i].Key != legacy[i].Key || derived[i].IsLocked != legacy[i].IsLocked || derived[i].PrefillValue != legacy[i].PrefillValue)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A device name trimmed, or an error when it's too long or has control characters.</summary>
    public static (string? Name, string? Error) CleanName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length > MaxNameLength)
        {
            return (null, $"A device name can be at most {MaxNameLength} characters.");
        }

        if (trimmed.Any(char.IsControl))
        {
            return (null, "A device name can't contain control characters.");
        }

        return (trimmed, null);
    }

    public static bool IsCleanField(string? value, int maxLength = MaxFieldLength)
    {
        return value is null || (value.Length <= maxLength && !value.Any(char.IsControl));
    }
}
