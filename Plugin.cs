using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
        KeepWhatWasOn();
    }

    /// <summary>
    /// Features are opt-in, but an upgrade mustn't switch off what a server was already using:
    /// Pick Together was on by default in 0.5.0, and hiding titles and signed commands had no
    /// switch before. Whatever has no saved setting yet is set from whether it was in use.
    /// </summary>
    private void KeepWhatWasOn()
    {
        try
        {
            var path = ConfigurationFilePath;
            var saved = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : string.Empty;
            var config = Configuration;
            var changed = false;
            if (saved.Length > 0 && !saved.Contains("<PickTogether>", StringComparison.Ordinal))
            {
                config.PickTogether.Enabled = true;
                changed = true;
            }

            if (!saved.Contains("<HiddenEnabled>", StringComparison.Ordinal))
            {
                var hidden = System.IO.Path.Combine(DataFolderPath, "hidden");
                if (System.IO.Directory.Exists(hidden) && System.IO.Directory.EnumerateFiles(hidden).Any())
                {
                    config.HiddenEnabled = true;
                    changed = true;
                }
            }

            var security = saved.IndexOf("<Security>", StringComparison.Ordinal);
            var securityEnd = security < 0 ? -1 : saved.IndexOf("</Security>", security, StringComparison.Ordinal);
            var hasSwitch = security >= 0 && securityEnd > security && saved[security..securityEnd].Contains("<Enabled>", StringComparison.Ordinal);
            if (!hasSwitch && System.IO.File.Exists(System.IO.Path.Combine(DataFolderPath, "signing.json")))
            {
                config.Security.Enabled = true;
                changed = true;
            }

            if (changed)
            {
                SaveConfiguration();
            }
        }
        catch (Exception)
        {
            // Never stop the plugin loading over this; the switches can still be set by hand.
        }
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
                Name = "ArcfinSync",
                DisplayName = "Arcfin Sync",
                EnableInMainMenu = true,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", ns)
            },
            new PluginPageInfo
            {
                Name = "ArcfinSync.js",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.ArcfinSync.js", ns)
            }
        };
    }
}
