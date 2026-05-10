using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellytriggers.Api;
using Jellyfin.Plugin.Jellytriggers.Api.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytriggers.Services;

/// <summary>
/// Resolves a Jellyfin <see cref="BaseItem"/> to its DTDD media id.
/// </summary>
/// <remarks>
/// DTDD has no "find by TMDB id" endpoint — only <c>/dddsearch?q=&lt;title&gt;</c>.
/// So the algorithm is: search by title, then pick the result whose own
/// TMDB / IMDb id matches the Jellyfin item's, falling back to exact
/// name+year. Resolution is cached on disk; positive hits live for ~30 days,
/// confirmed misses for ~7 days (configurable on the admin page).
/// </remarks>
public sealed class TriggerLookupService
{
    private readonly DoesTheDogDieClient _client;
    private readonly TriggerCache _cache;
    private readonly ILogger<TriggerLookupService> _logger;

    public TriggerLookupService(
        DoesTheDogDieClient client,
        TriggerCache cache,
        ILogger<TriggerLookupService> logger)
    {
        _client = client;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Returns the DTDD media id for <paramref name="item"/>, or <c>null</c>
    /// if the item isn't on DTDD (or the lookup fails). The result is cached.
    /// </summary>
    public async Task<int?> ResolveAsync(
        BaseItem item,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var config = Plugin.Instance?.Configuration;
        var hitTtl = config?.ResolutionCacheHitTtlDays ?? 30;
        var missTtl = config?.ResolutionCacheMissTtlDays ?? 7;

        if (_cache.TryGetResolution(item.Id, hitTtl, missTtl, out var cached))
        {
            return cached;
        }

        var resolved = await DoLookupAsync(item, apiKey, cancellationToken).ConfigureAwait(false);
        if (resolved.HasValue)
        {
            _cache.SetResolution(item.Id, resolved.Value);
        }
        else
        {
            _cache.MarkResolutionMissing(item.Id);
        }

        return resolved;
    }

    private async Task<int?> DoLookupAsync(
        BaseItem item,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var title = item.Name;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var tmdbId = TryParseInt(item.GetProviderId(MetadataProvider.Tmdb));
        var imdbId = item.GetProviderId(MetadataProvider.Imdb);
        var year = item.ProductionYear;

        var search = await _client.SearchAsync(title, apiKey, cancellationToken).ConfigureAwait(false);
        if (search?.Items is not { Count: > 0 } results)
        {
            _logger.LogDebug(
                "Jellytriggers: DTDD search returned no items for {Title} ({Year})",
                title,
                year);
            return null;
        }

        // 1. TMDB id match — most reliable.
        if (tmdbId.HasValue)
        {
            var byTmdb = results.FirstOrDefault(r => r.TmdbId == tmdbId.Value && IsMovie(r));
            if (byTmdb != null)
            {
                return byTmdb.Id;
            }
        }

        // 2. IMDb id match — IMDb ids are stable and rare to clash, so a
        //    lower-cased exact compare is fine.
        if (!string.IsNullOrEmpty(imdbId))
        {
            var byImdb = results.FirstOrDefault(
                r => string.Equals(r.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase) && IsMovie(r));
            if (byImdb != null)
            {
                return byImdb.Id;
            }
        }

        // 3. Title + year fallback — only if the admin allows it. Useful for
        //    items missing both TMDB and IMDb ids; risk is wrong-movie collisions
        //    when titles aren't unique.
        if (Plugin.Instance?.Configuration?.AllowTitleSearchFallback == true && year.HasValue)
        {
            var byTitleYear = results.FirstOrDefault(r =>
                IsMovie(r)
                && string.Equals(r.Name, title, StringComparison.OrdinalIgnoreCase)
                && ParseYear(r.ReleaseYear) == year);
            if (byTitleYear != null)
            {
                return byTitleYear.Id;
            }
        }

        _logger.LogDebug(
            "Jellytriggers: no DTDD match for {Title} ({Year}) tmdb={Tmdb} imdb={Imdb} amongst {Count} candidates.",
            title,
            year,
            tmdbId,
            imdbId,
            results.Count);
        return null;
    }

    private static bool IsMovie(DtddItem item)
    {
        // "Movie" is what DTDD returns; we ignore Series, Books, Video Games.
        return string.Equals(item.ItemTypeName, "Movie", StringComparison.OrdinalIgnoreCase);
    }

    private static int? TryParseInt(string? raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static int? ParseYear(string? raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
