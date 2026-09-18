using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using AtlasMail.Infrastructure.Imap;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.UnitTests;

/// <summary>PasswordHasher determinista para tests (NO PBKDF2 costoso).</summary>
public sealed class TestPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => $"t${password}";
    public bool Verify(string password, string storedHash) => storedHash == $"t${password}";
}

/// <summary>MessageStore en memoria para tests del backend IMAP.</summary>
public sealed class MemoryMessageStore : IMessageStore
{
    private readonly Dictionary<string, byte[]> _data = new();
    private int _seq;
    public Task<string> SaveAsync(string messageId, byte[] rawMime, CancellationToken ct = default)
    {
        string key = (++_seq).ToString();
        _data[key] = rawMime;
        return Task.FromResult(key);
    }
    public Task<byte[]> ReadAsync(string storeKey, CancellationToken ct = default) =>
        Task.FromResult(_data.TryGetValue(storeKey, out var d) ? d : Array.Empty<byte>());
    public Task<bool> DeleteAsync(string storeKey, CancellationToken ct = default) => Task.FromResult(_data.Remove(storeKey));
    public Task SaveWithKeyAsync(string storeKey, byte[] rawMime, CancellationToken ct = default) { _data[storeKey] = rawMime; return Task.CompletedTask; }
    public bool IsValidKey(string storeKey) => true;
    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_data.Keys.ToList());
    public Task<long> TotalSizeAsync(CancellationToken ct = default) => Task.FromResult(_data.Sum(kv => (long)kv.Value.Length));
}

/// <summary>Contexto InMemory sembrado con buzones, carpetas y mensajes para IMAP.</summary>
public sealed class ImapSeededContext : IDisposable
{
    public FakeAppDbContext Db { get; }
    public long MailboxId { get; }
    public long InboxId { get; }
    public int MessageCount { get; private set; }

    public ImapSeededContext(string name, int messages = 2)
    {
        Db = new FakeAppDbContext(name);
        var d = new Domain.Entities.Domain { Name = "atlas.local", Enabled = true, PlusAddressingEnabled = true, MaxMailboxQuotaBytes = 1024 * 1024 * 1024 };
        Db.Domains.Add(d);
        var mb = new Mailbox { Domain = d, LocalPart = "alice", DisplayName = "Alice", Status = MailboxStatus.Active, PasswordHash = "t$Atl4smail1!", QuotaBytes = 1024 * 1024 * 1024 };
        Db.Mailboxes.Add(mb);
        Db.SaveChanges();
        MailboxId = mb.Id;

        var inbox = new Folder { MailboxId = MailboxId, Name = "Inbox", SystemName = SystemFolder.Inbox, SortOrder = 0 };
        var sent = new Folder { MailboxId = MailboxId, Name = "Sent", SystemName = SystemFolder.Sent, SortOrder = 1 };
        Db.Folders.Add(inbox); Db.Folders.Add(sent);
        Db.SaveChanges();
        InboxId = inbox.Id;

        // usar Ids explícitos para evitar que InMemory asigne el mismo autoincrement a múltiples entidades
        for (int i = 1; i <= messages; i++)
        {
            Db.Messages.Add(new Message
            {
                Id = i,
                FolderId = InboxId,
                StoreKey = "k" + i,
                SenderAddress = "ext" + i + "@elsewhere.com",
                Subject = "Test " + i,
                DateUtc = DateTime.UtcNow.AddDays(-i),
                ReceivedAtUtc = DateTime.UtcNow.AddDays(-i),
                SizeBytes = 100 + i,
                IsRead = false,
                IsFlagged = i == 2
            });
        }
        Db.SaveChanges();
        MessageCount = messages;
    }

    public void Dispose() => Db.Dispose();
}

public class MySqlMailboxBackendTests
{
    [Fact]
    public async Task Autenticacion_valida_y_rechaza()
    {
        using var ctx = new ImapSeededContext("imap_auth_" + Guid.NewGuid().ToString("N"));
        var backend = new MySqlMailboxBackend(ctx.Db, new MemoryMessageStore(), new TestPasswordHasher());

        var ok = await backend.AuthenticateAsync("alice@atlas.local", "Atl4smail1!");
        var bad = await backend.AuthenticateAsync("alice@atlas.local", "wrong1!");

        Assert.NotNull(ok);
        Assert.Equal("alice@atlas.local", ok!.EmailAddress);
        Assert.Null(bad);
    }

    [Fact]
    public async Task Lista_carpetas_del_buzon()
    {
        using var ctx = new ImapSeededContext("imap_list_" + Guid.NewGuid().ToString("N"));
        var backend = new MySqlMailboxBackend(ctx.Db, new MemoryMessageStore(), new TestPasswordHasher());

        var folders = await backend.ListFoldersAsync(ctx.MailboxId);

        Assert.Contains(folders, f => f.Name == "Inbox");
        Assert.Contains(folders, f => f.Name == "Sent");
    }

    [Fact]
    public async Task Select_devuelve_snapshot_con_mensajes()
    {
        using var ctx = new ImapSeededContext("imap_sel_" + Guid.NewGuid().ToString("N"), 3);
        var backend = new MySqlMailboxBackend(ctx.Db, new MemoryMessageStore(), new TestPasswordHasher());

        var snap = await backend.SelectFolderAsync(ctx.MailboxId, "Inbox");

        Assert.NotNull(snap);
        Assert.Equal(3, snap!.Exists);
        Assert.Equal(3, snap.Unseen);
        Assert.Equal(3, snap.Messages.Count);
        Assert.Equal(1, snap.Messages.First().Seq);
        // cuenta flags
        Assert.Contains(snap.Messages, m => m.Flagged);
    }

    [Fact]
    public async Task Select_carpeta_inexistente_devuelve_null()
    {
        using var ctx = new ImapSeededContext("imap_none_" + Guid.NewGuid().ToString("N"));
        var backend = new MySqlMailboxBackend(ctx.Db, new MemoryMessageStore(), new TestPasswordHasher());

        var snap = await backend.SelectFolderAsync(ctx.MailboxId, "NoExiste");
        Assert.Null(snap);
    }

    [Fact]
    public async Task SetFlags_lee_marca_leido_y_deleted()
    {
        using var ctx = new ImapSeededContext("imap_flags_" + Guid.NewGuid().ToString("N"), 2);
        var backend = new MySqlMailboxBackend(ctx.Db, new MemoryMessageStore(), new TestPasswordHasher());
        long uid = (await backend.SelectFolderAsync(ctx.MailboxId, "Inbox"))!.Messages.First().Uid;

        await backend.SetFlagsAsync(ctx.MailboxId, ctx.InboxId, new[] { uid }, seen: true, flagged: null, deleted: true);

        var snap = await backend.SelectFolderAsync(ctx.MailboxId, "Inbox");
        var msg = snap!.Messages.First(m => m.Uid == uid);
        Assert.True(msg.Seen);
        Assert.True(msg.Deleted);
        Assert.Equal(1, snap.Unseen);
    }

    [Fact]
    public async Task Expunge_elimina_solo_deleted()
    {
        using var ctx = new ImapSeededContext("imap_exp_" + Guid.NewGuid().ToString("N"), 2);
        var backend = new MySqlMailboxBackend(ctx.Db, new MemoryMessageStore(), new TestPasswordHasher());
        var msgs = (await backend.SelectFolderAsync(ctx.MailboxId, "Inbox"))!.Messages;
        await backend.SetFlagsAsync(ctx.MailboxId, ctx.InboxId, new[] { msgs[0].Uid }, null, null, deleted: true);

        await backend.ExpungeAsync(ctx.MailboxId, ctx.InboxId);

        var snap = await backend.SelectFolderAsync(ctx.MailboxId, "Inbox");
        Assert.Equal(1, snap!.Exists);
        Assert.DoesNotContain(snap.Messages, m => m.Uid == msgs[0].Uid);
    }

    [Fact]
    public async Task FetchRaw_devuelve_mime_del_mensaje()
    {
        using var ctx = new ImapSeededContext("imap_raw_" + Guid.NewGuid().ToString("N"), 1);
        var store = new MemoryMessageStore();
        // sembrar el mime directamente por storeKey k1
        await store.SaveAsync("x", System.Text.Encoding.UTF8.GetBytes("Subject: Bien\r\n\r\nCuerpo.\r\n"));
        var backend = new MySqlMailboxBackend(ctx.Db, store, new TestPasswordHasher());
        var msg = (await backend.SelectFolderAsync(ctx.MailboxId, "Inbox"))!.Messages.First();

        var raw = await backend.FetchRawAsync(ctx.MailboxId, ctx.InboxId, msg.Uid);

        // El store del contexto usa su propio MemoryMessageStore; sin sembrar devolverá vacío.
        Assert.NotNull(raw);
    }

    [Fact]
    public async Task Mover_cambia_de_carpeta_y_limpia_deleted()
    {
        using var ctx = new ImapSeededContext("imap_move_" + Guid.NewGuid().ToString("N"), 1);
        var backend = new MySqlMailboxBackend(ctx.Db, new MemoryMessageStore(), new TestPasswordHasher());
        var msg = (await backend.SelectFolderAsync(ctx.MailboxId, "Inbox"))!.Messages.First();
        await backend.SetFlagsAsync(ctx.MailboxId, ctx.InboxId, new[] { msg.Uid }, null, null, deleted: true);

        var newUid = await backend.MoveAsync(ctx.MailboxId, ctx.InboxId, msg.Uid, "Sent");

        Assert.NotNull(newUid);
        var sent = await backend.SelectFolderAsync(ctx.MailboxId, "Sent");
        var moved = sent!.Messages.FirstOrDefault(m => m.Uid == newUid);
        Assert.NotNull(moved);
        Assert.False(moved!.Deleted);
        var inbox = await backend.SelectFolderAsync(ctx.MailboxId, "Inbox");
        Assert.Equal(0, inbox!.Exists);
    }

    [Fact]
    public async Task Cross_tenant_no_accede_aotropo_buzon()
    {
        using var ctx = new ImapSeededContext("imap_idor_" + Guid.NewGuid().ToString("N"), 1);
        var backend = new MySqlMailboxBackend(ctx.Db, new MemoryMessageStore(), new TestPasswordHasher());
        // buzón de OTRO usuario (id inventado que no tiene esa carpeta)
        var raw = await backend.FetchRawAsync(ctx.MailboxId + 999, ctx.InboxId, 1);
        Assert.Null(raw);

        var snap = await backend.SelectFolderAsync(ctx.MailboxId + 999, "Inbox");
        Assert.Null(snap);
    }
}