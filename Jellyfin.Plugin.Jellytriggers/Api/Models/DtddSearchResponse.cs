using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>Wrapper around DTDD's <c>/dddsearch</c> result list.</summary>
public sealed class DtddSearchResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<DtddItem> Items { get; set; } = System.Array.Empty<DtddItem>();
}
