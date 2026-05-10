using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>Shape of <c>/media/{id}</c> with a personal API key.</summary>
public sealed class DtddMediaResponse
{
    [JsonPropertyName("item")]
    public DtddItem? Item { get; set; }

    [JsonPropertyName("topicItemStats")]
    public IReadOnlyList<DtddTopicItemStat> TopicItemStats { get; set; } =
        System.Array.Empty<DtddTopicItemStat>();
}
