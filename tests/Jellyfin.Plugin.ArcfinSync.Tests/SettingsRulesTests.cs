using Jellyfin.Plugin.ArcfinSync.Api;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ArcfinSync.Tests;

public class SettingsRulesTests
{
    private static readonly Guid Alice = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Bob = Guid.Parse("22222222-2222-2222-2222-222222222222");

    internal static readonly SettingsSchema Schema = SettingsSchema.Parse("""
    {
      "version": 1,
      "groups": [ { "id": "playback", "title": "Playback" } ],
      "settings": [
        { "key": "skipIntro", "type": "bool", "scope": "synced", "group": "playback", "title": "Skip Intro", "default": "false" },
        { "key": "skipForwardSeconds", "type": "int", "scope": "synced", "group": "playback", "title": "Skip Forward",
          "options": [ { "value": "10", "title": "10 seconds" }, { "value": "15", "title": "15 seconds" }, { "value": "30", "title": "30 seconds" } ] },
        { "key": "tvOnly", "type": "bool", "scope": "synced", "title": "TV only", "platforms": [ "tvOS" ] },
        { "key": "seerrURL", "type": "string", "scope": "synced", "title": "Seerr URL", "platforms": [ "iOS", "macOS", "tvOS" ] },
        { "key": "videoQuality", "type": "string", "scope": "synced", "group": "playback", "title": "Quality",
          "options": [ { "value": "Max", "title": "Max" } ] },
        { "key": "seerrAuthMethod", "type": "string", "scope": "synced", "title": "Seerr Account Type" },
        { "key": "seerrUsername", "type": "string", "scope": "synced", "title": "Seerr Username" },
        { "key": "codecAllowHEVCDirectPlay", "type": "bool", "scope": "device", "title": "HEVC" },
        { "key": "playbackSpeed", "type": "double", "scope": "synced", "title": "Speed", "min": 0.5, "max": 2 },
        { "key": "subtitleScale", "type": "double", "scope": "synced", "title": "Scale",
          "options": [ { "value": "0.75", "title": "Small" }, { "value": "1.0", "title": "Normal" } ] },
        { "key": "homeShelves", "type": "string", "format": "json", "scope": "synced", "title": "Home shelves" },
        { "key": "streamingServices", "type": "json", "scope": "device", "title": "Streaming services" }
      ]
    }
    """);

    private static SettingsPolicy Policy(params PolicySetting[] global) => new() { Settings = global.ToList() };

    private static PolicySetting P(string key, string? value, bool locked) => new() { Key = key, DefaultValue = value, Locked = locked };

    [Fact]
    public void UserOverrideLockWinsAndCanUnlock()
    {
        var policy = Policy(P("skipIntro", "true", true), P("videoQuality", "Max", false));
        policy.Users.Add(new UserPolicyOverride { UserId = Alice, Settings = { P("skipIntro", null, false), P("videoQuality", "720p", true) } });

        var alice = SettingsRules.RulesForUser(policy, Alice);
        Assert.Equal(new EffectiveRule("skipIntro", "true", false), alice[0]);
        Assert.Equal(new EffectiveRule("videoQuality", "720p", true), alice[1]);

        var bob = SettingsRules.RulesForUser(policy, Bob);
        Assert.Equal(new EffectiveRule("skipIntro", "true", true), bob[0]);
        Assert.Equal(new EffectiveRule("videoQuality", "Max", false), bob[1]);
    }

    [Fact]
    public void SyncMergeSourcesAndOmissions()
    {
        var policy = Policy(P("skipIntro", "true", true), P("skipForwardSeconds", "30", false), P("codecAllowHEVCDirectPlay", "false", true));
        var rules = SettingsRules.RulesForUser(policy, Alice);
        var stored = new Dictionary<string, string?> { ["skipIntro"] = "false", ["videoQuality"] = "720p", ["codecAllowHEVCDirectPlay"] = "true", ["legacyKey"] = "x" };

        var merged = SettingsRules.SyncMerge(Schema, rules, stored, "alice").ToDictionary(s => s.Key);

        Assert.Equal(("true", true, "locked"), (merged["skipIntro"].Value, merged["skipIntro"].IsLocked, merged["skipIntro"].Source));
        Assert.Equal(("30", "default"), (merged["skipForwardSeconds"].Value, merged["skipForwardSeconds"].Source));
        Assert.Equal(("720p", "user"), (merged["videoQuality"].Value, merged["videoQuality"].Source));
        Assert.Equal(("false", "locked"), (merged["codecAllowHEVCDirectPlay"].Value, merged["codecAllowHEVCDirectPlay"].Source));
        Assert.False(merged.ContainsKey("seerrAuthMethod"));
        Assert.False(merged.ContainsKey("seerrUsername"));
        Assert.False(merged.ContainsKey("legacyKey"));
    }

    [Fact]
    public void SeerrUsernameFollowsLockedAccountType()
    {
        var forced = SettingsRules.RulesForUser(Policy(P("seerrAuthMethod", "jellyfin", true), P("seerrUsername", "admin", true)), Alice);
        var stored = new Dictionary<string, string?> { ["seerrUsername"] = "mine" };

        var legacy = SettingsRules.LegacyMerge(forced, stored, "alice");
        Assert.Equal(new[] { "seerrAuthMethod", "seerrUsername" }, legacy.Select(s => s.Key));
        Assert.Equal(("alice", true), (legacy[1].Value, legacy[1].IsLocked));
        Assert.Contains(SettingsRules.SeerrUsername, SettingsRules.LockedKeys(forced));

        var local = SettingsRules.RulesForUser(Policy(P("seerrAuthMethod", "local", true)), Alice);
        var sync = SettingsRules.SyncMerge(Schema, local, stored, "alice").Single(s => s.Key == "seerrUsername");
        Assert.Equal(("mine", false, "user"), (sync.Value, sync.IsLocked, sync.Source));
        Assert.DoesNotContain(SettingsRules.SeerrUsername, SettingsRules.LockedKeys(local));
    }

    /// <summary>The 0.3.1 GetMergedSettings, kept here to prove the legacy output didn't move.</summary>
    private static List<SyncedSettingDto> OldMerge(List<LockedSetting> lockedSettings, Dictionary<string, string?> userValues, string? jellyfinUsername)
    {
        var result = new List<SyncedSettingDto>();
        var seenKeys = new HashSet<string>();
        LockedSetting? authMethodSetting = null;
        foreach (var setting in lockedSettings)
        {
            if (setting.Key == "seerrUsername")
            {
                continue;
            }

            if (setting.Key == "seerrAuthMethod")
            {
                authMethodSetting = setting;
            }

            seenKeys.Add(setting.Key);
            var value = setting.IsLocked ? setting.PrefillValue : (userValues.TryGetValue(setting.Key, out var s) ? s : setting.PrefillValue);
            result.Add(new SyncedSettingDto { Key = setting.Key, Value = value, IsLocked = setting.IsLocked });
        }

        seenKeys.Add("seerrUsername");
        if (authMethodSetting is { IsLocked: true, PrefillValue: "jellyfin" })
        {
            result.Add(new SyncedSettingDto { Key = "seerrUsername", Value = jellyfinUsername, IsLocked = true });
        }
        else
        {
            result.Add(new SyncedSettingDto { Key = "seerrUsername", Value = userValues.TryGetValue("seerrUsername", out var su) ? su : null, IsLocked = false });
        }

        foreach (var kvp in userValues)
        {
            if (!seenKeys.Contains(kvp.Key))
            {
                result.Add(new SyncedSettingDto { Key = kvp.Key, Value = kvp.Value, IsLocked = false });
            }
        }

        return result;
    }

    [Theory]
    [InlineData("jellyfin", true)]
    [InlineData("local", true)]
    [InlineData("jellyfin", false)]
    public void LegacyMergeMatchesOldPlugin(string authMethod, bool authLocked)
    {
        var legacy = new List<LockedSetting>
        {
            new() { Key = "videoQuality", IsLocked = true, PrefillValue = "1080p" },
            new() { Key = "skipIntro", IsLocked = false, PrefillValue = "false" },
            new() { Key = "seerrURL", IsLocked = false, PrefillValue = null },
            new() { Key = "seerrAuthMethod", IsLocked = authLocked, PrefillValue = authMethod },
            new() { Key = "skipForwardSeconds", IsLocked = true, PrefillValue = "10" }
        };
        var stored = new Dictionary<string, string?> { ["skipIntro"] = "true", ["videoQuality"] = "Max", ["seerrUsername"] = "bob", ["tvOnly"] = "true" };

        var (policy, _) = SettingsRules.Reconcile(Schema, null, legacy);
        var now = SettingsRules.LegacyMerge(SettingsRules.RulesForUser(policy, Alice), stored, "alice");
        var old = OldMerge(legacy, stored, "alice");

        Assert.Equal(old.Select(s => (s.Key, s.Value, s.IsLocked)), now.Select(s => (s.Key, s.Value, s.IsLocked)));
    }

    [Fact]
    public void ApplyNeverStoresLockedKeysAndReplaceClearsTheRest()
    {
        var stored = new Dictionary<string, string?>
        {
            ["skipIntro"] = "true", ["skipForwardSeconds"] = "30", ["videoQuality"] = "Max", ["legacyKey"] = "keep",
            ["tvOnly"] = "true", ["seerrURL"] = "https://seerr", ["seerrAuthMethod"] = "local"
        };
        var locked = new HashSet<string> { "videoQuality" };
        var incoming = SettingsRules.FilterToSchema(Schema, new Dictionary<string, string?> { ["skipIntro"] = "false", ["videoQuality"] = "480p" }, SettingsSchema.ScopeSynced);

        var unknownPlatform = new Dictionary<string, string?>(stored);
        SettingsRules.Apply(Schema, unknownPlatform, incoming, locked, replace: true, platform: null);
        Assert.Equal("30", unknownPlatform["skipForwardSeconds"]);

        Assert.True(SettingsRules.Apply(Schema, stored, incoming, locked, replace: true, platform: "iPadOS"));
        Assert.Equal("false", stored["skipIntro"]);
        Assert.False(stored.ContainsKey("skipForwardSeconds"));
        Assert.Equal("Max", stored["videoQuality"]);
        Assert.Equal("keep", stored["legacyKey"]);
        Assert.Equal("true", stored["tvOnly"]);
        Assert.Equal("https://seerr", stored["seerrURL"]);
        Assert.Equal("local", stored["seerrAuthMethod"]);

        Assert.False(SettingsRules.Apply(Schema, stored, incoming, locked, replace: false));
    }

    [Fact]
    public void FilterDropsUnknownWrongScopeAndBadTypes()
    {
        var raw = new Dictionary<string, string?>
        {
            ["skipIntro"] = "yes",
            ["skipForwardSeconds"] = "15",
            ["tvOnly"] = "nope",
            ["videoQuality"] = "anything",
            ["codecAllowHEVCDirectPlay"] = "true",
            ["accessToken"] = "secret"
        };

        var synced = SettingsRules.FilterToSchema(Schema, raw, SettingsSchema.ScopeSynced);
        Assert.Equal(new[] { "skipForwardSeconds", "videoQuality" }, synced.Keys.OrderBy(k => k));

        var snapshot = SettingsRules.FilterToSchema(Schema, raw, null);
        Assert.Equal(new[] { "codecAllowHEVCDirectPlay", "skipForwardSeconds", "videoQuality" }, snapshot.Keys.OrderBy(k => k));
    }

    [Theory]
    [InlineData("playbackSpeed", "1.25", true)]
    [InlineData("playbackSpeed", "2", true)]
    [InlineData("playbackSpeed", "1,25", false)]
    [InlineData("playbackSpeed", "2.5", false)]
    [InlineData("playbackSpeed", "NaN", false)]
    [InlineData("playbackSpeed", "Infinity", false)]
    [InlineData("subtitleScale", "1", true)]
    [InlineData("subtitleScale", "0.75", true)]
    [InlineData("subtitleScale", "0.8", false)]
    [InlineData("homeShelves", "[\"continue\",\"latest\"]", true)]
    [InlineData("homeShelves", "{\"a\":1}", true)]
    [InlineData("homeShelves", "\"just a string\"", false)]
    [InlineData("homeShelves", "[1,", false)]
    [InlineData("streamingServices", "[{\"id\":\"netflix\"}]", true)]
    [InlineData("streamingServices", "netflix", false)]
    [InlineData("subtitleScale", "1.0", true)]
    public void DoubleAndJsonValues(string key, string value, bool valid)
    {
        Assert.Equal(valid, SettingsSchema.IsValidValue(Schema.Find(key)!, value));
    }

    [Fact]
    public void PurgeKeepsOnlySyncedSchemaKeys()
    {
        var stored = new Dictionary<string, string?> { ["skipIntro"] = "true", ["offlineModeEnabled"] = "true", ["codecAllowHEVCDirectPlay"] = "true" };
        Assert.True(SettingsRules.PurgeToSchema(Schema, stored));
        Assert.Equal(new[] { "skipIntro" }, stored.Keys);
        Assert.False(SettingsRules.PurgeToSchema(Schema, stored));
    }

    [Fact]
    public void ReconcileDropsKeysTheSchemaDoesNotList()
    {
        var legacy = new List<LockedSetting>
        {
            new() { Key = "offlineModeEnabled", IsLocked = true, PrefillValue = "false" },
            new() { Key = "skipIntro", IsLocked = true, PrefillValue = "true" },
            new() { Key = "streamystatsURL", PrefillValue = null }
        };
        var (migrated, _) = SettingsRules.Reconcile(Schema, null, legacy);
        Assert.Equal(new[] { "skipIntro" }, migrated.Settings.Select(s => s.Key));

        // A 0.4.0 policy that still holds them: dropped, and the configuration saved once.
        var old = Policy(P("offlineModeEnabled", "false", true), P("skipIntro", "true", true));
        old.Users.Add(new UserPolicyOverride { UserId = Alice, Settings = { P("streamystatsURL", "x", false) } });
        var (cleaned, changed) = SettingsRules.Reconcile(Schema, old, SettingsRules.ToLegacy(old));
        Assert.True(changed);
        Assert.Equal(new[] { "skipIntro" }, cleaned.Settings.Select(s => s.Key));
        Assert.Empty(cleaned.Users);
        Assert.False(SettingsRules.Reconcile(Schema, cleaned, SettingsRules.ToLegacy(cleaned)).Changed);
    }

    [Fact]
    public void PayloadLimits()
    {
        Assert.Null(SettingsRules.CheckPayload(new Dictionary<string, string?> { ["a"] = new string('x', 4097) }));
        Assert.NotNull(SettingsRules.CheckPayload(Enumerable.Range(0, 513).ToDictionary(i => $"k{i}", _ => (string?)"v")));
    }

    [Fact]
    public void ASettingTooLongIsSkippedNotTheRest()
    {
        var kept = SettingsRules.WithinLimits(new Dictionary<string, string?>
        {
            ["a"] = new string('x', 4096), ["b"] = new string('x', 4097), [new string('k', 129)] = "v", [""] = "v", ["c"] = null
        });
        Assert.Equal(new[] { "a", "c" }, kept.Keys.OrderBy(k => k));
    }

    [Fact]
    public void ReplaceKeepsASettingThatWasSentButSkipped()
    {
        var stored = new Dictionary<string, string?> { ["skipIntro"] = "true", ["skipForwardSeconds"] = "30" };
        var sent = new Dictionary<string, string?> { ["skipIntro"] = "false", ["skipForwardSeconds"] = new string('9', 5000) };
        var incoming = SettingsRules.FilterToSchema(Schema, SettingsRules.WithinLimits(sent), SettingsSchema.ScopeSynced);
        Assert.True(SettingsRules.Apply(Schema, stored, incoming, new HashSet<string>(), replace: true, platform: "iOS", sent: sent.Keys));
        Assert.Equal("false", stored["skipIntro"]);
        Assert.Equal("30", stored["skipForwardSeconds"]);
    }

    [Fact]
    public void ReconcileMigratesKeepsAndReimports()
    {
        var legacy = new List<LockedSetting> { new() { Key = "skipIntro", IsLocked = true, PrefillValue = "true" } };
        var (migrated, changed) = SettingsRules.Reconcile(Schema, null, legacy);
        Assert.True(changed);
        Assert.Equal(("skipIntro", "true", true), (migrated.Settings[0].Key, migrated.Settings[0].DefaultValue, migrated.Settings[0].Locked));

        migrated.Users.Add(new UserPolicyOverride { UserId = Alice, Settings = { P("skipIntro", null, false) } });
        var (same, unchanged) = SettingsRules.Reconcile(Schema, migrated, SettingsRules.ToLegacy(migrated));
        Assert.False(unchanged);
        Assert.Same(migrated, same);

        var editedByOldPlugin = new List<LockedSetting> { new() { Key = "skipIntro", IsLocked = false, PrefillValue = "false" } };
        var (reimported, again) = SettingsRules.Reconcile(Schema, migrated, editedByOldPlugin);
        Assert.True(again);
        Assert.False(reimported.Settings[0].Locked);
        Assert.Single(reimported.Users);
    }

    [Fact]
    public void ReconcileSettlesOnDuplicateLegacyEntries()
    {
        var legacy = new List<LockedSetting> { new() { Key = "skipIntro", PrefillValue = "true" }, new() { Key = "skipIntro", PrefillValue = "false" } };
        var (policy, _) = SettingsRules.Reconcile(Schema, null, legacy);
        var (_, changed) = SettingsRules.Reconcile(Schema, policy, SettingsRules.ToLegacy(policy));
        Assert.False(changed);
    }

    [Fact]
    public void PolicyValidation()
    {
        var existing = Policy(P("skipIntro", "false", true));
        bool Known(Guid id) => id == Alice;

        var ok = new SettingsPolicy
        {
            Settings = { P("playbackSpeed", "1.5", false), P("skipForwardSeconds", "", false), P("seerrUsername", "x", true), P("skipIntro", "true", true), P("homeShelves", "", false) },
            Users = { new UserPolicyOverride { UserId = Alice, Settings = { P("skipIntro", null, false) } } }
        };
        var (clean, error) = PolicyService.Validate(Schema, ok, existing, Known);
        Assert.Null(error);
        Assert.Equal(new[] { "playbackSpeed", "skipForwardSeconds", "skipIntro", "homeShelves" }, clean!.Settings.Select(s => s.Key));
        Assert.Null(clean.Settings[1].DefaultValue);
        Assert.Null(clean.Settings[3].DefaultValue);

        Assert.NotNull(PolicyService.Validate(Schema, Policy(P("offlineModeEnabled", "false", true)), existing, Known).Error);
        Assert.NotNull(PolicyService.Validate(Schema, Policy(P("playbackSpeed", "3", false)), existing, Known).Error);
        Assert.NotNull(PolicyService.Validate(Schema, Policy(P("homeShelves", "not json", false)), existing, Known).Error);

        Assert.NotNull(PolicyService.Validate(Schema, Policy(P("newUnknown", "1", true)), existing, Known).Error);
        Assert.NotNull(PolicyService.Validate(Schema, Policy(P("videoQuality", "Max\u0001", true)), existing, Known).Error);

        var lockedNoValue = PolicyService.Validate(Schema, new SettingsPolicy
        {
            Settings = { P("skipIntro", null, true) },
            Users = { new UserPolicyOverride { UserId = Alice, Settings = { P("skipIntro", null, true) } } }
        }, existing, Known).Policy!;
        Assert.Equal("false", lockedNoValue.Settings[0].DefaultValue);
        Assert.Null(lockedNoValue.Users[0].Settings[0].DefaultValue);
        Assert.NotNull(PolicyService.Validate(Schema, Policy(P("skipIntro", "yes", true)), existing, Known).Error);
        Assert.NotNull(PolicyService.Validate(Schema, Policy(P("skipForwardSeconds", "12", true)), existing, Known).Error);
        Assert.Null(PolicyService.Validate(Schema, Policy(P("skipForwardSeconds", "15", true)), existing, Known).Error);
        Assert.NotNull(PolicyService.Validate(Schema, Policy(P("skipIntro", "true", true), P("skipIntro", "false", true)), existing, Known).Error);
        Assert.NotNull(PolicyService.Validate(Schema, new SettingsPolicy { Users = { new UserPolicyOverride { UserId = Bob } } }, existing, Known).Error);
    }

    [Theory]
    [InlineData("  Living Room  ", "Living Room", false)]
    [InlineData("", "", false)]
    [InlineData("Bad\nName", null, true)]
    public void DeviceNames(string input, string? expected, bool fails)
    {
        var (name, error) = SettingsRules.CleanName(input);
        Assert.Equal(fails, error is not null);
        Assert.Equal(expected, name);
        Assert.NotNull(SettingsRules.CleanName(new string('n', 65)).Error);
    }
}
