using System.Text.RegularExpressions;
using AtlasMail.Application.Abstractions;

namespace AtlasMail.Infrastructure.Storage;

/// <summary>
/// Implementación local en filesystem de IMessageStore (sección 1).
/// Cada mensaje se guarda como un archivo. Metadatos viven en MySQL.
/// Preparado para reemplazar por ObjectStorageMessageStore sin tocar Application.
/// </summary>
public sealed class FileSystemMessageStore : IMessageStore
{
    private readonly string _root;
    private readonly object _lock = new();

    public FileSystemMessageStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    private string Resolve(string key)
    {
        var safe = Regex.Replace(key, @"[^a-zA-Z0-9._-]", "_");
        // Evita path traversal
        var path = Path.GetFullPath(Path.Combine(_root, safe));
        if (!path.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Clave de almacenamiento inválida");
        return path;
    }

    public Task<string> SaveAsync(string messageId, byte[] rawMime, CancellationToken ct = default)
    {
        string key = Guid.NewGuid().ToString("N") + (string.IsNullOrWhiteSpace(messageId) ? "" : ".eml");
        string path = Resolve(key);
        lock (_lock) File.WriteAllBytes(path, rawMime);
        return Task.FromResult(key);
    }

    public Task<byte[]> ReadAsync(string storeKey, CancellationToken ct = default)
    {
        string path = Resolve(storeKey);
        lock (_lock)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Mensaje no encontrado en el store", storeKey);
            return Task.FromResult(File.ReadAllBytes(path));
        }
    }

    public Task<bool> DeleteAsync(string storeKey, CancellationToken ct = default)
    {
        lock (_lock)
        {
            string path = Resolve(storeKey);
            if (File.Exists(path)) { File.Delete(path); return Task.FromResult(true); }
            return Task.FromResult(false);
        }
    }

    public Task SaveWithKeyAsync(string storeKey, byte[] rawMime, CancellationToken ct = default)
    {
        lock (_lock) File.WriteAllBytes(Resolve(storeKey), rawMime);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<string>>(
                Directory.GetFiles(_root).Select(f => Path.GetFileName(f) ?? f).ToList());
    }

    public Task<long> TotalSizeAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(Directory.GetFiles(_root).Sum(f => new FileInfo(f).Length));
    }
}