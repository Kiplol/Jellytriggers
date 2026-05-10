namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>Request shape for <c>POST /Plugins/Jellytriggers/key</c>.</summary>
public sealed class SetKeyRequest
{
    public string ApiKey { get; set; } = string.Empty;
}
