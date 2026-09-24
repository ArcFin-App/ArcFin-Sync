using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ArcfinSync.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ArcfinSync;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Arcfin Sync";

    public override Guid Id => Guid.Parse("f3a1c9d2-6b4e-4a7f-9c3d-1e2f3a4b5c6d");

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "General",
                DisplayName = "General",
                EnableInMainMenu = true,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.general.html", ns)
            },
            new PluginPageInfo
            {
                Name = "Locked Settings",
                DisplayName = "Locked Settings",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.lockedSettings.html", ns)
            },
            new PluginPageInfo
            {
                Name = "Advanced",
                DisplayName = "Advanced",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.advanced.html", ns)
            }
        };
    }
}
