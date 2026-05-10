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
    /// <param name="item">The Jellyfin item to resolve.</param>
    /// <param name="apiKey">The calling user's DTDD API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="forceRefresh">
    /// When <c>true</c>, bypass the resolution cache and always hit the DTDD
    /// search API. Use this when the caller has explicitly requested a full
    /// re-resolve (e.g. the pane's Refresh button), so a previously-cached
    /// "not found" entry doesn't block a fresh attempt.
    /// </param>
    public async Task<int?> ResolveAsync(
        BaseItem item,
        string apiKey,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        ArgumentNullException.ThrowIfNull(item);

        var config = Plugin.Instance?.Configuration;
        var hitTtl = config?.ResolutionCacheHitTtlDays ?? 30;
        var missTtl = config?.ResolutionCacheMissTtlDays ?? 7;

        if (!forceRefresh && _cache.TryGetResolution(item.Id, hitTtl, missTtl, out var cached))
        {
            _logger.LogInformation(
                "Jellytriggers: resolution cache hit for \"{Title}\" → dtddId={DtddId} (forceRefresh={Force})",
                item.Name, cached, forceRefresh);
            return cached;
        }

        _logger.LogInformation(
            "Jellytriggers: cache miss for \"{Title}\" (forceRefresh={Force}) — going to DTDD",
            item.Name, forceRefresh);

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

        _logger.LogInformation(
            "Jellytriggers: resolving \"{Title}\" ({Year}) — Jellyfin tmdb={Tmdb} imdb={Imdb}",
            title, year, tmdbId, imdbId);

        var search = await _client.SearchAsync(title, apiKey, cancellationToken).ConfigureAwait(false);
        if (search?.Items is not { Count: > 0 } results)
        {
            _logger.LogInformation(
                "Jellytriggers: DTDD search returned no results for \"{Title}\"", title);
            return null;
        }

        _logger.LogInformation(
            "Jellytriggers: DTDD returned {Count} candidates for \"{Title}\":", results.Count, title);
        foreach (var r in results)
        {
            _logger.LogInformation(
                "  dtdd={DtddId} type={Type} name=\"{Name}\" year={Year} tmdb={Tmdb} imdb={Imdb}",
                r.Id, r.ItemTypeName, r.Name, r.ReleaseYear, r.TmdbId, r.ImdbId);
        }

        // 1. TMDB id match — most reliable. ID match is authoritative; skip type
        //    filter because DTDD returns type=null for most entries.
        if (tmdbId.HasValue)
        {
            var byTmdb = results.FirstOrDefault(r => r.TmdbId == tmdbId.Value);
            if (byTmdb != null)
            {
                _logger.LogInformation(
                    "Jellytriggers: matched \"{Title}\" via TMDB id {Tmdb} → dtdd={DtddId}",
                    title, tmdbId, byTmdb.Id);
                return byTmdb.Id;
            }

            _logger.LogInformation(
                "Jellytriggers: no TMDB match for tmdb={Tmdb} ({Count} candidates)",
                tmdbId, results.Count);
        }

        // 2. IMDb id match. Same reasoning — skip type filter.
        if (!string.IsNullOrEmpty(imdbId))
        {
            var byImdb = results.FirstOrDefault(
                r => string.Equals(r.ImdbId, imdbId, StringComparison.OrdinalIgnoreCase));
            if (byImdb != null)
            {
                _logger.LogInformation(
                    "Jellytriggers: matched \"{Title}\" via IMDb id {Imdb} → dtdd={DtddId}",
                    title, imdbId, byImdb.Id);
                return byImdb.Id;
            }

            _logger.LogInformation(
                "Jellytriggers: no IMDb match for imdb={Imdb}", imdbId);
        }

        // 3. Title + year fallback — only if the admin allows it.
        //    Accept null type because DTDD does not reliably populate it.
        if (Plugin.Instance?.Configuration?.AllowTitleSearchFallback == true && year.HasValue)
        {
            var byTitleYear = results.FirstOrDefault(r =>
                IsMovieOrUnknown(r)
                && string.Equals(r.Name, title, StringComparison.OrdinalIgnoreCase)
                && ParseYear(r.ReleaseYear) == year);
            if (byTitleYear != null)
            {
                _logger.LogInformation(
                    "Jellytriggers: matched \"{Title}\" via title+year fallback → dtdd={DtddId}",
                    title, byTitleYear.Id);
                return byTitleYear.Id;
            }
        }

        _logger.LogInformation(
            "Jellytriggers: no match found for \"{Title}\" ({Year}) tmdb={Tmdb} imdb={Imdb} — {Count} candidates above",
            title, year, tmdbId, imdbId, results.Count);
        return null;
    }

    private static bool IsMovieOrUnknown(DtddItem item)
    {
        // Accept "Movie" or null/empty — DTDD does not reliably populate ItemTypeName.
        // Explicit non-movie types (Series, Book, VideoGame, etc.) are excluded.
        return string.IsNullOrEmpty(item.ItemTypeName)
            || string.Equals(item.ItemTypeName, "Movie", StringComparison.OrdinalIgnoreCase);
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
