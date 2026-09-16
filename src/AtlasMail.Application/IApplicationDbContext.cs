using AtlasMail.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.Application;

/// <summary>
/// Abstracción del contexto de persistencia expuesta a Application. Evita que
/// Application dependa del proveedor de MySQL/Pomelo; Infrastructure lo implementa.
/// </summary>
public interface IApplicationDbContext
{
    DbSet<MailDomain> Domains { get; }
    DbSet<User> Users { get; }
    DbSet<Mailbox> Mailboxes { get; }
    DbSet<Alias> Aliases { get; }
    DbSet<Folder> Folders { get; }
    DbSet<Message> Messages { get; }
    DbSet<MessageRecipient> MessageRecipients { get; }
    DbSet<Attachment> Attachments { get; }
    DbSet<DeliveryQueueItem> DeliveryQueue { get; }
    DbSet<DeliveryAttempt> DeliveryAttempts { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<LoginAttempt> LoginAttempts { get; }
    DbSet<Contact> Contacts { get; }
    DbSet<ConfigurationEntry> ConfigurationEntries { get; }

    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    /// <summary>Ejecuta SQL raw (usado por backup/restore). Implementado en Infrastructure.</summary>
    Task<int> ExecuteSqlRawAsync(string sql, CancellationToken ct = default);
}