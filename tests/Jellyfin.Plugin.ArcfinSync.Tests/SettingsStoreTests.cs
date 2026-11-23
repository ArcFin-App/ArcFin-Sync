using System.Text.Json;
using Jellyfin.Plugin.ArcfinSync.Api;
using Xunit;

namespace Jellyfin.Plugin.ArcfinSync.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "arcfin-sync-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void ReadsTheOldValuesFileAndWritesBesideIt()
    {
        var user = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_root, "users"));
        var oldFile = Path.Combine(_root, "users", $"{user}.json");
        File.WriteAllText(oldFile, """{"skipIntro":"true","unknownOld":"x"}""");

        var store = new SettingsStore(_root);
        Assert.Equal("true", store.LoadValues(user)["skipIntro"]);

        store.WithUser(user, files =>
        {
            files.Values["skipIntro"] = "false";
            files.ValuesChanged();
            files.Devices["../../evil"] = new DeviceRecord { DeviceId = "../../evil", JellyfinDeviceName = "Phone" };
            files.DevicesChanged();
            return 0;
        });

        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(oldFile))!;
        Assert.Equal("false", values["skipIntro"]);
        Assert.Equal("x", values["unknownOld"]);
        Assert.True(File.Exists(Path.Combine(_root, "devices", $"{user}.json")));
        Assert.Equal(new[] { $"{user}.json" }, Directory.GetFiles(Path.Combine(_root, "devices")).Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "users"), "*.tmp"));
        Assert.Equal("Phone", store.LoadDevices(user)["../../evil"].DisplayName);
        Assert.Equal(1, store.CountDevices());
    }

    [Fact]
    public void RefusesTheEmptyUser()
    {
        Assert.Throws<ArgumentException>(() => new SettingsStore(_root).LoadValues(Guid.Empty));
    }

    [Fact]
    public void EmbeddedSchemaLoads()
    {
        var schema = SettingsSchema.Embedded;
        Assert.NotEmpty(schema.Settings);
        Assert.All(schema.Settings, s => Assert.Contains(s.Scope, new[] { "synced", "device" }));
        Assert.All(schema.Settings, s => Assert.False(string.IsNullOrEmpty(s.Title)));
    }
}
