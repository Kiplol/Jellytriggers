using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellytriggers.Services;

/// <summary>
/// Stores each Jellyfin user's DTDD API key, on disk, with light obfuscation.
/// </summary>
/// <remarks>
/// <para>
/// API keys are AES-GCM encrypted with a key generated on first run and
/// persisted to <c>jellytriggers.keystore</c> alongside the data file. Anyone
/// with filesystem access to the plugin's data directory can still recover
/// the keys — this isn't a vault. The goal is just to keep them out of logs,
/// out of GET responses, and out of casual file viewers.
/// </para>
/// <para>
/// Thread-safety: a single <see cref="SemaphoreSlim"/> serializes reads and
/// writes against the JSON file. Throughput is low (one save per user key
/// edit), so contention isn't a concern.
/// </para>
/// </remarks>
public sealed class UserKeyStore
{
    private const int NonceBytes = 12;          // AES-GCM standard nonce length
    private const int TagBytes = 16;            // AES-GCM tag length
    private const int AesKeyBytes = 32;         // 256-bit key

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _dataFilePath;
    private readonly string _keyFilePath;
    private readonly ILogger<UserKeyStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private byte[]? _aesKey;
    private ConcurrentDictionary<Guid, string>? _encryptedByUser;

    public UserKeyStore(IApplicationPaths applicationPaths, ILogger<UserKeyStore> logger)
    {
        var dir = Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellytriggers");
        Directory.CreateDirectory(dir);
        _dataFilePath = Path.Combine(dir, "userkeys.json");
        _keyFilePath = Path.Combine(dir, "jellytriggers.keystore");
        _logger = logger;
    }

    /// <summary>True if the user has a DTDD API key configured.</summary>
    public bool HasKey(Guid userId)
    {
        EnsureLoaded();
        return _encryptedByUser!.ContainsKey(userId);
    }

    /// <summary>Returns the user's DTDD API key, or <c>null</c> if not configured.</summary>
    public string? GetKey(Guid userId)
    {
        EnsureLoaded();
        if (!_encryptedByUser!.TryGetValue(userId, out var encryptedB64))
        {
            return null;
        }

        try
        {
            return Decrypt(encryptedB64);
        }
        catch (CryptographicException ex)
        {
            _logger.LogWarning(ex, "Failed to decrypt DTDD key for user {UserId}", userId);
            return null;
        }
    }

    /// <summary>Sets or replaces the user's DTDD API key.</summary>
    public void SetKey(Guid userId, string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key must not be empty.", nameof(apiKey));
        }

        EnsureLoaded();
        var encrypted = Encrypt(apiKey.Trim());
        _encryptedByUser![userId] = encrypted;
        Save();
    }

    /// <summary>Removes the user's DTDD API key, if any.</summary>
    public bool DeleteKey(Guid userId)
    {
        EnsureLoaded();
        if (_encryptedByUser!.TryRemove(userId, out _))
        {
            Save();
            return true;
        }

        return false;
    }

    /// <summary>Snapshot of every user id we have a key for. Used for cache invalidation sweeps.</summary>
    public IReadOnlyCollection<Guid> ListUsersWithKeys()
    {
        EnsureLoaded();
        return new List<Guid>(_encryptedByUser!.Keys);
    }

    // ---- internals ----------------------------------------------------------

    private void EnsureLoaded()
    {
        if (_encryptedByUser != null && _aesKey != null)
        {
            return;
        }

        _gate.Wait();
        try
        {
            if (_aesKey == null)
            {
                _aesKey = LoadOrCreateAesKey();
            }

            if (_encryptedByUser == null)
            {
                _encryptedByUser = LoadDataFile();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private byte[] LoadOrCreateAesKey()
    {
        if (File.Exists(_keyFilePath))
        {
            var existing = File.ReadAllBytes(_keyFilePath);
            if (existing.Length == AesKeyBytes)
            {
                return existing;
            }

            _logger.LogWarning("Keystore file at {Path} had unexpected length {Length}; regenerating.",
                _keyFilePath, existing.Length);
        }

        var fresh = RandomNumberGenerator.GetBytes(AesKeyBytes);
        File.WriteAllBytes(_keyFilePath, fresh);
        return fresh;
    }

    private ConcurrentDictionary<Guid, string> LoadDataFile()
    {
        if (!File.Exists(_dataFilePath))
        {
            return new ConcurrentDictionary<Guid, string>();
        }

        try
        {
            using var stream = File.OpenRead(_dataFilePath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
                      ?? new Dictionary<string, string>();
            var dict = new ConcurrentDictionary<Guid, string>();
            foreach (var kv in raw)
            {
                if (Guid.TryParse(kv.Key, out var userId))
                {
                    dict[userId] = kv.Value;
                }
            }

            return dict;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning(ex, "Failed to read user-keys file at {Path}; starting empty.", _dataFilePath);
            return new ConcurrentDictionary<Guid, string>();
        }
    }

    private void Save()
    {
        _gate.Wait();
        try
        {
            var dict = new Dictionary<string, string>(_encryptedByUser!.Count);
            foreach (var kv in _encryptedByUser)
            {
                dict[kv.Key.ToString("D")] = kv.Value;
            }

            var tmp = _dataFilePath + ".tmp";
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, dict, _jsonOptions);
            }

            File.Move(tmp, _dataFilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Encrypt(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];

        using (var aes = new AesGcm(_aesKey!, TagBytes))
        {
            aes.Encrypt(nonce, plainBytes, cipher, tag);
        }

        // layout: nonce | tag | ciphertext
        var combined = new byte[NonceBytes + TagBytes + cipher.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceBytes);
        Buffer.BlockCopy(tag, 0, combined, NonceBytes, TagBytes);
        Buffer.BlockCopy(cipher, 0, combined, NonceBytes + TagBytes, cipher.Length);

        return Convert.ToBase64String(combined);
    }

    private string Decrypt(string encryptedB64)
    {
        var combined = Convert.FromBase64String(encryptedB64);
        if (combined.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("Encrypted blob is too short.");
        }

        var nonce = new byte[NonceBytes];
        var tag = new byte[TagBytes];
        var cipher = new byte[combined.Length - NonceBytes - TagBytes];
        Buffer.BlockCopy(combined, 0, nonce, 0, NonceBytes);
        Buffer.BlockCopy(combined, NonceBytes, tag, 0, TagBytes);
        Buffer.BlockCopy(combined, NonceBytes + TagBytes, cipher, 0, cipher.Length);

        var plain = new byte[cipher.Length];
        using (var aes = new AesGcm(_aesKey!, TagBytes))
        {
            aes.Decrypt(nonce, cipher, tag, plain);
        }

        return Encoding.UTF8.GetString(plain);
    }
}
