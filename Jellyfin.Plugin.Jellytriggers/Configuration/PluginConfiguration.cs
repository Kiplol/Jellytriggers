using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Jellytriggers.Configuration;

/// <summary>
/// Server-wide settings persisted by Jellyfin to its plugin config XML.
/// Per-user data (DTDD API keys, per-user cache entries) lives elsewhere —
/// see <c>UserKeyStore</c> and <c>TriggerCache</c>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>How long to remember a successful Jellyfin-item → DTDD-media-id resolution.</summary>
    public int ResolutionCacheHitTtlDays { get; set; } = 30;

    /// <summary>How long to remember a failed lookup (negative cache).</summary>
    public int ResolutionCacheMissTtlDays { get; set; } = 7;

    /// <summary>How long to cache a (movie, user) filtered pane payload.</summary>
    public int PaneCacheTtlHours { get; set; } = 24;

    /// <summary>Base URL of the DTDD API. Almost never needs changing.</summary>
    public string DtddBaseUrl { get; set; } = "https://www.doesthedogdie.com";

    /// <summary>
    /// If true, an item that has no TMDB or IMDb id will fall through to
    /// a title-search lookup against DTDD. Disable to keep traffic predictable.
    /// </summary>
    public bool AllowTitleSearchFallback { get; set; } = true;
}
