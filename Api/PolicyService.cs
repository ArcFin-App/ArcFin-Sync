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
            var (policy, changed) = SettingsRules.Reconcile(SettingsSchema.Embedded, config.Policy, config.LockedSettings);
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

    /// <summary>
    /// Checks an admin's policy and normalises it: schema keys only, values of the right type, no
    /// duplicates, known users. An empty bool/int/double default counts as no default. seerrUsername is dropped: it's
    /// never one value for everyone and follows seerrAuthMethod instead.
    /// </summary>
    public static (SettingsPolicy? Policy, string? Error) Validate(SettingsSchema schema, SettingsPolicy? incoming, SettingsPolicy existing, Func<Guid, bool> userExists)
    {
        if (incoming is null)
        {
            return (null, "No policy.");
        }

        var result = new SettingsPolicy();
        var (global, error) = CleanEntries(schema, incoming.Settings?.Select(s => s is null ? null : new Entry(s.Key, s.DefaultValue, s.Locked)).ToList(), isGlobal: true);
        if (error is not null)
        {
            return (null, error);
        }

        result.Settings = global!.Select(e => new PolicySetting { Key = e.Key, DefaultValue = e.Value, Locked = e.Locked == true }).ToList();
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

            var (entries, userError) = CleanEntries(schema, user.Settings?.Select(s => s is null ? null : new Entry(s.Key, s.DefaultValue, s.Locked)).ToList(), isGlobal: false);
            if (userError is not null)
            {
                return (null, userError);
            }

            // An entry with neither its own default nor its own lock is the same as none.
            var settings = entries!.Where(e => e.Value is not null || e.Locked is not null)
                .Select(e => new UserPolicySetting { Key = e.Key, DefaultValue = e.Value, Locked = e.Locked }).ToList();
            if (settings.Count > 0)
            {
                result.Users.Add(new UserPolicyOverride { UserId = user.UserId, Settings = settings });
            }
        }

        return (result, null);
    }

    private static string InvalidValueMessage(SchemaSetting setting)
    {
        var key = setting.Key;
        if (setting.Type == "bool")
        {
            return $"{key} must be true or false.";
        }

        if (setting.IsJson)
        {
            return $"{key} must be a JSON array or object.";
        }

        if (setting.Options is { Count: > 0 })
        {
            return $"{key} must be one of {string.Join(", ", setting.Options.Select(o => o.Value))}.";
        }

        if (setting.Type == "double")
        {
            var range = setting.Min is not null || setting.Max is not null
                ? $" from {setting.Min?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "any"} to {setting.Max?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "any"}"
                : string.Empty;
            return $"{key} must be a number written with a dot{range}.";
        }

        return $"{key} must be a whole number.";
    }

    private sealed record Entry(string? Key, string? Value, bool? Locked);

    private sealed record CleanEntry(string Key, string? Value, bool? Locked);

    private static (List<CleanEntry>? Entries, string? Error) CleanEntries(SettingsSchema schema, List<Entry?>? entries, bool isGlobal)
    {
        var result = new List<CleanEntry>();
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
            if (setting is null)
            {
                return (null, $"{entry.Key} isn't an Arcfin setting.");
            }

            var value = entry.Value;
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
                if (value.Length == 0 && (setting.Type is "bool" or "int" or "double" || setting.IsJson))
                {
                    value = null;
                }
                else if (!SettingsSchema.IsValidValue(setting, value))
                {
                    return (null, InvalidValueMessage(setting));
                }
            }

            // A server-wide lock needs a value to pin, or every device would keep whatever it has.
            // Unset, it locks to the app's own default. A user's override with no value takes
            // the server-wide one instead, as one with no lock of its own takes the server-wide lock.
            if (isGlobal && entry.Locked == true && value is null)
            {
                value = setting?.Default;
            }

            result.Add(new CleanEntry(entry.Key, value, isGlobal ? entry.Locked == true : entry.Locked));
        }

        return (result, null);
    }
}
