using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>A media item (movie/show/etc.) as DTDD describes it.</summary>
/// <remarks>
/// DTDD uses two different casings for the TMDB id depending on endpoint
/// (<c>tmdbid</c> on /dddsearch, <c>tmdbId</c> on /media). We only care
/// about the canonical camelCase variant; the search endpoint setter is
/// modeled separately if we need it.
/// </remarks>
public sealed class DtddItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("releaseYear")]
    public string? ReleaseYear { get; set; }

    [JsonPropertyName("itemTypeName")]
    public string? ItemTypeName { get; set; }

    [JsonPropertyName("tmdbId")]
    public int? TmdbIdCamel { get; set; }

    [JsonPropertyName("tmdbid")]
    public int? TmdbIdLower { get; set; }

    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }

    /// <summary>TMDB id, regardless of which casing the endpoint used.</summary>
    [JsonIgnore]
    public int? TmdbId => TmdbIdCamel ?? TmdbIdLower;
}
