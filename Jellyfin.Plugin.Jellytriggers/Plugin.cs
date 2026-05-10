using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Jellytriggers.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Jellytriggers;

/// <summary>
/// Jellytriggers plugin entry point.
/// </summary>
/// <remarks>
/// Exposes the admin config page to Jellyfin and stashes a static
/// <see cref="Instance"/> so other classes (controllers, services) can
/// reach the live <see cref="PluginConfiguration"/> without having to
/// thread it through DI.
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "Jellytriggers";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("58f13d24-a724-409e-a998-f2154ba986d4");

    /// <inheritdoc />
    public override string Description =>
        "Surfaces personalized Does The Dog Die content warnings on the movie detail page.";

    /// <summary>Convenience pointer to the running plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
            },
        };
    }
}
