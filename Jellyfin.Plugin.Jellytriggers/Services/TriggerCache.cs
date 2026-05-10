using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Jellytriggers.Api.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytriggers.Services;

/// <summary>
/// Two on-disk caches:
/// <list type="bullet">
///   <item><b>Resolution cache</b> — Jellyfin item id → DTDD media id (or "not found").</item>
///   <item><b>Pane cache</b> — (DTDD media id, Jellyfin user id) → filtered pane payload.</item>
/// </list>
/// </summary>
public sealed class TriggerCache
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _resolutionPath;
    private readonly string _panePath;
    private readonly ILogger<TriggerCache> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ConcurrentDictionary<Guid, ResolutionEntry>? _resolution;
    private ConcurrentDictionary<string, PaneEntry>? _pane;

    public TriggerCache(IApplicationPaths applicationPaths, ILogger<TriggerCache> logger)
    {
        var dir = Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellytriggers");
        Directory.CreateDirectory(dir);
        _resolutionPath = Path.Combine(dir, "resolution-cache.json");
        _panePath = Path.Combine(dir, "pane-cache.json");
        _logger = logger;
    }

    // ---- Resolution cache (Jellyfin item -> DTDD media id) -----------------

    /// <summary>
    /// Try to read a cached lookup result. Returns <c>(false, _)</c> for cache miss
    /// or expired entries; <c>(true, dtddMediaId)</c> when we have a positive hit;
    /// <c>(true, null)</c> when we previously confirmed the item is not on DTDD
    /// (the negative cache).
    /// </summary>
    public bool TryGetResolution(Guid itemId, int hitTtlDays, int missTtlDays, out int? dtddMediaId)
    {
        EnsureLoaded();
        dtddMediaId = null;

        if (!_resolution!.TryGetValue(itemId, out var entry))
        {
            return false;
        }

        var ttl = entry.DtddMediaId.HasValue
            ? TimeSpan.FromDays(Math.Max(1, hitTtlDays))
            : TimeSpan.FromDays(Math.Max(1, missTtlDays));

        if (DateTimeOffset.UtcNow - entry.CreatedUtc > ttl)
        {
            return false;
        }

        dtddMediaId = entry.DtddMediaId;
        return true;
    }

    public void SetResolution(Guid itemId, int dtddMediaId)
    {
        EnsureLoaded();
        _resolution![itemId] = new ResolutionEntry
        {
            DtddMediaId = dtddMediaId,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        SaveResolution();
    }

    public void MarkResolutionMissing(Guid itemId)
    {
        EnsureLoaded();
        _resolution![itemId] = new ResolutionEntry
        {
            DtddMediaId = null,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        SaveResolution();
    }

    /// <summary>
    /// Wipe every entry from the resolution cache. Useful after a code fix or
    /// API change that may have written incorrect "not found" entries.
    /// Returns the number of entries cleared.
    /// </summary>
    public int ClearResolutionCache()
    {
        EnsureLoaded();
        var count = _resolution!.Count;
        _resolution.Clear();
        SaveResolution();
        return count;
    }

    // ---- Pane cache ((DTDD media id, user id) -> rendered payload) ---------

    public bool TryGetPane(int dtddMediaId, Guid userId, int ttlHours, out PanePayload? payload)
    {
        EnsureLoaded();
        payload = null;

        var key = PaneKey(dtddMediaId, userId);
        if (!_pane!.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - entry.CreatedUtc > TimeSpan.FromHours(Math.Max(1, ttlHours)))
        {
            return false;
        }

        payload = entry.Payload;
        return true;
    }

    public void SetPane(int dtddMediaId, Guid userId, PanePayload payload)
    {
        EnsureLoaded();
        var key = PaneKey(dtddMediaId, userId);
        _pane![key] = new PaneEntry
        {
            Payload = payload,
            CreatedUtc = DateTimeOffset.UtcNow,
        };
        SavePane();
    }

    public void InvalidatePaneEntry(int dtddMediaId, Guid userId)
    {
        EnsureLoaded();
        if (_pane!.TryRemove(PaneKey(dtddMediaId, userId), out _))
        {
            SavePane();
        }
    }

    /// <summary>Drop every cached pane entry belonging to a particular user.</summary>
    public int InvalidatePaneForUser(Guid userId)
    {
        EnsureLoaded();
        var suffix = ":" + userId.ToString("D");
        var toRemove = _pane!.Keys.Where(k => k.EndsWith(suffix, StringComparison.Ordinal)).ToList();

        foreach (var k in toRemove)
        {
            _pane.TryRemove(k, out _);
        }

        if (toRemove.Count > 0)
        {
            SavePane();
        }

        return toRemove.Count;
    }

    // ---- internals ----------------------------------------------------------

    private static string PaneKey(int dtddMediaId, Guid userId)
        => string.Format(CultureInfo.InvariantCulture, "{0}:{1:D}", dtddMediaId, userId);

    private void EnsureLoaded()
    {
        if (_resolution != null && _pane != null)
        {
            return;
        }

        _gate.Wait();
        try
        {
            _resolution ??= LoadOrEmpty<Dictionary<string, ResolutionEntry>>(_resolutionPath)
                .ToConcurrentByGuid();
            _pane ??= new ConcurrentDictionary<string, PaneEntry>(
                LoadOrEmpty<Dictionary<string, PaneEntry>>(_panePath));
        }
        finally
        {
            _gate.Release();
        }
    }

    private T LoadOrEmpty<T>(string path) where T : class, new()
    {
        if (!File.Exists(path))
        {
            return new T();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream) ?? new T();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Failed to load {Path}; starting empty.", path);
            return new T();
        }
    }

    private void SaveResolution() => SaveDictionary(_resolutionPath, _resolution!.ToDictionary(kv => kv.Key.ToString("D"), kv => kv.Value));

    private void SavePane() => SaveDictionary(_panePath, new Dictionary<string, PaneEntry>(_pane!));

    private void SaveDictionary<T>(string path, Dictionary<string, T> data)
    {
        _gate.Wait();
        try
        {
            var tmp = path + ".tmp";
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, data, _jsonOptions);
            }

            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Failed to write cache file {Path}", path);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- entry shapes ------------------------------------------------------

    private sealed class ResolutionEntry
    {
        /// <summary>Null means "we looked, it isn't on DTDD" — the negative cache.</summary>
        public int? DtddMediaId { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }
    }

    private sealed class PaneEntry
    {
        public PanePayload Payload { get; set; } = new();

        public DateTimeOffset CreatedUtc { get; set; }
    }
}

internal static class CacheConversionExtensions
{
    /// <summary>
    /// Convert a <c>Dictionary&lt;string, ResolutionEntry&gt;</c> (the on-disk shape,
    /// keyed by Guid string) into the in-memory map keyed by <see cref="Guid"/>.
    /// </summary>
    public static ConcurrentDictionary<Guid, T> ToConcurrentByGuid<T>(
        this Dictionary<string, T> source)
    {
        var dict = new ConcurrentDictionary<Guid, T>();
        foreach (var kv in source)
        {
            if (Guid.TryParse(kv.Key, out var id))
            {
                dict[id] = kv.Value;
            }
        }

        return dict;
    }
}
