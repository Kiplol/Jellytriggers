namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>Response shape for <c>POST /Plugins/Jellytriggers/refresh</c>.</summary>
public sealed class RefreshAllResponse
{
    /// <summary>How many cached pane entries we just dropped.</summary>
    public int Cleared { get; set; }
}
