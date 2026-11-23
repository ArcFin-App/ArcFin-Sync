using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ArcfinSync.Configuration;

namespace Jellyfin.Plugin.ArcfinSync.Api;

/// <summary>
/// Reads and writes the policy in the plugin configuration, keeping the 0.3.x LockedSettings
/// list derived from it so a downgrade keeps the global locks and defaults.
/// </summary>
public static class PolicyService
{
    private static readonly object ConfigLock = new();

    public static SettingsPolicy Get()
    {
        lock (ConfigLock)
        {
            var plugin = Plugin.Instance!;
            var config = plugin.Configuration;
            var (policy, changed) = SettingsRules.Reconcile(config.Policy, config.LockedSettings);
            if (changed)
            {
                config.Policy = policy;
                config.LockedSettings = SettingsRules.ToLegacy(policy);
                plugin.SaveConfiguration();
            }

            return policy;
        }
    }

    public static void Save(SettingsPolicy policy)
    {
        lock (ConfigLock)
        {
            var plugin = Plugin.Instance!;
            plugin.Configuration.Policy = policy;
            plugin.Configuration.LockedSettings = SettingsRules.ToLegacy(policy);
            plugin.SaveConfiguration();
        }
    }

    public static void UpdateConfig(Action<PluginConfiguration> change)
    {
        lock (ConfigLock)
        {
            var plugin = Plugin.Instance!;
            change(plugin.Configuration);
            plugin.SaveConfiguration();
        }
    }

    /// <summary>
    /// Checks an admin's policy and normalises it: schema keys only (plus any non-schema keys the
    /// policy already had, carried over from 0.3.x), values of the right type, no duplicates,
    /// known users. An empty bool/int default counts as no default. seerrUsername is dropped: it's
    /// never one value for everyone and follows seerrAuthMethod instead.
    /// </summary>
    public static (SettingsPolicy? Policy, string? Error) Validate(SettingsSchema schema, SettingsPolicy? incoming, SettingsPolicy existing, Func<Guid, bool> userExists)
    {
        if (incoming is null)
        {
            return (null, "No policy.");
        }

        var result = new SettingsPolicy();
        var existingGlobal = existing.Settings.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var (global, error) = CleanEntries(schema, incoming.Settings, existingGlobal, isGlobal: true);
        if (error is not null)
        {
            return (null, error);
        }

        result.Settings = global!;
        var seenUsers = new HashSet<Guid>();
        foreach (var user in incoming.Users ?? new List<UserPolicyOverride>())
        {
            if (user is null || user.UserId == Guid.Empty || !userExists(user.UserId))
            {
                return (null, $"Unknown user {user?.UserId}.");
            }

            if (!seenUsers.Add(user.UserId))
            {
                return (null, $"User {user.UserId} is listed twice.");
            }

            var existingUser = existing.Users.FirstOrDefault(u => u.UserId == user.UserId)?.Settings.Select(s => s.Key).ToHashSet(StringComparer.Ordinal)
                               ?? new HashSet<string>(StringComparer.Ordinal);
            var (entries, userError) = CleanEntries(schema, user.Settings, existingUser, isGlobal: false);
            if (userError is not null)
            {
                return (null, userError);
            }

            if (entries!.Count > 0)
            {
                result.Users.Add(new UserPolicyOverride { UserId = user.UserId, Settings = entries });
            }
        }

        return (result, null);
    }

    private static (List<PolicySetting>? Entries, string? Error) CleanEntries(SettingsSchema schema, List<PolicySetting>? entries, HashSet<string> carried, bool isGlobal)
    {
        var result = new List<PolicySetting>();
        if (entries is null)
        {
            return (result, null);
        }

        if (entries.Count > SettingsRules.MaxKeys)
        {
            return (null, $"At most {SettingsRules.MaxKeys} settings.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is null || string.IsNullOrEmpty(entry.Key) || entry.Key.Length > SettingsRules.MaxKeyLength)
            {
                return (null, "A setting key is empty or too long.");
            }

            if (entry.Key == SettingsRules.SeerrUsername)
            {
                continue;
            }

            if (!seen.Add(entry.Key))
            {
                return (null, $"{entry.Key} is listed twice.");
            }

            var setting = schema.Find(entry.Key);
            if (setting is null && !carried.Contains(entry.Key))
            {
                return (null, $"{entry.Key} isn't an Arcfin setting.");
            }

            var value = entry.DefaultValue;
            if (value is not null && value.Any(char.IsControl))
            {
                return (null, $"The value of {entry.Key} can't contain control characters.");
            }

            if (value is not null && value.Length > SettingsRules.MaxValueLength)
            {
                return (null, $"The value of {entry.Key} is longer than {SettingsRules.MaxValueLength} characters.");
            }

            if (setting is not null && value is not null)
            {
                if (value.Length == 0 && setting.Type is "bool" or "int")
                {
                    value = null;
                }
                else if (!SettingsSchema.IsValidValue(setting, value))
                {
                    return (null, setting.Type == "bool" ? $"{entry.Key} must be true or false."
                        : setting.Options is { Count: > 0 } ? $"{entry.Key} must be one of {string.Join(", ", setting.Options.Select(o => o.Value))}."
                        : $"{entry.Key} must be a whole number.");
                }
            }

            // A server-wide lock needs a value to pin, or every device would keep whatever it has.
            // Unset, it locks to the app's own default. A user's override with no value takes
            // the server-wide one instead.
            if (isGlobal && entry.Locked && value is null)
            {
                value = setting?.Default;
            }

            result.Add(new PolicySetting { Key = entry.Key, DefaultValue = value, Locked = entry.Locked });
        }

        return (result, null);
    }
}
