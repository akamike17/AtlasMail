using System.Text.RegularExpressions;
using AtlasMail.Application.Abstractions;

namespace AtlasMail.Infrastructure.Storage;

/// <summary>
/// Implementación local en filesystem de IMessageStore (sección 1).
/// Cada mensaje se guarda como un archivo. Metadatos viven en MySQL.
/// Preparado para reemplazar por ObjectStorageMessageStore sin tocar Application.
/// Escribas atómicas (§48/§seguridad): nunca se escribe el archivo final directo; se escribe
/// a un temporal en el MISMO filesystem y se publica con rename atómico. Ante una interrupción
/// el MIME nunca queda truncado como archivo "válido" de la clave.
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

    public bool IsValidKey(string storeKey)
    {
        try { Resolve(storeKey); return true; }
        catch { return false; }
    }

    public Task<string> SaveAsync(string messageId, byte[] rawMime, CancellationToken ct = default)
    {
        string key = Guid.NewGuid().ToString("N") + (string.IsNullOrWhiteSpace(messageId) ? "" : ".eml");
        WriteAtomic(Resolve(key), rawMime);
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
        // 1. Validar/Resolve la clave SIEMPRE (lanza InvalidOperationException si es traversal).
        string path = Resolve(storeKey);
        // 2. Escribir temporal en el mismo filesystem y publicar por rename atómico.
        WriteAtomic(path, rawMime);
        return Task.CompletedTask;
    }

    /// <summary>Escribe <paramref name="data"/> de forma atómica: fichero temporal + flush a disco
    /// + rename (reemplazo) sobre la ruta final. Si algo falla a medias, el archivo de la clave
    /// nunca queda truncado: se borra el temporal y la ruta final retiene su contenido previo.</summary>
    private void WriteAtomic(string path, byte[] data)
    {
        lock (_lock)
        {
            string tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(flushToDisk: true);
                }
                // Rename atómico (reemplaza) en el MISMO volumen — el lector nunca ve bytes a medias.
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(tmp)) { try { File.Delete(tmp); } catch { /* best effort */ } }
            }
        }
    }

    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<string>>(
                Directory.GetFiles(_root)
                    .Select(f => Path.GetFileName(f) ?? f)
                    // excluir temporales de escritura atómica
                    .Where(f => !f.Contains(".tmp."))
                    .ToList());
    }

    public Task<long> TotalSizeAsync(CancellationToken ct = default)
    {
        lock (_lock)
            return Task.FromResult(Directory.GetFiles(_root).Where(f => !Path.GetFileName(f).Contains(".tmp.")).Sum(f => new FileInfo(f).Length));
    }
}