using AtlasMail.Application;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.Infrastructure.Persistence;

public class AtlasMailDbContext : DbContext, IApplicationDbContext
{
    public AtlasMailDbContext(DbContextOptions<AtlasMailDbContext> options) : base(options) { }

    public DbSet<MailDomain> Domains => Set<MailDomain>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
    public DbSet<Alias> Aliases => Set<Alias>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageRecipient> MessageRecipients => Set<MessageRecipient>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<DeliveryQueueItem> DeliveryQueue => Set<DeliveryQueueItem>();
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<LoginAttempt> LoginAttempts => Set<LoginAttempt>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<ConfigurationEntry> ConfigurationEntries => Set<ConfigurationEntry>();
    public DbSet<BlockedSender> BlockedSenders => Set<BlockedSender>();
    public DbSet<Calendar> Calendars => Set<Calendar>();
    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
    public DbSet<CalendarEventAttendee> CalendarEventAttendees => Set<CalendarEventAttendee>();
    public DbSet<DistributionList> DistributionLists => Set<DistributionList>();
    public DbSet<DistributionListMember> DistributionListMembers => Set<DistributionListMember>();

    public Task<int> ExecuteSqlRawAsync(string sql, CancellationToken ct = default)
        => Database.ExecuteSqlRawAsync(sql, ct);

    public void ClearChangeTracker() => ChangeTracker.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MailDomain>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(255).IsRequired();
            e.HasIndex(d => d.Name).IsUnique();
        });

        modelBuilder.Entity<User>(e =>
        {
            e.Property(u => u.Username).HasMaxLength(255).IsRequired();
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.PasswordHash).HasMaxLength(1024).IsRequired();
            e.HasOne(u => u.Domain).WithMany().HasForeignKey(u => u.DomainId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Mailbox>(e =>
        {
            e.Property(m => m.LocalPart).HasMaxLength(255).IsRequired();
            e.Property(m => m.DisplayName).HasMaxLength(255);
            e.Property(m => m.PasswordHash).HasMaxLength(1024).IsRequired();
            e.HasIndex(m => new { m.DomainId, m.LocalPart }).IsUnique();
            e.HasOne(m => m.Domain).WithMany(d => d.Mailboxes).HasForeignKey(m => m.DomainId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Alias>(e =>
        {
            e.Property(a => a.LocalPart).HasMaxLength(255).IsRequired();
            e.HasIndex(a => new { a.DomainId, a.LocalPart }).IsUnique();
            e.HasOne(a => a.Domain).WithMany(d => d.Aliases).HasForeignKey(a => a.DomainId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.TargetMailbox).WithMany(m => m.Aliases).HasForeignKey(a => a.TargetMailboxId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Folder>(e =>
        {
            e.Property(f => f.Name).HasMaxLength(255).IsRequired();
            e.HasOne(f => f.Mailbox).WithMany(m => m.Folders).HasForeignKey(f => f.MailboxId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.Property(m => m.StoreKey).HasMaxLength(512).IsRequired();
            e.Property(m => m.MessageIdHeader).HasMaxLength(512);
            e.Property(m => m.SenderAddress).HasMaxLength(320).IsRequired();
            e.Property(m => m.SenderName).HasMaxLength(255);
            e.Property(m => m.Subject).HasMaxLength(1024);
            e.Property(m => m.BodyPreview).HasMaxLength(1024);
            e.Property(m => m.InReplyTo).HasMaxLength(512);
            e.HasOne(m => m.Folder).WithMany(f => f.Messages).HasForeignKey(m => m.FolderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MessageRecipient>(e =>
        {
            e.Property(r => r.Address).HasMaxLength(320).IsRequired();
            e.Property(r => r.DisplayName).HasMaxLength(255);
            e.HasOne(r => r.Message).WithMany(m => m.Recipients).HasForeignKey(r => r.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Attachment>(e =>
        {
            e.Property(a => a.FileName).HasMaxLength(512).IsRequired();
            e.Property(a => a.ContentType).HasMaxLength(255);
            e.Property(a => a.Sha256).HasMaxLength(64).IsRequired();
            e.HasOne(a => a.Message).WithMany(m => m.Attachments).HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeliveryQueueItem>(e =>
        {
            e.Property(q => q.EnvelopeFrom).HasMaxLength(320).IsRequired();
            e.Property(q => q.EnvelopeTo).HasMaxLength(320).IsRequired();
            e.Property(q => q.StoreKey).HasMaxLength(512).IsRequired();
            e.Property(q => q.RemoteServer).HasMaxLength(255);
            e.Property(q => q.ClaimedBy).HasMaxLength(255);
            e.Property(q => q.LastError).HasMaxLength(1024);
            e.Property(q => q.MessageIdHeader).HasMaxLength(512);
            e.HasMany(q => q.AttemptHistory).WithOne(a => a.QueueItem).HasForeignKey(a => a.QueueItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeliveryAttempt>(e =>
        {
            e.Property(a => a.RemoteResponse).HasMaxLength(1024);
            e.Property(a => a.Error).HasMaxLength(1024);
        });

        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.Property(ev => ev.Action).HasMaxLength(255).IsRequired();
            e.Property(ev => ev.Actor).HasMaxLength(255);
            e.Property(ev => ev.ActorId).HasMaxLength(64);
            e.Property(ev => ev.IpAddress).HasMaxLength(64);
            e.Property(ev => ev.Target).HasMaxLength(255);
            e.Property(ev => ev.TargetId).HasMaxLength(255);
            e.Property(ev => ev.Result).HasMaxLength(64);
            e.Property(ev => ev.Metadata).HasMaxLength(2000);
            e.HasIndex(ev => ev.TimestampUtc);
        });

        modelBuilder.Entity<LoginAttempt>(e =>
        {
            e.Property(l => l.Username).HasMaxLength(255);
            e.Property(l => l.IpAddress).HasMaxLength(64);
            e.Property(l => l.FailureReason).HasMaxLength(64);
        });

        modelBuilder.Entity<Contact>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(255).IsRequired();
            e.Property(c => c.Email).HasMaxLength(320).IsRequired();
            e.Property(c => c.Phone).HasMaxLength(64);
            e.Property(c => c.Company).HasMaxLength(255);
            e.Property(c => c.Notes).HasMaxLength(2000);
            e.HasOne(c => c.Domain).WithMany().HasForeignKey(c => c.DomainId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ConfigurationEntry>(e =>
        {
            e.Property(c => c.Key).HasMaxLength(255).IsRequired();
            e.HasIndex(c => c.Key).IsUnique();
            e.Property(c => c.Value).HasMaxLength(2000);
        });
    }
}