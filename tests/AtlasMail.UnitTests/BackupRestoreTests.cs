using System.IO;
using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using AtlasMail.Application.Services;
using AtlasMail.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.UnitTests;

/// <summary>
/// Unit del restore §48/§seguridad: el restore valida TODO primero (hash + claves) y restaura el
/// message store ANTES de la DB, de modo que un fallo (hash corrupto, storeKey inválido/traversal,
/// write-back fallido) NUNCA deja la DB publicada apuntando a un store parcial.
/// </summary>
public class BackupRestoreTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atlasmail_bkp_" + Guid.NewGuid().ToString("N"));

    private static BackupService NewService(IApplicationDbContext ctx, IMessageStore store, string root) =>
        new(ctx, store, new NoopAudit(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupService>.Instance, root);

    /// <summary>Helper: crea un backup de un store con una clave y MIME conocidos (DB snapshot vacío:
    /// el foco de estos tests es el store; la restauración de la DB se cubre en los tests de integración).</summary>
    private async Task<(BackupService svc, string key, byte[] mime)> MakeBackupWithStoreAsync(IMessageStore store, string root, byte[] mime)
    {
        var ctx = new FakeAppDbContext("bkp_store_" + Guid.NewGuid().ToString("N"));
        var svc = NewService(ctx, store, root);
        string key = await store.SaveAsync("msg-1", mime);
        var backup = await svc.CreateBackupAsync("test");
        return (svc, key, mime);
    }

    [Fact]
    public async Task Restore_rechaza_hash_snapshot_corrupto_sin_modificar()
    {
        Directory.CreateDirectory(_dir);
        using var ctx = new FakeAppDbContext("bkp2_" + Guid.NewGuid().ToString("N"));
        var store = new RecordingStore();
        var svc = NewService(ctx, store, _dir);
        var backup = await svc.CreateBackupAsync("test");

        // Corromper el snapshot (el store no se debe haber tocado)
        string snap = Path.Combine(_dir, backup.BackupId, "db-snapshot.json");
        await File.WriteAllTextAsync(snap, "{ corrupto");
        var res = await svc.RestoreAsync(backup.BackupId, "test");
        Assert.False(res.Success);
        Assert.Contains("Hash", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Restore_preserva_clave_original_y_mime_byte_a_byte_y_vuelve_a_leer()
    {
        Directory.CreateDirectory(_dir);
        string storeRoot = Path.Combine(Path.GetTempPath(), "atlasmail_store_" + Guid.NewGuid().ToString("N"));
        var store = new FileSystemMessageStore(storeRoot);
        byte[] mime = System.Text.Encoding.UTF8.GetBytes("Subject: Correo 1\r\n\r\nHola.\r\n");

        var (svc, key, _) = await MakeBackupWithStoreAsync(store, _dir, mime);

        // Mutar el store CON intención de romperlo: borrar el archivo y luego restaurar.
        // (Restaurar debe reponer la clave original con el MIME idéntico.)
        await store.DeleteAsync(key);
        var backupId = Directory.GetDirectories(_dir).Select(Path.GetFileName).First()!;
        var res = await svc.RestoreAsync(backupId, "test");
        Assert.True(res.Success, $"restore falló: {res.Error}");

        // CLAVE ORIGINAL preservada + MIME idéntico byte a byte + se puede volver a leer.
        var back = await store.ReadAsync(key);
        var list = await store.ListKeysAsync();
        Assert.Contains(key, list);
        Assert.True(back.SequenceEqual(mime), "el MIME restaurado debe ser idéntico byte a byte");

        try { Directory.Delete(storeRoot, true); } catch { }
    }

    [Fact]
    public async Task Restore_hash_archivo_store_corrupto_no_modifica_destino()
    {
        Directory.CreateDirectory(_dir);
        string storeRoot = Path.Combine(Path.GetTempPath(), "atlasmail_store_" + Guid.NewGuid().ToString("N"));
        var store = new FileSystemMessageStore(storeRoot);
        byte[] mime = System.Text.Encoding.UTF8.GetBytes("Subject: original\r\n\r\ndata");
        var (svc, key, _) = await MakeBackupWithStoreAsync(store, _dir, mime);

        // Corromper UN archivo del store dentro del backup (simula MIME dañado en el backup).
        string backupDir = Directory.GetDirectories(_dir).First();
        var storeFile = Directory.GetFiles(Path.Combine(backupDir, "store")).First();
        await File.WriteAllBytesAsync(storeFile, new byte[] { 0xFF, 0xFF, 0xFF });

        var before = await store.ReadAsync(key);
        var res = await svc.RestoreAsync(Path.GetFileName(backupDir), "test");
        Assert.False(res.Success);
        Assert.Contains("Hash", res.Error, StringComparison.OrdinalIgnoreCase);

        // El destino NO se modificó: la clave sigue con el MIME original del store actual.
        var after = await store.ReadAsync(key);
        Assert.True(after.SequenceEqual(before), "el almacén destino no debe modificarse ante un hash corrupto");

        try { Directory.Delete(storeRoot, true); } catch { }
    }

    [Fact]
    public async Task Restore_storeKey_invalido_no_modifica_destino()
    {
        Directory.CreateDirectory(_dir);
        var store = new CrashStore();
        byte[] mime = System.Text.Encoding.UTF8.GetBytes("Subject: x\r\n\r\ny");
        using var ctx = new FakeAppDbContext("bkp_badkey_" + Guid.NewGuid().ToString("N"));
        var d = TestData.NewDomain("badkey.local");
        ctx.Domains.Add(d);
        ctx.SaveChanges();
        var svc = NewService(ctx, store, _dir);

        // 2 claves; backup válido.
        string k1 = await store.SaveAsync("m1", mime);
        string k2 = await store.SaveAsync("m2", mime);
        var backup = await svc.CreateBackupAsync("test");

        // Forzar al manifest una clave inválida/traversal en el primer storeFile.
        string manifest = Path.Combine(_dir, backup.BackupId, "manifest.json");
        var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
        var mutable = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!;
        mutable!["storeFiles"]![0]["key"] = "__escX__/..";
        await File.WriteAllTextAsync(manifest, mutable.ToJsonString());
        store.InvalidKey = "__escX__/..";

        var res = await svc.RestoreAsync(backup.BackupId, "test");
        Assert.False(res.Success);
        Assert.Contains("inválida", res.Error, StringComparison.OrdinalIgnoreCase);

        // No publicó nada: el store conserva sus claves originales y la DB su dominio.
        Assert.True(store.Data.ContainsKey(k1) && store.Data.ContainsKey(k2));
        Assert.Equal(1, ctx.Domains.Count());
    }

    [Fact]
    public async Task Restore_fallo_writeback_no_publica_parcial()
    {
        Directory.CreateDirectory(_dir);
        var store = new CrashStore();
        byte[] mime = System.Text.Encoding.UTF8.GetBytes("Subject: z\r\n\r\nbody");
        using var ctx = new FakeAppDbContext("bkp_wb_" + Guid.NewGuid().ToString("N"));
        ctx.Domains.Add(TestData.NewDomain("wb.local"));
        ctx.SaveChanges();
        var svc = NewService(ctx, store, _dir);

        string k1 = await store.SaveAsync("m1", mime);
        string k2 = await store.SaveAsync("m2", mime);
        var backup = await svc.CreateBackupAsync("test");

        // Simular fallo de disco en el SEGUNDO write-back del store.
        store.ThrowOnSave = 2;
        var res = await svc.RestoreAsync(backup.BackupId, "test");
        Assert.False(res.Success);
        Assert.Contains("store", res.Error, StringComparison.OrdinalIgnoreCase);

        // La DB NO se publicó (sigue tal cual, sin el wipe/re-poblar del snapshot).
        Assert.Equal(1, ctx.Domains.Count());
        Assert.True(ctx.Domains.Any(x => x.Name == "wb.local"));
    }

    [Fact]
    public async Task Restore_inexistente_falla_limpio()
    {
        using var ctx = new FakeAppDbContext("bkp_no_" + Guid.NewGuid().ToString("N"));
        var res = await NewService(ctx, new RecordingStore(), _dir).RestoreAsync("noexiste", "test");
        Assert.False(res.Success);
        Assert.Contains("no encontrado", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsValidKey_es_estricto_la_clave_debe_ser_exacta()
    {
        // §3.md/Fix 3: NO se normaliza una clave inválida. La validación es estricta: una clave solo es
        // válida si YA es un nombre de archivo seguro exacto. Separar "validación" de "normalización" y
        // rechazar claves que requieran normalizar evita romper la correspondencia DB↔store (la StoreKey
        // persistida debe == nombre en disco).
        var store = new FileSystemMessageStore(Path.Combine(Path.GetTempPath(),
            "atlasmail_storekey_" + Guid.NewGuid().ToString("N")));

        // Clave legítima generada por el store (GUID hex [.eml]) → válida.
        Assert.True(store.IsValidKey(Guid.NewGuid().ToString("N")));
        Assert.True(store.IsValidKey(Guid.NewGuid().ToString("N") + ".eml"));

        // Traversal / separadores de ruta → inválidos (antes se "normalizaban" a "_").
        Assert.False(store.IsValidKey("../../etc/passwd"));
        Assert.False(store.IsValidKey("..\\..\\windows\\system32"));
        Assert.False(store.IsValidKey("../../archivo"));
        Assert.False(store.IsValidKey("a/b"));
        Assert.False(store.IsValidKey("a\\b"));

        // Componentes de ruta ".." / "." puros → inválidos.
        Assert.False(store.IsValidKey(".."));
        Assert.False(store.IsValidKey("."));
        Assert.False(store.IsValidKey("a..b")); // segmento vacío entre puntos = camino/".."
        Assert.False(store.IsValidKey("a...b"));

        // Vacías / absurdamente largas → inválidas.
        Assert.False(store.IsValidKey(""));
        Assert.False(store.IsValidKey(new string('a', 250)));
    }

    /// <summary>Store controlable para inyectar fallos: key inválida y fallo de escritura en el N-ésimo write-back.</summary>
    private sealed class CrashStore : IMessageStore
    {
        public Dictionary<string, byte[]> Data { get; } = new();
        public int ThrowOnSave { get; set; } = -1;
        public string InvalidKey { get; set; } = "";
        public int SaveCalls { get; private set; }

        public Task<string> SaveAsync(string messageId, byte[] rawMime, CancellationToken ct = default)
        { var k = "k" + Data.Count; Data[k] = rawMime; return Task.FromResult(k); }
        public Task<byte[]> ReadAsync(string storeKey, CancellationToken ct = default) => Task.FromResult(Data.GetValueOrDefault(storeKey, Array.Empty<byte>()));
        public Task<bool> DeleteAsync(string storeKey, CancellationToken ct = default) => Task.FromResult(Data.Remove(storeKey));
        public bool IsValidKey(string storeKey) => storeKey != InvalidKey;
        public Task SaveWithKeyAsync(string storeKey, byte[] rawMime, CancellationToken ct = default)
        {
            SaveCalls++;
            if (ThrowOnSave > 0 && SaveCalls >= ThrowOnSave) throw new IOException("simulated disk failure");
            Data[storeKey] = rawMime;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Data.Keys.ToList());
        public Task<long> TotalSizeAsync(CancellationToken ct = default) => Task.FromResult((long)Data.Count);
    }

    private sealed class RecordingStore : IMessageStore
    {
        public List<string> Keys { get; } = new();
        private readonly System.Collections.Generic.Dictionary<string, byte[]> _d = new();
        public void Clear() { _d.Clear(); }
        public Task<string> SaveAsync(string messageId, byte[] rawMime, CancellationToken ct = default)
        { var k = "k" + _d.Count; _d[k] = rawMime; return Task.FromResult(k); }
        public Task<byte[]> ReadAsync(string storeKey, CancellationToken ct = default) => Task.FromResult(_d.GetValueOrDefault(storeKey, Array.Empty<byte>()));
        public Task<bool> DeleteAsync(string storeKey, CancellationToken ct = default) => Task.FromResult(_d.Remove(storeKey));
        public bool IsValidKey(string storeKey) => true;
        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(_d.Keys.ToList());
        public Task<long> TotalSizeAsync(CancellationToken ct = default) => Task.FromResult((long)_d.Count);
        public Task SaveWithKeyAsync(string storeKey, byte[] rawMime, CancellationToken ct = default) { _d[storeKey] = rawMime; return Task.CompletedTask; }
    }

    private sealed class NoopAudit : IAuditService
    {
        public Task RecordAsync(string action, string? actor, string? actorId, string? ip, string? target, string? targetId, string? result, string? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditItem>> ListRecentAsync(int take = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditItem>>(new List<AuditItem>());
    }
}