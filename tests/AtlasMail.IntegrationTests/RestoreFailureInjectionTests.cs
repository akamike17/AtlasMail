using System.Security.Cryptography;
using System.Text;
using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using AtlasMail.Application.Services;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using AtlasMail.Infrastructure.Persistence;
using AtlasMail.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.IntegrationTests;

/// <summary>
/// E2E del restore atómico contra MySQL REAL (spec §48/§49 + remediación 3.md): inyecta un fallo
/// DURANTE la restauración de la DB (después de que el message store ya se publicó) y verifica que
/// la compensación devuelve AMBOS a su estado anterior — DB original intacta y store original intacto.
/// Esto demuestra la propiedad que una transacción SQL no puede cubrir por sí sola: la atomicidad
/// DB + filesystem como unidad de publicación sobre MySQL real.
/// </summary>
public class RestoreFailureInjectionTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atlasgt_restore_" + Guid.NewGuid().ToString("N"));

    private sealed class DbWrapper : IApplicationDbContext
    {
        private readonly AtlasMailDbContext _inner;
        // Lanza a partir del N-ésimo SaveChangesAsync durante el apply (simula un fallo de DB real a mitad
        // del restore). -1 = nunca lanzar.
        public int ThrowOnSave = -1;
        private int _saves;
        public DbWrapper(AtlasMailDbContext inner) => _inner = inner;

        public DbSet<MailDomain> Domains => _inner.Domains;
        public DbSet<User> Users => _inner.Users;
        public DbSet<Mailbox> Mailboxes => _inner.Mailboxes;
        public DbSet<Alias> Aliases => _inner.Aliases;
        public DbSet<Folder> Folders => _inner.Folders;
        public DbSet<Message> Messages => _inner.Messages;
        public DbSet<MessageRecipient> MessageRecipients => _inner.MessageRecipients;
        public DbSet<Attachment> Attachments => _inner.Attachments;
        public DbSet<DeliveryQueueItem> DeliveryQueue => _inner.DeliveryQueue;
        public DbSet<DeliveryAttempt> DeliveryAttempts => _inner.DeliveryAttempts;
        public DbSet<AuditEvent> AuditEvents => _inner.AuditEvents;
        public DbSet<LoginAttempt> LoginAttempts => _inner.LoginAttempts;
        public DbSet<Contact> Contacts => _inner.Contacts;
        public DbSet<ConfigurationEntry> ConfigurationEntries => _inner.ConfigurationEntries;
        public DbSet<BlockedSender> BlockedSenders => _inner.BlockedSenders;
        public DbSet<Calendar> Calendars => _inner.Calendars;
        public DbSet<CalendarEvent> CalendarEvents => _inner.CalendarEvents;
        public DbSet<CalendarEventAttendee> CalendarEventAttendees => _inner.CalendarEventAttendees;
        public DbSet<DistributionList> DistributionLists => _inner.DistributionLists;
        public DbSet<DistributionListMember> DistributionListMembers => _inner.DistributionListMembers;
        public DatabaseFacade Database => _inner.Database;
        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            _saves++;
            if (ThrowOnSave > 0 && _saves == ThrowOnSave)
                throw new InvalidOperationException("fallo inyectado durante el apply del snapshot DB");
            return _inner.SaveChangesAsync(ct);
        }
        public Task<int> ExecuteSqlRawAsync(string sql, CancellationToken ct = default) => _inner.ExecuteSqlRawAsync(sql, ct);
        public void ClearChangeTracker() => _inner.ClearChangeTracker();
    }

    [Fact]
    public async Task Restore_que_falla_en_DB_se_revierte_y_deja_DB_y_store_originales()
    {
        var factory = new AtlasMailFactory();
        try
        {
            Directory.CreateDirectory(_root);
            // El store y el admin client usan la BD/almacén TEMPORAL del factory (MySQL real).
            var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();
            var dbRoot = Path.Combine(_root, "store");
            var store = new FileSystemMessageStore(dbRoot);

            string k1 = await store.SaveAsync("m1", Encoding.UTF8.GetBytes("MIME-A"));
            // Estado inicial real en MySQL: dominio orig.local
            using (var setupScope = scopeFactory.CreateScope())
            {
                var real = setupScope.ServiceProvider.GetRequiredService<AtlasMailDbContext>();
                real.Domains.Add(new MailDomain
                {
                    Name = "orig.local", Enabled = true, PlusAddressingEnabled = true,
                    MaxMailboxQuotaBytes = 1024 * 1024 * 1024, CatchAllEnabled = false
                });
                await real.SaveChangesAsync();
            }

            // Backup del estado A (dominio orig.local + store k1=MIME-A)
            var backupRoot = Path.Combine(_root, "backups");
            string backupId;
            using (var scope = scopeFactory.CreateScope())
            {
                var real = scope.ServiceProvider.GetRequiredService<AtlasMailDbContext>();
                var svc = new BackupService(real, store, new NoopAudit(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupService>.Instance, backupRoot);
                var bkp = await svc.CreateBackupAsync("role:admin");
                backupId = bkp.BackupId;
            }

            // Estado actual cambia a B: dominio nuevo.local en DB + store k1=MIME-B (simula actividad real).
            byte[] mimeB = Encoding.UTF8.GetBytes("MIME-B");
            await store.SaveWithKeyAsync(k1, mimeB);
            using (var addScope = scopeFactory.CreateScope())
            {
                var real = addScope.ServiceProvider.GetRequiredService<AtlasMailDbContext>();
                real.Domains.Add(new MailDomain
                {
                    Name = "nuevo.local", Enabled = true, PlusAddressingEnabled = true,
                    MaxMailboxQuotaBytes = 1024 * 1024 * 1024, CatchAllEnabled = false
                });
                await real.SaveChangesAsync();
            }

            // Restore: el store se publica (k1=MIME-A) y luego el apply DB FALLA a mitad (wrapper lanza en
            // el SaveChanges del snapshot). La compensación debe devolver DB (nuevo.local) y store (MIME-B).
            using (var scope = scopeFactory.CreateScope())
            {
                var real = scope.ServiceProvider.GetRequiredService<AtlasMailDbContext>();
                var wrapper = new DbWrapper(real) { ThrowOnSave = 1 };
                var svc = new BackupService(wrapper, store, new NoopAudit(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BackupService>.Instance, backupRoot);
                var res = await svc.RestoreAsync(backupId, "role:admin");
                Assert.False(res.Success, "el restore debió fallar (fallo inyectado en la DB)");
                Assert.Contains("revirtió", res.Error, StringComparison.OrdinalIgnoreCase);
            }

            // VERIFICAR: DB original intacta (nuevo.local presente, NO el wipe del snapshot), store original intacto (MIME-B).
            using (var verifyScope = scopeFactory.CreateScope())
            {
                var db = verifyScope.ServiceProvider.GetRequiredService<AtlasMailDbContext>();
                var names = db.Domains.Select(d => d.Name).ToList();
                // La DB original prevalece: nuevo.local sigue presente (el snapshot NO quedó publicado).
                Assert.Contains("nuevo.local", names, StringComparer.OrdinalIgnoreCase);
            }
            var storedBack = await store.ReadAsync(k1);
            Assert.Equal(mimeB, storedBack);
        }
        finally
        {
            try { factory.Dispose(); } catch { }
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        }
    }

    private sealed class NoopAudit : IAuditService
    {
        public Task RecordAsync(string action, string? actor, string? actorId, string? ip, string? target, string? targetId, string? result, string? metadata = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditItem>> ListRecentAsync(int take = 200, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditItem>>(new List<AuditItem>());
    }

    private byte[] Sha(string data) => SHA256.HashData(Encoding.UTF8.GetBytes(data));
}