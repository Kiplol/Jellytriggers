using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>One topic's votes/comments for a particular media item.</summary>
public sealed class DtddTopicItemStat
{
    [JsonPropertyName("TopicId")]
    public int TopicId { get; set; }

    /// <summary>Top-level convenience copy of <see cref="DtddTopic.DoesName"/>.</summary>
    [JsonPropertyName("doesName")]
    public string? DoesName { get; set; }

    [JsonPropertyName("yesSum")]
    public int YesSum { get; set; }

    [JsonPropertyName("noSum")]
    public int NoSum { get; set; }

    [JsonPropertyName("numComments")]
    public int NumComments { get; set; }

    [JsonPropertyName("slug")]
    public string? Slug { get; set; }

    [JsonPropertyName("paywalled")]
    public bool Paywalled { get; set; }

    /// <summary>
    /// True when the calling DTDD account has marked this topic as a favorite.
    /// This is the field we pivot on to decide what shows in the pane.
    /// </summary>
    [JsonPropertyName("isFavorite")]
    public bool IsFavorite { get; set; }

    [JsonPropertyName("topic")]
    public DtddTopic? Topic { get; set; }
}
