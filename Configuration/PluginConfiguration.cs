using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ArcfinSync.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool NotificationsEnabled { get; set; } = true;

    public bool SeerrWebhookEnabled { get; set; } = true;

    public string SeerrWebhookSecret { get; set; } = string.Empty;

    /// When true and a user hasn't set (or been given) a Seerr username some other way,
    /// their Jellyfin username is used as the default. Never applies to a password — Arcfin
    /// only ever stores the Jellyfin access token, never the raw password, so there is
    /// nothing safe to prefill it with here.
    public bool SeerrUsernameFollowsJellyfin { get; set; }

    public List<LockedSetting> LockedSettings { get; set; } = new();

    public string AdvancedYamlConfig { get; set; } = string.Empty;
}

public class LockedSetting
{
    public string Key { get; set; } = string.Empty;

    public bool IsLocked { get; set; }

    public string? PrefillValue { get; set; }
}
