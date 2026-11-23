using System;
using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ArcfinSync.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool NotificationsEnabled { get; set; } = true;

    public bool SeerrWebhookEnabled { get; set; } = true;

    public string SeerrWebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// The 0.3 form of the global policy. Still written, derived from <see cref="Policy"/>, so a
    /// server that goes back to 0.3.x keeps its locks and defaults.
    /// </summary>
    public List<LockedSetting> LockedSettings { get; set; } = new();

    /// <summary>
    /// Defaults and locks, globally and per user. Null until the first load after upgrading
    /// from 0.3.x, which builds it from <see cref="LockedSettings"/>.
    /// </summary>
    public SettingsPolicy? Policy { get; set; }

    public string AdvancedYamlConfig { get; set; } = string.Empty;
}

public class LockedSetting
{
    public string Key { get; set; } = string.Empty;

    public bool IsLocked { get; set; }

    public string? PrefillValue { get; set; }
}

public class SettingsPolicy
{
    public List<PolicySetting> Settings { get; set; } = new();

    public List<UserPolicyOverride> Users { get; set; } = new();
}

public class PolicySetting
{
    public string Key { get; set; } = string.Empty;

    public string? DefaultValue { get; set; }

    public bool Locked { get; set; }
}

public class UserPolicyOverride
{
    public Guid UserId { get; set; }

    public List<PolicySetting> Settings { get; set; } = new();
}
