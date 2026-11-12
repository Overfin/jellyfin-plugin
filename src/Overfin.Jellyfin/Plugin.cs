using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Overfin.Jellyfin;

/// <summary>What Overfin tells the plugin: which key is Overfin's.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// The SHA-256 (hex) of the Jellyfin API key Overfin created for itself.
    /// Empty until Overfin sets it: then the plugin answers no one.
    /// </summary>
    public string KeyHash { get; set; } = string.Empty;
}

/// <summary>
/// Overfin's companion plugin: a few routes under /Overfin/ for Overfin
/// alone, where Jellyfin's own API falls short.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    public Plugin(IApplicationPaths paths, IXmlSerializer xml) : base(paths, xml)
    {
        Instance = this;
    }

    /// <summary>The plugin as Jellyfin loaded it.</summary>
    public static Plugin? Instance { get; private set; }

    public override string Name => "Overfin";

    public override string Description => "Overfin's companion plugin. It answers Overfin only.";

    public override Guid Id => Guid.Parse("33809b90-8925-4df0-93fd-d9b06f14ca0a");
}
