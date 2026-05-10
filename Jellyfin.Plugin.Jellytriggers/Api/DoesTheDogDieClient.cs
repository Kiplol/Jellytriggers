using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.Jellytriggers.Api.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytriggers.Api;

/// <summary>
/// Typed HTTP client for the Does The Dog Die API.
/// </summary>
/// <remarks>
/// We do NOT bake the API key into the client because it's per-user — each
/// caller passes the relevant Jellyfin viewer's stored DTDD key, and DTDD
/// returns responses personalized to that key (notably the <c>isFavorite</c>
/// flag on every topic).
/// </remarks>
public sealed class DoesTheDogDieClient
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DoesTheDogDieClient> _logger;

    public DoesTheDogDieClient(
        IHttpClientFactory httpClientFactory,
        ILogger<DoesTheDogDieClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the full <c>/media/{id}</c> payload for the given DTDD media id,
    /// using <paramref name="apiKey"/> so the response is personalized to that user.
    /// </summary>
    /// <returns>The parsed payload, or <c>null</c> if DTDD returns 404 or auth fails.</returns>
    public async Task<DtddMediaResponse?> GetMediaAsync(
        int dtddMediaId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var baseUrl = GetBaseUrl();
        var url = string.Format(CultureInfo.InvariantCulture, "{0}/media/{1}", baseUrl, dtddMediaId);
        return await SendJsonAsync<DtddMediaResponse>(url, apiKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches DTDD by title via <c>/dddsearch?q=</c>.
    /// </summary>
    public async Task<DtddSearchResponse?> SearchAsync(
        string query,
        string apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var baseUrl = GetBaseUrl();
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/dddsearch?q={1}",
            baseUrl,
            HttpUtility.UrlEncode(query));
        return await SendJsonAsync<DtddSearchResponse>(url, apiKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> SendJsonAsync<T>(
        string url,
        string apiKey,
        CancellationToken cancellationToken)
        where T : class
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogDebug("DTDD call skipped: no API key for caller.");
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Add("X-API-KEY", apiKey);

        var http = _httpClientFactory.CreateClient(NamedHttpClients.Default);

        try
        {
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _logger.LogDebug("DTDD 404 for {Url}", url);
                return null;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized
                || response.StatusCode == HttpStatusCode.Forbidden)
            {
                _logger.LogWarning(
                    "DTDD rejected key (HTTP {Status}) for {Url}", (int)response.StatusCode, url);
                return null;
            }

            if (response.StatusCode == (HttpStatusCode)429)
            {
                _logger.LogWarning("DTDD rate-limit (HTTP 429) for {Url}", url);
                return null;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content
                .ReadFromJsonAsync<T>(_jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "DTDD request failed for {Url}", url);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "DTDD response was not valid JSON for {Url}", url);
            return null;
        }
    }

    private static string GetBaseUrl()
    {
        var configured = Plugin.Instance?.Configuration?.DtddBaseUrl;
        return string.IsNullOrWhiteSpace(configured)
            ? "https://www.doesthedogdie.com"
            : configured.TrimEnd('/');
    }
}

/// <summary>Names we use for <see cref="IHttpClientFactory"/> entries.</summary>
internal static class NamedHttpClients
{
    public const string Default = "Jellytriggers";
}
