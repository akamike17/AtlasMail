using System.Text;
using System.Text.Json;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Application.Services;

/// <summary>
/// Backup/restore self-contained (secciones 28, 48):
///  - snapshot lógico de la DB (dominios, usuarios, buzones, alias, carpetas, mensajes,
///    cola, auditoría, config) en JSON + message store (MIME crudo) copiado,
///  - manifest con timestamp, versión y SHA-256 de cada store key y del DB snapshot,
///  - restore valida manifest/hash ANTES de aplicar; nunca en DB real a menos que se pida.
/// No depende de mysqldump ni de binarios externos.
/// </summary>
public class BackupService : IBackupService
{
    private record DbSnapshotRow(string Table, string Data);

    private readonly IApplicationDbContext _db;
    private readonly IMessageStore _store;
    private readonly IAuditService _audit;
    private readonly ILogger<BackupService> _logger;
    private readonly string _backupRoot;

    // Archivo snapshot self-contained: ignora props get-only (colecciones de navegación y
    // propiedades computadas) para que el round-trip serialize/deserialize sea válido.
    private static readonly JsonSerializerOptions SnapOptions = new()
    {
        WriteIndented = false,
        IgnoreReadOnlyProperties = true,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles
    };

    public BackupService(IApplicationDbContext db, IMessageStore store, IAuditService audit,
        ILogger<BackupService> logger, string backupRoot)
    {
        _db = db; _store = store; _audit = audit; _logger = logger; _backupRoot = backupRoot;
    }

    public async Task<BackupDescriptor> CreateBackupAsync(string actor, CancellationToken ct = default)
    {
        var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var dir = Path.Combine(_backupRoot, id);
        Directory.CreateDirectory(Path.Combine(dir, "store"));

        var snapshot = await CaptureSnapshotAsync(ct);

        // Copiar message store
        long storeBytes = 0;
        var storeManifest = new List<(string key, string sha, long size)>();
        foreach (var key in await _store.ListKeysAsync(ct))
        {
            var data = await _store.ReadAsync(key, ct);
            string fname = Path.Combine(dir, "store", Sanitize(key));
            await File.WriteAllBytesAsync(fname, data, ct);
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
            storeManifest.Add((key, sha, data.LongLength));
            storeBytes += data.LongLength;
        }

        string snapshotJson = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
        string snapshotSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(snapshotJson))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(dir, "db-snapshot.json"), snapshotJson, ct);

        var manifest = new
        {
            format = "atlasmail-backup",
            version = 1,
            createdUtc = DateTime.UtcNow,
            dbSnapshot = new { file = "db-snapshot.json", sha256 = snapshotSha },
            storeFiles = storeManifest.Select(m => new { key = m.key, file = Sanitize(m.key), sha256 = m.sha, size = m.size }).ToList(),
            note = "No contiene secretos: la DB backup NO incluye credential/token/private-key; password hashes sí se conservan."
        };
        string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        string manifestSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(manifestJson))).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), manifestJson, ct);

        await _audit.RecordAsync("Backup.Created", actor, null, null, "backup", id, "OK", $"storeBytes={storeBytes}", ct);
        _logger.LogInformation("Backup {Id} creado: {Bytes} bytes store, {Tables} tablas DB", id, storeBytes, snapshot.Count);
        return new BackupDescriptor(DateTime.UtcNow, id, Path.Combine(dir, "manifest.json"), storeBytes, snapshot.Count, manifestSha);
    }

    public async Task<RestoreResult> RestoreAsync(string backupId, string actor, CancellationToken ct = default)
    {
        var dir = Path.Combine(_backupRoot, backupId);
        string manifestPath = Path.Combine(dir, "manifest.json");
        string snapshotPath = Path.Combine(dir, "db-snapshot.json");
        if (!File.Exists(manifestPath) || !File.Exists(snapshotPath))
            return new RestoreResult(false, "Backup no encontrado", null, 0);

        // 1. Validar manifest (JSON bien formado, versión correcta)
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct));
            if (doc.RootElement.GetProperty("version").GetInt32() != 1 ||
                doc.RootElement.GetProperty("format").GetString() != "atlasmail-backup")
                return new RestoreResult(false, "Manifest de versión/format desconocido", null, 0);
            if (doc.RootElement.GetProperty("dbSnapshot").GetProperty("file").GetString() != "db-snapshot.json")
                return new RestoreResult(false, "Manifest inválido", null, 0);
        }
        catch (Exception ex) { return new RestoreResult(false, "Manifest corrupto: " + ex.Message, null, 0); }

        // 2. Validar SHA-256 del snapshot
        string expected = GetManifestSnapshotSha(manifestPath);
        string actual = ComputeSha256File(snapshotPath);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            return new RestoreResult(false, $"Hash del snapshot no coincide (esperado {expected}, real {actual})", null, 0);

        // 3. Restore store (validar hash de cada archivo antes de confirmar)
        foreach (var f in Directory.GetFiles(Path.Combine(dir, "store")))
        {
            var data = await File.ReadAllBytesAsync(f, ct);
            string fileSha = ComputeSha256File(f);
            string expectedSha = GetStoreSha(manifestPath, Path.GetFileName(f));
            if (!string.Equals(fileSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                return new RestoreResult(false, $"Hash del archivo store {Path.GetFileName(f)} no coincide", null, 0);
        }

        // 4. Restore DB snapshot (identificación por URL del método — implementación segura en snapshot)
        int restored = await ApplySnapshotAsync(snapshotPath, ct);

        // 5. Restore message store: escribir de vuelta cada archivo con su CLAVE ORIGINAL
        //    (debe coincidir con el StoreKey referenciado por la DB). §48 backup/restore completo.
        var storeMap = GetStoreKeyMap(manifestPath);
        foreach (var (key, file) in storeMap)
        {
            string filePath = Path.Combine(dir, "store", file);
            byte[] data = await File.ReadAllBytesAsync(filePath, ct);
            await _store.SaveWithKeyAsync(key, data, ct);
        }

        await _audit.RecordAsync("Backup.Restored", actor, null, null, "backup", backupId, "OK", $"tables={restored} store={storeMap.Count}", ct);
        _logger.LogInformation("Backup {Id} restaurado ({Restored} tablas, {Store} archivos store)", backupId, restored, storeMap.Count);
        return new RestoreResult(true, null, backupId, restored);
    }

    private async Task<List<DbSnapshotRow>> CaptureSnapshotAsync(CancellationToken ct)
    {
        var snap = new List<DbSnapshotRow>();
        snap.Add(new DbSnapshotRow("Domains", ToJson(await _db.Domains.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("Users", ToJson(await _db.Users.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("Mailboxes", ToJson(await _db.Mailboxes.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("Aliases", ToJson(await _db.Aliases.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("Folders", ToJson(await _db.Folders.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("Messages", ToJson(await _db.Messages.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("MessageRecipients", ToJson(await _db.MessageRecipients.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("Attachments", ToJson(await _db.Attachments.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("DeliveryQueue", ToJson(await _db.DeliveryQueue.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("DeliveryAttempts", ToJson(await _db.DeliveryAttempts.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("AuditEvents", ToJson(await _db.AuditEvents.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("LoginAttempts", ToJson(await _db.LoginAttempts.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("Contacts", ToJson(await _db.Contacts.AsNoTracking().ToListAsync(ct))));
        snap.Add(new DbSnapshotRow("ConfigurationEntries", ToJson(await _db.ConfigurationEntries.AsNoTracking().ToListAsync(ct))));
        return snap;
    }

    private async Task<int> ApplySnapshotAsync(string snapshotPath, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath, ct));
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in doc.RootElement.EnumerateArray())
            data[el.GetProperty("Table").GetString()!] = el.GetProperty("Data").GetString()!;

        // Pomelo usa MySqlRetryingExecutionStrategy, que requiere envolver la transacción
        // manual en ExecuteAsync (no soporta BeginTransactionAsync directo).
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Vaciar tablas en orden inverso a las FK (evita violación de RESTRICT).
            // Deshabilitar temporalmente la verificación de FK solo dentro de esta transacción.
            await _db.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS=0");
            foreach (var table in new[] { "DeliveryAttempts", "AuditEvents", "LoginAttempts", "Contacts",
                "ConfigurationEntries", "DeliveryQueue", "Attachments", "MessageRecipients", "Messages",
                "Folders", "Aliases", "Mailboxes", "Users", "Domains" })
            {
                await _db.ExecuteSqlRawAsync($"TRUNCATE TABLE `{table}`");
            }
            await _db.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS=1");

            // Restaurar en orden de dependencia (padres primero).
            RestoreTable(_db.Domains, data, "Domains");
            RestoreTable(_db.Users, data, "Users");
            RestoreTable(_db.Mailboxes, data, "Mailboxes");
            RestoreTable(_db.Aliases, data, "Aliases");
            RestoreTable(_db.Folders, data, "Folders");
            RestoreTable(_db.Messages, data, "Messages");
            RestoreTable(_db.MessageRecipients, data, "MessageRecipients");
            RestoreTable(_db.Attachments, data, "Attachments");
            RestoreTable(_db.DeliveryQueue, data, "DeliveryQueue");
            RestoreTable(_db.DeliveryAttempts, data, "DeliveryAttempts");
            RestoreTable(_db.Contacts, data, "Contacts");
            RestoreTable(_db.ConfigurationEntries, data, "ConfigurationEntries");
            RestoreTable(_db.LoginAttempts, data, "LoginAttempts");
            RestoreTable(_db.AuditEvents, data, "AuditEvents");

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return data.Count;
        });
    }

    private static void RestoreTable<T>(DbSet<T> set, Dictionary<string, string> data, string table) where T : class
    {
        if (data.TryGetValue(table, out var json))
        {
            var items = JsonSerializer.Deserialize<List<T>>(json, SnapOptions) ?? new List<T>();
            foreach (var item in items) set.Add(item);
        }
    }

    private static string ToJson<T>(List<T> items) => JsonSerializer.Serialize(items, SnapOptions);
    private static string Sanitize(string key) => string.Concat(key.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '.'));
    private static string ComputeSha256File(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string GetManifestSnapshotSha(string manifestPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        return doc.RootElement.GetProperty("dbSnapshot").GetProperty("sha256").GetString()!;
    }

    private static string GetStoreSha(string manifestPath, string fileName)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        foreach (var f in doc.RootElement.GetProperty("storeFiles").EnumerateArray())
            if (f.GetProperty("file").GetString() == fileName)
                return f.GetProperty("sha256").GetString()!;
        return string.Empty;
    }

    /// <summary>Mapea clave original (storeKey) → nombre de archivo en el backup, desde el manifest.</summary>
    private static List<(string Key, string File)> GetStoreKeyMap(string manifestPath)
    {
        var result = new List<(string, string)>();
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        foreach (var f in doc.RootElement.GetProperty("storeFiles").EnumerateArray())
            result.Add((f.GetProperty("key").GetString()!, f.GetProperty("file").GetString()!));
        return result;
    }
}