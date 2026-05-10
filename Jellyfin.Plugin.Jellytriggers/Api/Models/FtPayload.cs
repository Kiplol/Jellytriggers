using Newtonsoft.Json;

namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>
/// The payload File Transformation passes to our index.html callback.
/// Defined as a plain DTO — not JObject — so Newtonsoft can safely
/// deserialize it across AssemblyLoadContext boundaries without any
/// type-identity mismatch between the FT and Jellytriggers load contexts.
/// </summary>
public sealed class FtPayload
{
    [JsonProperty("contents")]
    public string? Contents { get; set; }
}
