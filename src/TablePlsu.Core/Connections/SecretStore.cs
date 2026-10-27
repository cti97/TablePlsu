using System.Security.Cryptography;
using System.Text.Json;
using TablePlsu.Core.IO;
using TablePlsu.Core.Paths;

namespace TablePlsu.Core.Connections;

/// <summary>
/// DB/SSH passwords and key passphrases, keyed by connection id, stored in secrets.dat as one DPAPI blob
/// (<see cref="DataProtectionScope.CurrentUser"/>). Loaded lazily on first use; every change rewrites the file atomically.
/// A corrupt or foreign-user file is treated as empty and reported via <see cref="LastError"/>.
/// </summary>
public sealed class SecretStore(AppPaths paths)
{
    private static readonly byte[] Entropy = "TablePlsu.secrets.v1"u8.ToArray();
    private readonly object _gate = new();
    private Dictionary<string, string>? _cache;

    public string? LastError { get; private set; }

    public string? Get(string connectionId, SecretKind kind)
    {
        lock (_gate) return Cache().GetValueOrDefault(Key(connectionId, kind));
    }

    /// <summary>Stores a secret; null or empty removes it.</summary>
    public void Set(string connectionId, SecretKind kind, string? value)
    {
        lock (_gate)
        {
            var cache = Cache();
            var key = Key(connectionId, kind);
            if (string.IsNullOrEmpty(value))
            {
                if (!cache.Remove(key)) return;
            }
            else
            {
                cache[key] = value;
            }
            Persist(cache);
        }
    }

    public void DeleteAll(string connectionId)
    {
        lock (_gate)
        {
            var cache = Cache();
            var prefix = connectionId + "/";
            var removed = cache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (removed.Count == 0) return;
            foreach (var k in removed) cache.Remove(k);
            Persist(cache);
        }
    }

    private static string Key(string connectionId, SecretKind kind) => connectionId + "/" + kind;

    private Dictionary<string, string> Cache()
    {
        if (_cache is not null) return _cache;
        _cache = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(paths.SecretsFile)) return _cache;
        try
        {
            var plain = Unprotect(File.ReadAllBytes(paths.SecretsFile));
            var data = JsonSerializer.Deserialize(plain, ConnectionsJsonContext.Default.DictionaryStringString);
            if (data is not null) _cache = new Dictionary<string, string>(data, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            LastError = "secrets.dat could not be read: " + ex.Message;
        }
        return _cache;
    }

    private void Persist(Dictionary<string, string> cache)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(cache, ConnectionsJsonContext.Default.DictionaryStringString);
        AtomicFile.WriteAllBytes(paths.SecretsFile, Protect(plain));
        LastError = null;
    }

    private static byte[] Protect(byte[] plain)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    }

    private static byte[] Unprotect(byte[] cipher)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        return ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
    }
}
