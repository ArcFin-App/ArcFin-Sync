using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ArcfinSync.Configuration;

/// <summary>
/// 0.3.x also had notification, Seerr webhook and YAML fields that nothing used. They're gone;
/// the XML serializer skips them when it reads an older file.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
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

    /// <summary>Who may use Pick Together. On for everyone until an admin changes it.</summary>
    public PickTogetherSettings PickTogether { get; set; } = new();
}

public class PickTogetherSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Users it's off for while it's on for everyone else.</summary>
    public List<Guid> DisabledUsers { get; set; } = new();
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

    /// <summary>Saved under the element name 0.4 betas used, so their files still read.</summary>
    [XmlArrayItem("PolicySetting")]
    public List<UserPolicySetting> Settings { get; set; } = new();
}

/// <summary>
/// One user's own default and lock for a setting. A null <see cref="Locked"/> follows the
/// server-wide lock, so a user can have their own default and still be locked or unlocked with
/// everyone else.
/// </summary>
public class UserPolicySetting
{
    public string Key { get; set; } = string.Empty;

    public string? DefaultValue { get; set; }

    public bool? Locked { get; set; }
}
