using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ArcfinSync.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public bool NotificationsEnabled { get; set; } = true;

    public bool SeerrWebhookEnabled { get; set; } = true;

    public string SeerrWebhookSecret { get; set; } = string.Empty;

    public List<LockedSetting> LockedSettings { get; set; } = new();

    public string AdvancedYamlConfig { get; set; } = string.Empty;
}

public class LockedSetting
{
    public string Key { get; set; } = string.Empty;

    public bool IsLocked { get; set; }

    public string? PrefillValue { get; set; }
}
