using AtlasMail.Application.Services;
using AtlasMail.Application.Dtos;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;

namespace AtlasMail.UnitTests;

public class AddressResolutionTests : SeededContext
{
    public AddressResolutionTests() : base("addr-" + Guid.NewGuid().ToString("N")) { }

    [Fact]
    public async Task Resuelve_buzon_directo()
    {
        var svc = new AddressResolutionService(Db);
        var r = await svc.ResolveAsync("alice@atlas.local");
        Assert.True(r.Found);
        Assert.Equal("alice", r.LocalPart);
        Assert.False(r.IsAlias);
    }

    [Fact]
    public async Task Resuelve_alias_a_buzon_destino()
    {
        var bob = Db.Mailboxes.First(m => m.LocalPart == "bob");
        Db.Aliases.Add(new Alias { DomainId = 1, LocalPart = "ventas", TargetMailboxId = bob.Id, Enabled = true });
        await Db.SaveChangesAsync();
        var svc = new AddressResolutionService(Db);
        var r = await svc.ResolveAsync("ventas@atlas.local");
        Assert.True(r.Found);
        Assert.True(r.IsAlias);
        Assert.Equal(bob.Id, r.MailboxId);
    }

    [Fact]
    public async Task Plus_address_entrega_al_buzon_base()
    {
        var svc = new AddressResolutionService(Db);
        var r = await svc.ResolveAsync("alice+bootstrap@atlas.local");
        Assert.True(r.Found);
        Assert.Equal("alice", r.ResolvedLocalPart);
        Assert.NotNull(r.MailboxId);
    }

    [Fact]
    public async Task Dominio_externo_no_es_local()
    {
        var svc = new AddressResolutionService(Db);
        Assert.False(await svc.IsDomainLocalAsync("elsewhere.com"));
    }

    [Fact]
    public async Task Destinatario_inexistente_no_se_confunde_con_relay()
    {
        var svc = new AddressResolutionService(Db);
        var r = await svc.ResolveAsync("ghost@atlas.local");
        Assert.False(r.Found);
    }
}

public class OutboundQueueTests
{
    [Fact]
    public async Task Claim_marca_processing_y_devuelve_datos()
    {
        using var ctx = new SeededContext("q-" + Guid.NewGuid().ToString("N"));
        var queue = new OutboundQueueService(ctx.Db, NullLogger<OutboundQueueService>.Instance);
        long id = await queue.EnqueueAsync("a@x.com", "b@x.com", "k1", "<mid>", isLocal: true);
        var claim = await queue.ClaimNextAsync("worker-1");
        Assert.NotNull(claim);
        Assert.Equal(id, claim!.QueueItemId);
        Assert.Equal("worker-1", ctx.Db.DeliveryQueue.Find(id)!.ClaimedBy);
        Assert.Equal(DeliveryState.Processing, ctx.Db.DeliveryQueue.Find(id)!.State);
    }

    [Fact]
    public async Task Complete_marca_delivered_y_libera_lease()
    {
        using var ctx = new SeededContext("q-" + Guid.NewGuid().ToString("N"));
        var queue = new OutboundQueueService(ctx.Db, NullLogger<OutboundQueueService>.Instance);
        long id = await queue.EnqueueAsync("a@x.com", "b@x.com", "k1", null, isLocal: true);
        await queue.ClaimNextAsync("w");
        await queue.CompleteAsync(id, "250 OK");
        var item = ctx.Db.DeliveryQueue.Find(id)!;
        Assert.Equal(DeliveryState.Delivered, item.State);
        Assert.Null(item.LeaseExpiresAtUtc);
    }

    [Fact]
    public async Task Defer_cuenta_intentos_y_usa_retry_hasta_maximo()
    {
        using var ctx = new SeededContext("q-" + Guid.NewGuid().ToString("N"));
        var queue = new OutboundQueueService(ctx.Db, NullLogger<OutboundQueueService>.Instance);
        long id = await queue.EnqueueAsync("a@x.com", "b@x.com", "k1", null, isLocal: true);

        for (int i = 1; i <= 6; i++) await queue.DeferAsync(id, "temp failure", "451 4.3.0", CancellationToken.None);

        var item = ctx.Db.DeliveryQueue.Find(id)!;
        Assert.Equal(6, item.Attempts);
        Assert.Equal(DeliveryState.Failed, item.State); // sin loop infinito: Failed al llegar al máximo
    }

    [Fact]
    public async Task Claim_lease_vencido_se_puede_reclamar_de_nuevo()
    {
        using var ctx = new SeededContext("q-" + Guid.NewGuid().ToString("N"));
        var queue = new OutboundQueueService(ctx.Db, NullLogger<OutboundQueueService>.Instance);
        long id = await queue.EnqueueAsync("a@x.com", "b@x.com", "k1", null, isLocal: true);
        var claim = await queue.ClaimNextAsync("w1");
        Assert.NotNull(claim);

        // simular crash: expirar lease
        var item = ctx.Db.DeliveryQueue.Find(id)!;
        item.LeaseExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        await ctx.Db.SaveChangesAsync();

        var reclaim = await queue.ClaimNextAsync("w2");
        Assert.NotNull(reclaim); // crash recovery: vuelve a ser procesable
        Assert.Equal(DeliveryState.Processing, ctx.Db.DeliveryQueue.Find(id)!.State);
    }
}

public class MailboxServiceIdorTests
{
    private static (FakeAppDbContext db, Mailbox alice, Mailbox bob) SeedTwoMailboxes(string name)
    {
        var db = new FakeAppDbContext(name);
        var d = TestData.NewDomain();
        db.Domains.Add(d);
        db.SaveChanges();
        var alice = TestData.NewMailbox(d.Id, "alice");
        var bob = TestData.NewMailbox(d.Id, "bob");
        db.Mailboxes.Add(alice); db.Mailboxes.Add(bob);
        db.SaveChanges();
        var fAliceInbox = TestData.NewFolder(alice.Id, SystemFolder.Inbox);
        var fBobInbox = TestData.NewFolder(bob.Id, SystemFolder.Inbox);
        db.Folders.Add(fAliceInbox); db.Folders.Add(fBobInbox);
        db.SaveChanges();
        return (db, alice, bob);
    }

    [Fact]
    public async Task Alice_rechaza_leer_buzon_de_bob()
    {
        var (db, alice, bob) = SeedTwoMailboxes("idor-" + Guid.NewGuid().ToString("N"));
        var fBobInbox = db.Folders.First(f => f.MailboxId == bob.Id);
        db.Messages.Add(new Message { FolderId = fBobInbox.Id, StoreKey = "k", SenderAddress = "x@x.com", Subject = "secreto de bob", SizeBytes = 1 });
        db.SaveChanges();
        var svc = new MailboxService(db, new StubStore());

        // alice intenta listar el Inbox de bob (folder de bob)
        var msgs = await svc.ListFolderMessagesAsync(alice.Id, fBobInbox.Id);
        Assert.Empty(msgs); // IDOR bloqueado
    }

    [Fact]
    public async Task Current_user_misma_carpeta_si_no_pertenece()
    {
        var (db, alice, bob) = SeedTwoMailboxes("idor2-" + Guid.NewGuid().ToString("N"));
        var fBobInbox = db.Folders.First(f => f.MailboxId == bob.Id);
        var svc = new MailboxService(db, new StubStore());
        var detail = await svc.ReadMessageAsync(alice.Id, 1); // mensaje inexistente
        Assert.Null(detail);
    }
}

public class StubStore : Application.Abstractions.IMessageStore
{
    public Task<string> SaveAsync(string messageId, byte[] rawMime, CancellationToken ct = default) => Task.FromResult("k");
    public Task<byte[]> ReadAsync(string storeKey, CancellationToken ct = default) => Task.FromResult(new byte[0]);
    public Task<bool> DeleteAsync(string storeKey, CancellationToken ct = default) => Task.FromResult(true);
    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    public Task<long> TotalSizeAsync(CancellationToken ct = default) => Task.FromResult(0L);
    public Task SaveWithKeyAsync(string storeKey, byte[] rawMime, CancellationToken ct = default) => Task.CompletedTask;
}