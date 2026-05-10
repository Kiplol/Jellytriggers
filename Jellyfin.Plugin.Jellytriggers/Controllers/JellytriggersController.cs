using System;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jellytriggers.Api;
using Jellyfin.Plugin.Jellytriggers.Api.Models;
using Jellyfin.Plugin.Jellytriggers.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytriggers.Controllers;

/// <summary>
/// All HTTP endpoints under <c>/Plugins/Jellytriggers</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/Jellytriggers")]
[Produces(MediaTypeNames.Application.Json)]
public class JellytriggersController : ControllerBase
{
    private const string JellyfinUserIdClaim = "Jellyfin-UserId";

    private readonly UserKeyStore _keys;
    private readonly TriggerCache _cache;
    private readonly DoesTheDogDieClient _client;
    private readonly TriggerLookupService _lookup;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly ILogger<JellytriggersController> _logger;

    public JellytriggersController(
        UserKeyStore keys,
        TriggerCache cache,
        DoesTheDogDieClient client,
        TriggerLookupService lookup,
        ILibraryManager library,
        IUserManager users,
        ILogger<JellytriggersController> logger)
    {
        _keys = keys;
        _cache = cache;
        _client = client;
        _lookup = lookup;
        _library = library;
        _users = users;
        _logger = logger;
    }

    // ---- triggers ----------------------------------------------------------

    /// <summary>Returns the calling user's filtered triggers for a given Jellyfin item.</summary>
    [HttpGet("triggers/{itemId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PanePayload>> GetTriggersAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var userId = GetCallingUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        SetNoCacheHeaders();
        return await BuildPaneAsync(userId.Value, itemId, useCache: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Force a fresh DTDD pull for one (item, user) pair, bypassing the pane cache.</summary>
    [HttpPost("triggers/{itemId:guid}/refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PanePayload>> RefreshTriggersAsync(
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var userId = GetCallingUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        SetNoCacheHeaders();
        return await BuildPaneAsync(userId.Value, itemId, useCache: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Wipe every cached pane entry belonging to the calling user.</summary>
    [HttpPost("refresh")]
    public ActionResult<RefreshAllResponse> RefreshAll()
    {
        var userId = GetCallingUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var cleared = _cache.InvalidatePaneForUser(userId.Value);
        return new RefreshAllResponse { Cleared = cleared };
    }

    /// <summary>
    /// Clear every entry in the resolution cache (Jellyfin item → DTDD media id).
    /// Use this after a plugin update or API issue may have written incorrect "not found"
    /// entries. Individual lookups will re-resolve on next movie page visit.
    /// </summary>
    [HttpPost("resolution-cache/clear")]
    public ActionResult<RefreshAllResponse> ClearResolutionCache()
    {
        var userId = GetCallingUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var cleared = _cache.ClearResolutionCache();
        _logger.LogInformation("Jellytriggers: resolution cache cleared ({Count} entries).", cleared);
        return new RefreshAllResponse { Cleared = cleared };
    }

    // ---- key management ---------------------------------------------------

    [HttpGet("key")]
    public ActionResult<KeyStatusResponse> GetKeyStatus()
    {
        var userId = GetCallingUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        return new KeyStatusResponse { HasKey = _keys.HasKey(userId.Value) };
    }

    [HttpPost("key")]
    public IActionResult SetKey([FromBody] SetKeyRequest request)
    {
        var userId = GetCallingUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest(new { error = "apiKey is required" });
        }

        try
        {
            _keys.SetKey(userId.Value, request.ApiKey);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        // Identity changed → previously cached panes for this user are stale.
        _cache.InvalidatePaneForUser(userId.Value);
        return NoContent();
    }

    [HttpDelete("key")]
    public IActionResult DeleteKey()
    {
        var userId = GetCallingUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        _keys.DeleteKey(userId.Value);
        _cache.InvalidatePaneForUser(userId.Value);
        return NoContent();
    }

    // ---- script bundle ----------------------------------------------------

    /// <summary>Serves the embedded JS bundle. Anonymous because browsers can't auth before fetching a script tag.</summary>
    [HttpGet("script.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public IActionResult Script()
    {
        var assembly = typeof(JellytriggersController).Assembly;
        var resourceName = $"{typeof(JellytriggersController).Namespace!.Replace(".Controllers", string.Empty, StringComparison.Ordinal)}.Web.jellytriggers.js";

        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            _logger.LogWarning("Jellytriggers: embedded resource {Name} not found.", resourceName);
            return NotFound();
        }

        // no-store: neither the browser nor any proxy should cache this.
        // The injected URL already includes ?v=<version> so the URL itself
        // changes on every release, giving a fresh fetch without caching.
        Response.Headers["Cache-Control"] = "no-store";
        return File(stream, "application/javascript");
    }

    /// <summary>Companion CSS, in case the JS chooses to import it. Anonymous, same reason as script.</summary>
    [HttpGet("style.css")]
    [AllowAnonymous]
    [Produces("text/css")]
    public IActionResult Stylesheet()
    {
        var assembly = typeof(JellytriggersController).Assembly;
        var resourceName = $"{typeof(JellytriggersController).Namespace!.Replace(".Controllers", string.Empty, StringComparison.Ordinal)}.Web.jellytriggers.css";

        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            return NotFound();
        }

        var version = typeof(JellytriggersController).Assembly.GetName().Version?.ToString() ?? "0";
        Response.Headers["ETag"] = $"\"{version}\"";
        Response.Headers["Cache-Control"] = "no-cache";
        return File(stream, "text/css");
    }

    // ---- helpers ----------------------------------------------------------

    private async Task<ActionResult<PanePayload>> BuildPaneAsync(
        Guid userId,
        Guid itemId,
        bool useCache,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Jellytriggers: BuildPane user={UserId} item={ItemId} useCache={UseCache}",
            userId, itemId, useCache);

        var item = _library.GetItemById(itemId);
        if (item == null)
        {
            _logger.LogInformation("Jellytriggers: item {ItemId} not found in library", itemId);
            return NotFound();
        }

        var apiKey = _keys.GetKey(userId);
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogInformation("Jellytriggers: no API key for user {UserId} → KeyMissing", userId);
            return new PanePayload { State = PaneState.KeyMissing };
        }

        _logger.LogInformation("Jellytriggers: key present for user {UserId}, resolving item \"{Title}\"",
            userId, item.Name);

        // Resolve Jellyfin item -> DTDD media id (its own cache).
        // When useCache is false (force refresh), also bypass the resolution cache
        // so a previously-cached "not found" entry doesn't block a fresh lookup.
        var mediaId = await _lookup.ResolveAsync(item, apiKey, cancellationToken, forceRefresh: !useCache).ConfigureAwait(false);
        if (mediaId is null)
        {
            return new PanePayload { State = PaneState.NotOnDoesTheDogDie };
        }

        // Pane cache lookup.
        var paneTtl = Plugin.Instance?.Configuration?.PaneCacheTtlHours ?? 24;
        if (useCache && _cache.TryGetPane(mediaId.Value, userId, paneTtl, out var hit) && hit != null)
        {
            return hit;
        }

        // Fresh pull.
        var media = await _client.GetMediaAsync(mediaId.Value, apiKey, cancellationToken).ConfigureAwait(false);
        if (media?.TopicItemStats is null)
        {
            return new PanePayload { State = PaneState.NotOnDoesTheDogDie, DtddMediaId = mediaId };
        }

        var favorites = media.TopicItemStats
            .Where(t => t.IsFavorite)
            .Select(ToPaneItem)
            .ToList();

        var payload = new PanePayload
        {
            DtddMediaId = mediaId,
            State = favorites.Count == 0 ? PaneState.UserHasNoFavorites : PaneState.Ok,
            Items = favorites,
        };

        _cache.SetPane(mediaId.Value, userId, payload);
        return payload;
    }

    private static PaneItem ToPaneItem(DtddTopicItemStat stat)
    {
        var topic = stat.Topic;
        var doesName = !string.IsNullOrEmpty(stat.DoesName)
            ? stat.DoesName
            : topic?.DoesName ?? string.Empty;

        return new PaneItem
        {
            TopicId = stat.TopicId,
            DoesName = doesName,
            YesSum = stat.YesSum,
            NoSum = stat.NoSum,
            NumComments = stat.NumComments,
            Slug = stat.Slug,
            Paywalled = stat.Paywalled,
            NotName = topic?.NotName,
            SurvivesName = topic?.SurvivesName,
        };
    }

    /// <summary>Prevent reverse-proxies and browsers from caching personalised API responses.</summary>
    private void SetNoCacheHeaders()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
    }

    private Guid? GetCallingUserId()
    {
        // Jellyfin's auth pipeline sets a "Jellyfin-UserId" claim. Falling back
        // to NameIdentifier or username-based lookup keeps us robust to small
        // changes between Jellyfin versions.
        var raw = User.FindFirst(JellyfinUserIdClaim)?.Value
                  ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (Guid.TryParse(raw, out var fromClaim))
        {
            return fromClaim;
        }

        var name = User.Identity?.Name;
        if (!string.IsNullOrEmpty(name))
        {
            var user = _users.GetUserByName(name);
            if (user != null)
            {
                return user.Id;
            }
        }

        return null;
    }
}
