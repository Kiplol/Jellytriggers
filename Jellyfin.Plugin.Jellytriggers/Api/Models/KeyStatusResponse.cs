namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>Response shape for <c>GET /Plugins/Jellytriggers/key</c>.</summary>
/// <remarks>Does not include the key itself — only whether one is set.</remarks>
public sealed class KeyStatusResponse
{
    public bool HasKey { get; set; }
}
