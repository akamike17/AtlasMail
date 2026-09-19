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

        // 3. FASE A (read-only): cargar y validar TODO el store del manifest — hash de cada archivo
        //    y validez de cada clave — ANTES de publicar nada. Si un MIME/hash/storeKey falla,
        //    ningún destino (BD ni store) se ha tocado todavía.
        List<(string Key, byte[] Data)> storeData;
        try
        {
            storeData = await LoadAndValidateStoreAsync(manifestPath, dir, ct);
        }
        catch (InvalidDataException ex)
        {
            return new RestoreResult(false, ex.Message, null, 0);
        }

        // 4. FASE B (staging/rollback): capturar el estado ACTUAL de la DB y del store para poder
        //    volver íntegramente al estado anterior si la publicación falla a mitad. La atomicidad
        //    real se logra por COMPENSACIÓN: captura previa + restauración en caso de fallo.
        //    NO basta la transacción SQL: el message store es filesystem (ajeno a la transacción),
        //    y en MySQL `TRUNCATE`/DDL hace implicit commit (no rollbackable). Se evita esa trampa.
        List<(string Key, byte[]? Data)> storeOriginal;
        try { storeOriginal = await CaptureStoreStateAsync(storeData, ct); }
        catch (Exception ex)
        {
            return new RestoreResult(false, "No se pudo capturar el estado actual del store (abortado sin tocar nada): " + ex.Message, null, 0);
        }
        List<DbSnapshotRow> dbOriginal;
        try { dbOriginal = await CaptureSnapshotAsync(ct); }
        catch (Exception ex)
        {
            return new RestoreResult(false, "No se pudo capturar el estado actual de la DB (abortado): " + ex.Message, null, 0);
        }

        // 5. Publicar store (escrituras atómicas por clave) y luego DB (transaccional con DELETE
        //    rollbackable). Si CUALQUIER paso falla → compensar: restaurar el estado original de
        //    AMBOS, de modo que nunca quede un estado híbrido (DB nueva + store viejo o viceversa).
        try
        {
            foreach (var (key, data) in storeData)
                await _store.SaveWithKeyAsync(key, data, ct);

            int restored = await ApplySnapshotAsync(snapshotPath, ct);

            await _audit.RecordAsync("Backup.Restored", actor, null, null, "backup", backupId, "OK", $"tables={restored} store={storeData.Count}", ct);
            _logger.LogInformation("Backup {Id} restaurado ({Restored} tablas, {Store} archivos store)", backupId, restored, storeData.Count);
            return new RestoreResult(true, null, backupId, restored);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restore falló; compensando al estado original (DB+store): backup {Id}", backupId);
            var compensationError = await CompensateRestoreAsync(storeOriginal, dbOriginal, actor, backupId, ex.Message, ct);
            var msg = compensationError ?? ("Restore falló y se revirtió al estado anterior: " + ex.Message);
            return new RestoreResult(false, msg, null, 0);
        }
    }

    /// <summary>Captura el estado ACTUAL del store únicamente para las claves que el backup va a
    /// sobrescribir (key → bytes previos, o null si la clave no existía). Es la base del rollback.</summary>
    private async Task<List<(string Key, byte[]? Data)>> CaptureStoreStateAsync(
        List<(string Key, byte[] Data)> storeData, CancellationToken ct)
    {
        var result = new List<(string, byte[]?)>();
        foreach (var (key, _) in storeData)
        {
            byte[]? prev = null;
            try { prev = await _store.ReadAsync(key, ct); } catch (FileNotFoundException) { /* no existía */ }
            result.Add((key, prev));
        }
        return result;
    }

    /// <summary>Restaura el estado original de la DB y del store capturado en FASE B. Best-effort:
    /// nunca lanza hacia el llamador: si la compensación falla, queda un fallo crítico auditado y un
    /// punto de recuperación manual, y el RestoreAsync devuelve false con el motivo.</summary>
    private async Task<string?> CompensateRestoreAsync(
        List<(string Key, byte[]? Data)> storeOriginal, List<DbSnapshotRow> dbOriginal,
        string actor, string backupId, string reason, CancellationToken ct)
    {
        // 1) Restaurar el store original (los archivos previos, o borrar los que no existían).
        try
        {
            foreach (var (key, prev) in storeOriginal)
            {
                if (prev is null) await _store.DeleteAsync(key, ct);
                else await _store.SaveWithKeyAsync(key, prev, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Compensación del STORE falló para backup {Id} (estado híbrido posible). Requiere intervención manual.", backupId);
            await _audit.RecordAsync("Backup.Restored", actor, null, null, "backup", backupId, "FAIL", $"restore falló ({reason}); compensación STORE falló: {ex.Message}", ct);
            return "Restore falló y la compensación del store no pudo completarse (estado híbrido posible, requiere intervención): " + ex.Message;
        }

        // 2) Restaurar la DB original (mismo mecanismo transaccional con DELETE, rollbackable).
        //    Limpiamos el ChangeTracker: el intento de restore que falló puede haber dejado entidades
        //    trackeadas que un SaveChanges reinsertaría por error.
        try
        {
            _db.ClearChangeTracker();
            await ApplySnapshotRowsAsync(dbOriginal, ct);
            await _audit.RecordAsync("Backup.Restored", actor, null, null, "backup", backupId, "FAIL",
                $"restore falló ({reason}); DB y store revertidos al estado anterior", ct);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Compensación de la DB falló para backup {Id}. Requiere intervención manual.", backupId);
            await _audit.RecordAsync("Backup.Restored", actor, null, null, "backup", backupId, "FAIL", $"restore falló ({reason}); compensación DB falló: {ex.Message}", ct);
            return "Restore falló y la compensación de la DB no pudo completarse (requiere intervención): " + ex.Message;
        }
    }

    private async Task<int> ApplySnapshotAsync(string snapshotPath, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath, ct));
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var el in doc.RootElement.EnumerateArray())
            data[el.GetProperty("Table").GetString()!] = el.GetProperty("Data").GetString()!;
        return await ApplySnapshotRowsAsync(ToRows(data), ct);
    }

    private static List<DbSnapshotRow> ToRows(Dictionary<string, string> data) =>
        data.Select(kv => new DbSnapshotRow(kv.Key, kv.Value)).ToList();

    /// <summary>Aplica un set de tablas de forma transaccional. Usa DELETE (DML rollbackable con
    /// InnoDB), NO TRUNCATE: `TRUNCATE` hace implicit commit en MySQL y no puede formar parte de un
    /// rollback, lo que hacía falsa la atomicidad del restore. DELETE dentro de la transacción sí
    /// se revierte con Rollback si algo falla a mitad.</summary>
    private async Task<int> ApplySnapshotRowsAsync(List<DbSnapshotRow> rows, CancellationToken ct)
    {
        var data = rows.ToDictionary(r => r.Table, r => r.Data, StringComparer.Ordinal);

        // Pomelo usa MySqlRetryingExecutionStrategy, que requiere envolver la transacción
        // manual en ExecuteAsync (no soporta BeginTransactionAsync directo).
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Vaciar tablas en orden inverso a las FK (evita violación de RESTRICT). DELETE es
            // rollbackable; en fallo, el Rollback devuelve la DB al estado anterior.
            await _db.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS=0");
            foreach (var table in new[] { "DeliveryAttempts", "AuditEvents", "LoginAttempts", "Contacts",
                "ConfigurationEntries", "DeliveryQueue", "Attachments", "MessageRecipients", "Messages",
                "Folders", "Aliases", "Mailboxes", "Users", "Domains" })
            {
                await _db.ExecuteSqlRawAsync($"DELETE FROM `{table}`");
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

    /// <summary>Carga y valida todo el message store referenciado por el manifest: cada archivo
    /// debe existir y coincidir su hash, y cada clave debe ser válida para el store (sin traversal).
    /// Lanza <see cref="InvalidDataException"/> con el motivo si algo no es restaurable. NO escribe nada.</summary>
    private async Task<List<(string Key, byte[] Data)>> LoadAndValidateStoreAsync(string manifestPath, string dir, CancellationToken ct)
    {
        var result = new List<(string, byte[])>();
        var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

        // storeFiles[].file → hash   y   storeFiles[].key → file
        var hashByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var keyByFile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in manifest.RootElement.GetProperty("storeFiles").EnumerateArray())
        {
            string file = f.GetProperty("file").GetString() ?? string.Empty;
            string key = f.GetProperty("key").GetString() ?? string.Empty;
            string sha = f.GetProperty("sha256").GetString() ?? string.Empty;
            hashByFile[file] = sha;
            keyByFile[file] = key;
        }

        foreach (var (file, sha) in hashByFile)
        {
            string filePath = Path.Combine(dir, "store", file);
            if (!File.Exists(filePath))
                throw new InvalidDataException($"Archivo store {file} del manifest no existe en el backup");

            string actual = ComputeSha256File(filePath);
            if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Hash del archivo store {file} no coincide ({actual} != {sha})");

            string key = keyByFile[file];
            if (!_store.IsValidKey(key))
                throw new InvalidDataException($"Clave de almacenamiento inválida (posible path traversal): {key}");

            result.Add((key, await File.ReadAllBytesAsync(filePath, ct)));
        }
        return result;
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
}