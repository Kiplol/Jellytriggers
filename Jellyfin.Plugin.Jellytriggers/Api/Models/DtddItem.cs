using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Jellytriggers.Api.Models;

/// <summary>A media item (movie/show/etc.) as DTDD describes it.</summary>
/// <remarks>
/// DTDD uses two different casings for the TMDB id depending on endpoint
/// (<c>tmdbid</c> on /dddsearch, <c>tmdbId</c> on /media). Both are handled
/// by a single property because the deserializer runs with
/// <c>PropertyNameCaseInsensitive = true</c> — having two properties whose
/// names differ only in case causes a collision and an
/// <see cref="System.InvalidOperationException"/> at runtime.
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
    public int? TmdbId { get; set; }

    [JsonPropertyName("imdbId")]
    public string? ImdbId { get; set; }
}
