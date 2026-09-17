using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using AtlasMail.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.UnitTests;

/// <summary>
/// Unit del restore §48: verifica que el restore escribe DE VUELTA los archivos del message
/// store con sus claves originales (no solo el snapshot DB), y que rechaza hash corrupto.
/// </summary>
public class BackupRestoreTests
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atlasmail_bkp_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Restore_rechaza_hash_snapshot_corrupto()
    {
        Directory.CreateDirectory(_dir);
        using var ctx = new FakeAppDbContext("bkp2_" + Guid.NewGuid().ToString("N"));
        var svc = new BackupService(ctx, new RecordingStore(), new NoopAudit(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupService>.Instance, _dir);
        var backup = await svc.CreateBackupAsync("test");

        // corromper el snapshot
        string snap = Path.Combine(_dir, backup.BackupId, "db-snapshot.json");
        await File.WriteAllTextAsync(snap, "{ corrupto");
        var res = await svc.RestoreAsync(backup.BackupId, "test");
        Assert.False(res.Success);
        Assert.Contains("Hash", res.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingStore : IMessageStore
    {
        public List<string> Keys { get; } = new();
        private readonly Dictionary<string, byte[]> _d = new();
        public void Clear() { _d.Clear(); }
        public Task<string> SaveAsync(string messageId, byte[] rawMime, CancellationToken ct = default)
        { var k = "k" + _d.Count; _d[k] = rawMime; return Task.FromResult(k); }
        public Task<byte[]> ReadAsync(string storeKey, CancellationToken ct = default) => Task.FromResult(_d.GetValueOrDefault(storeKey, Array.Empty<byte>()));
        public Task<bool> DeleteAsync(string storeKey, CancellationToken ct = default) => Task.FromResult(_d.Remove(storeKey));
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