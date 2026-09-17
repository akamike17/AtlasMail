using AtlasMail.Application;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.UnitTests;

/// <summary>Fake InMemory de IApplicationDbContext para tests unitarios de Application.</summary>
public class FakeAppDbContext : DbContext, IApplicationDbContext
{
    public FakeAppDbContext(string dbName)
        : base(new DbContextOptionsBuilder<FakeAppDbContext>().UseInMemoryDatabase(dbName).Options) { }

    public DbSet<MailDomain> Domains { get; set; } = null!;
    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Mailbox> Mailboxes { get; set; } = null!;
    public DbSet<Alias> Aliases { get; set; } = null!;
    public DbSet<Folder> Folders { get; set; } = null!;
    public DbSet<Message> Messages { get; set; } = null!;
    public DbSet<MessageRecipient> MessageRecipients { get; set; } = null!;
    public DbSet<Attachment> Attachments { get; set; } = null!;
    public DbSet<DeliveryQueueItem> DeliveryQueue { get; set; } = null!;
    public DbSet<DeliveryAttempt> DeliveryAttempts { get; set; } = null!;
    public DbSet<AuditEvent> AuditEvents { get; set; } = null!;
    public DbSet<LoginAttempt> LoginAttempts { get; set; } = null!;
    public DbSet<Contact> Contacts { get; set; } = null!;
    public DbSet<ConfigurationEntry> ConfigurationEntries { get; set; } = null!;
    public DbSet<BlockedSender> BlockedSenders { get; set; } = null!;

    public Task<int> ExecuteSqlRawAsync(string sql, CancellationToken ct = default) => Task.FromResult(0);

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Mailbox>().HasKey(x => x.Id);
        mb.Entity<MailDomain>().HasKey(x => x.Id);
        mb.Entity<Folder>().HasKey(x => x.Id);
        mb.Entity<Alias>().HasKey(x => x.Id);
        mb.Entity<DeliveryQueueItem>().HasKey(x => x.Id);
        mb.Entity<Message>().HasKey(x => x.Id);
    }
}

public static class TestData
{
    public static MailDomain NewDomain(string name = "atlas.local") => new()
    {
        Name = name, Enabled = true, PlusAddressingEnabled = true,
        MaxMailboxQuotaBytes = 1024 * 1024 * 1024, CatchAllEnabled = false
    };

    public static Mailbox NewMailbox(long domainId, string local, long quota = 1024 * 1024 * 1024, long used = 0) => new()
    {
        DomainId = domainId, LocalPart = local, DisplayName = local, Status = MailboxStatus.Active,
        PasswordHash = "1$10000$salt$hash", QuotaBytes = quota, UsedBytes = used
    };

    public static User NewUser(string username, UserRole role = UserRole.User, long? domainId = 1) => new()
    {
        Username = username, PasswordHash = "1$10000$salt$hash", DisplayName = username, Role = role, DomainId = domainId
    };

    public static Folder NewFolder(long mailboxId, SystemFolder sys) => new()
    {
        MailboxId = mailboxId, Name = sys.ToString(), SystemName = sys, SortOrder = (int)sys
    };
}

public class SeededContext : IDisposable
{
    public FakeAppDbContext Db { get; }
    public SeededContext(string name)
    {
        Db = new FakeAppDbContext(name);
        var d = TestData.NewDomain();
        Db.Domains.Add(d);
        Db.Mailboxes.Add(TestData.NewMailbox(d.Id, "alice"));
        Db.Mailboxes.Add(TestData.NewMailbox(d.Id, "bob"));
        Db.SaveChanges();
        var mb = Db.Mailboxes.First();
        Db.Folders.Add(TestData.NewFolder(mb.Id, SystemFolder.Inbox));
        Db.Folders.Add(TestData.NewFolder(mb.Id, SystemFolder.Sent));
        Db.Users.Add(TestData.NewUser("alice"));
        Db.SaveChanges();
    }
    public void Dispose() => Db.Dispose();
}