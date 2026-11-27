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

    /// <summary>Who may start or join a watch party. On for everyone until an admin changes it.</summary>
    public PickTogetherSettings WatchParty { get; set; } = new();

    /// <summary>Rows picked for each user on the server. On until an admin turns them off.</summary>
    public bool RecommendationsEnabled { get; set; }

    /// <summary>Hiding titles from Continue Watching and Next Up. Off until an admin turns it on.</summary>
    public bool HiddenEnabled { get; set; }

    /// <summary>Seerr and TMDB through the server. The keys themselves are in the encrypted vault, never here.</summary>
    public IntegrationSettings Integrations { get; set; } = new();

    public LimitSettings Limits { get; set; } = new();

    /// <summary>Automatic key rotation and what happens to devices that fall behind; all off until an admin turns them on.</summary>
    public SecuritySettings Security { get; set; } = new();

    /// <summary>Past watch parties and Pick Together sessions, off until an admin turns it on.</summary>
    public HistorySettings History { get; set; } = new();

    /// <summary>Caps on what Arcfin may keep downloaded per person, across all their devices.</summary>
    public DownloadLimitSettings Downloads { get; set; } = new();

    /// <summary>Accounts used on shared screens, and whose own progress follows what's watched on them.</summary>
    public List<SharedAccount> SharedAccounts { get; set; } = new();

    /// <summary>Banners shown in Arcfin, from and until set times.</summary>
    public List<Announcement> Announcements { get; set; } = new();

    /// <summary>Planned downtime: Arcfin warns ahead, then explains rather than failing to connect.</summary>
    public List<MaintenanceWindow> Maintenance { get; set; } = new();

    /// <summary>Seerr request changes, announcements and maintenance for Arcfin to notify about. Off until an admin turns it on.</summary>
    public NotificationSettings Notifications { get; set; } = new();

    /// <summary>Short scenes from the library in a vertical feed. Off until an admin turns it on.</summary>
    public ArcsSettings Arcs { get; set; } = new();
}

public class ArcsSettings
{
    public bool Enabled { get; set; }

    public List<Guid> DisabledUsers { get; set; } = new();

    /// <summary>Libraries clips come from; empty for every film and show library.</summary>
    public List<Guid> Libraries { get; set; } = new();

    public int MinSeconds { get; set; } = 15;

    public int MaxSeconds { get; set; } = 60;

    public int DefaultSeconds { get; set; } = 30;

    /// <summary>UserChoice or AlwaysSafe.</summary>
    public string Spoilers { get; set; } = "UserChoice";

    /// <summary>Allowed or Off.</summary>
    public string Sharing { get; set; } = "Allowed";

    /// <summary>Optional, Forced or Off.</summary>
    public string Watermark { get; set; } = "Optional";

    public bool ClipOfTheDay { get; set; } = true;

    /// <summary>Titles never used, films or shows.</summary>
    public List<Guid> BannedItems { get; set; } = new();

    public int CacheMb { get; set; } = 2048;

    /// <summary>Clips of each feed page rendered ahead in the background.</summary>
    public int Prewarm { get; set; } = 3;

    /// <summary>Titles without chapters get clip starts from a scheduled scene-change scan.</summary>
    public bool UseSceneDetection { get; set; } = true;

    /// <summary>Scene scans run side by side (1 to 8). Each one uses more of the GPU, or the CPU without one.</summary>
    public int SceneScans { get; set; } = 1;

    /// <summary>Trailers of titles not on the server, to request through Seerr, mixed into the feed.</summary>
    public bool RequestsInFeed { get; set; } = true;

    /// <summary>Request clips play the title's YouTube trailer. Off, they show its poster only, whatever people choose.</summary>
    public bool YouTubeTrailers { get; set; } = true;

    /// <summary>Everyone or Off: whether people may send scenes to each other.</summary>
    public string Sending { get; set; } = "Everyone";

    /// <summary>Search by a line of dialogue, from the "Arcs dialogue index" scheduled task.</summary>
    public bool DialogueSearch { get; set; } = true;

    public ArcsWeights Weights { get; set; } = new();
}

public class ArcsWeights
{
    public double History { get; set; } = 1.0;

    public double Likes { get; set; } = 1.0;

    public double WatchTime { get; set; } = 1.0;

    public double WatchFromHere { get; set; } = 1.0;

    public double Shares { get; set; } = 1.0;

    public double Favorites { get; set; } = 1.0;

    public double Similar { get; set; } = 1.0;

    public double Popular { get; set; } = 1.0;

    /// <summary>Share of feed slots given to titles outside the person's usual taste.</summary>
    public double Exploration { get; set; } = 0.15;

    public double Fresh { get; set; } = 1.0;
}

public class NotificationSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// The address Seerr reaches this server on, when it isn't the dashboard's own (a local or
    /// Docker address, say). Only shown, to build the webhook URL for Seerr; nothing calls it.
    /// </summary>
    public string? WebhookAddress { get; set; }
}

public class DownloadLimitSettings
{
    public bool Enabled { get; set; }

    /// <summary>Titles each person may keep downloaded, or null for no cap on titles.</summary>
    public int? MaxTitles { get; set; }

    /// <summary>Gigabytes each person may keep downloaded, or null for no cap on space.</summary>
    public double? MaxGigabytes { get; set; }

    public List<DownloadLimitOverride> Users { get; set; } = new();

    /// <summary>Caps on downloaded music, kept apart from movies and episodes. Off until turned on.</summary>
    public bool MusicEnabled { get; set; }

    public int? MusicMaxAlbums { get; set; }

    public int? MusicMaxSongs { get; set; }

    public double? MusicMaxGigabytes { get; set; }
}

public class DownloadLimitOverride
{
    public Guid UserId { get; set; }

    /// <summary>inherit, off (no caps for them) or custom (their own caps below).</summary>
    public string Mode { get; set; } = "inherit";

    public int? MaxTitles { get; set; }

    public double? MaxGigabytes { get; set; }

    /// <summary>inherit, off or custom, for music.</summary>
    public string MusicMode { get; set; } = "inherit";

    public int? MusicMaxAlbums { get; set; }

    public int? MusicMaxSongs { get; set; }

    public double? MusicMaxGigabytes { get; set; }
}

public class SharedAccount
{
    /// <summary>The account signed in on the shared screen, e.g. "Home".</summary>
    public Guid UserId { get; set; }

    /// <summary>The people who may watch on it; Arcfin asks which of them are watching.</summary>
    public List<Guid> Members { get; set; } = new();
}

public class Announcement
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    /// <summary>info or warning.</summary>
    public string Style { get; set; } = "info";

    public DateTime? Starts { get; set; }

    public DateTime? Ends { get; set; }

    /// <summary>Empty for everyone.</summary>
    public List<Guid> Users { get; set; } = new();
}

public class MaintenanceWindow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Starts { get; set; }

    public DateTime Ends { get; set; }

    public string Message { get; set; } = string.Empty;
}

public class PinRules
{
    /// <summary>none, everyone or chosen (<see cref="Users"/>).</summary>
    public string RequiredFor { get; set; } = "none";

    public List<Guid> Users { get; set; } = new();

    /// <summary>4 to 8 digits.</summary>
    public int MinLength { get; set; } = 4;

    /// <summary>Wrong tries before the profile locks on that device until an admin unlocks it; null for the app's usual waits.</summary>
    public int? MaxTries { get; set; }

    public const int LongestPin = 8;
}

public class SecuritySettings
{
    /// <summary>Signed commands (clear a PIN, lock, unlock) and everything built on them. Off until an admin turns it on: no key is made and devices don't check in until then.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Devices reaching the server over plain HTTP trust its key without anyone checking the
    /// fingerprint. For households where no one will; the setting itself reaches devices over
    /// HTTP too, so it suits a home network the admin trusts.
    /// </summary>
    public bool TrustOverHttp { get; set; }

    /// <summary>Make a new signing key every <see cref="RotateEveryDays"/> days, signed by the last.</summary>
    public bool AutoRotate { get; set; }

    public int RotateEveryDays { get; set; } = 1;

    /// <summary>off, signOut, or lock (sign out, and lock Arcfin on that device until an admin unlocks it).</summary>
    public string StaleAction { get; set; } = StaleActions.Off;

    /// <summary>Days a device may go without checking in with the current key.</summary>
    public int StaleAfterDays { get; set; } = 14;

    /// <summary>When the stale-device rule was last turned on: no device counts as behind from before it.</summary>
    public DateTime? StaleSince { get; set; }

    /// <summary>
    /// allow, or signOut: Arcfin devices too old to check in with their key (from before 0.5.0
    /// build 3's security features) can't follow key changes or carry out locks, so they're
    /// signed out a day after they're first seen.
    /// </summary>
    public string OlderApps { get; set; } = OlderAppActions.Allow;

    /// <summary>When the rule for older apps was last turned on.</summary>
    public DateTime? OlderAppsSince { get; set; }

    public List<SecurityUserOverride> Users { get; set; } = new();

    /// <summary>A new Arcfin device waits until one of the person's trusted devices, or an admin, approves it.</summary>
    public bool RequireTrustedDevices { get; set; }

    /// <summary>When trusted devices were turned on: devices already checked in by then are trusted.</summary>
    public DateTime? TrustedDevicesSince { get; set; }

    public PinRules Pins { get; set; } = new();

    /// <summary>Arcfin won't use plain HTTP unless the address is on the local network.</summary>
    public bool RequireHttpsAway { get; set; }
}

public class SecurityUserOverride
{
    public Guid UserId { get; set; }

    /// <summary>inherit, off, signOut or lock.</summary>
    public string StaleAction { get; set; } = StaleActions.Inherit;

    /// <summary>This user's own days, or null for the server's.</summary>
    public int? StaleAfterDays { get; set; }

    /// <summary>inherit, on or off: whether this person's new devices need approving.</summary>
    public string TrustedDevices { get; set; } = StaleActions.Inherit;
}

public static class OlderAppActions
{
    public const string Allow = "allow";
    public const string SignOut = "signOut";
}

public static class StaleActions
{
    public const string Inherit = "inherit";
    public const string Off = "off";
    public const string SignOut = "signOut";
    public const string Lock = "lock";

    public static bool IsKnown(string? action, bool allowInherit) => action is Off or SignOut or Lock || (allowInherit && action == Inherit);
}

public class HistorySettings
{
    public bool Enabled { get; set; }

    /// <summary>Days to keep entries; null keeps them for ever.</summary>
    public int? KeepDays { get; set; } = 90;
}

public class IntegrationSettings
{
    /// <summary>Seerr's address as the server reaches it, e.g. http://seerr:5055.</summary>
    public string? SeerrUrl { get; set; }

    /// <summary>Seerr's address as people reach it (e.g. https://requests.example.com); it becomes everyone's Seerr URL in Arcfin unless the policy sets one.</summary>
    public string? SeerrPublicUrl { get; set; }

    /// <summary>Make a Seerr account for a Jellyfin user who hasn't got one yet, the first time they use it.</summary>
    public bool AddSeerrUsers { get; set; }
}

public class LimitSettings
{
    /// <summary>IANA or Windows time zone for bedtimes and the daily reset; null for the server's own.</summary>
    public string? TimeZone { get; set; }

    public List<Jellyfin.Plugin.ArcfinSync.Api.Limits.WatchLimitRule> Rules { get; set; } = new();

    /// <summary>Tell the person 10 and 5 minutes before their time or bedtime, on whatever's playing.</summary>
    public bool WarnBeforeEnd { get; set; } = true;
}

public class PickTogetherSettings
{
    /// <summary>Off until an admin turns it on.</summary>
    public bool Enabled { get; set; }

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
