using Microsoft.AspNetCore.Hosting;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Pulse.Infrastructure;

/// <summary>
/// Singleton store for generated files (PDF, Word, Excel, …).
/// Files are written to <c>App_Data/GeneratedFiles/</c> on disk so they survive
/// server restarts. Metadata (filename, expiry) is stored as a companion
/// <c>.meta.json</c> file. Default TTL is 30 days.
/// </summary>
public sealed class DownloadTokenStore
{
    private sealed record FileMeta(string FileName, DateTime ExpiresAt);

    private readonly string _dir;
    private readonly ConcurrentDictionary<string, FileMeta> _index = new();

    private static readonly TimeSpan DefaultTtl = TimeSpan.FromDays(30);
    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = false };

    public DownloadTokenStore(IWebHostEnvironment env)
    {
        _dir = Path.Combine(env.ContentRootPath, "App_Data", "GeneratedFiles");
        Directory.CreateDirectory(_dir);
        LoadFromDisk();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Persists <paramref name="data"/> to disk and returns a download token.</summary>
    public string Store(byte[] data, string fileName, TimeSpan? ttl = null)
    {
        var token     = Guid.NewGuid().ToString("N");
        var expiresAt = DateTime.UtcNow.Add(ttl ?? DefaultTtl);
        var meta      = new FileMeta(fileName, expiresAt);

        File.WriteAllBytes(DataPath(token), data);
        File.WriteAllText(MetaPath(token), JsonSerializer.Serialize(meta, JsonOpts));

        _index[token] = meta;
        return token;
    }

    /// <summary>
    /// Returns the file for <paramref name="token"/>, or <c>null</c> when the
    /// token is unknown, expired, or the file was deleted from disk.
    /// </summary>
    public (byte[] Data, string FileName)? Get(string token)
    {
        if (!_index.TryGetValue(token, out var meta)) return null;

        if (meta.ExpiresAt < DateTime.UtcNow)
        {
            Purge(token);
            return null;
        }

        var path = DataPath(token);
        if (!File.Exists(path))
        {
            _index.TryRemove(token, out _);
            return null;
        }

        return (File.ReadAllBytes(path), meta.FileName);
    }

    // ── Startup loader ────────────────────────────────────────────────────────

    private void LoadFromDisk()
    {
        foreach (var metaFile in Directory.GetFiles(_dir, "*.meta.json"))
        {
            try
            {
                var token = Path.GetFileNameWithoutExtension(
                                Path.GetFileNameWithoutExtension(metaFile)); // strip .meta.json

                var meta = JsonSerializer.Deserialize<FileMeta>(
                               File.ReadAllText(metaFile), JsonOpts);
                if (meta is null) continue;

                if (meta.ExpiresAt < DateTime.UtcNow || !File.Exists(DataPath(token)))
                {
                    TryDelete(metaFile);
                    TryDelete(DataPath(token));
                    continue;
                }

                _index[token] = meta;
            }
            catch { /* skip corrupt entries */ }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Purge(string token)
    {
        _index.TryRemove(token, out _);
        TryDelete(DataPath(token));
        TryDelete(MetaPath(token));
    }

    private string DataPath(string token) => Path.Combine(_dir, token + ".bin");
    private string MetaPath(string token) => Path.Combine(_dir, token + ".meta.json");

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
