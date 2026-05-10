using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>The trigger category itself ("a dog dies", "a cat dies", ...).</summary>
public sealed class DtddTopic
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Wording for the "yes, it occurs" question, e.g. "Does the dog die".</summary>
    [JsonPropertyName("doesName")]
    public string? DoesName { get; set; }

    /// <summary>Wording for the "no" answer, e.g. "no dogs die".</summary>
    [JsonPropertyName("notName")]
    public string? NotName { get; set; }

    /// <summary>Sometimes a more specific "survives" phrasing is provided.</summary>
    [JsonPropertyName("survivesName")]
    public string? SurvivesName { get; set; }
}
