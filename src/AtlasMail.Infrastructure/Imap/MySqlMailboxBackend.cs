using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using AtlasMail.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Infrastructure.Imap;

/// <summary>
/// Implementación del backend de buzón IMAP sobre el modelo MySQL (metadatos/carpetas)
/// y el IMessageStore (MIME crudo). El protocolo IMAP sólo ve IMailboxBackend.
/// </summary>
public sealed class MySqlMailboxBackend : IMailboxBackend
{
    private readonly IApplicationDbContext _db;
    private readonly IMessageStore _store;
    private readonly IPasswordHasher _hasher;

    public MySqlMailboxBackend(IApplicationDbContext db, IMessageStore store, IPasswordHasher hasher)
    {
        _db = db; _store = store; _hasher = hasher;
    }

    public async Task<MailboxLoginResult?> AuthenticateAsync(string username, string password, CancellationToken ct = default)
    {
        string local;
        string? domain = null;
        if (EmailAddress.TryParse(username, out var addr)) { local = addr.LocalPart; domain = addr.Domain; }
        else local = (username ?? string.Empty).Trim();

        Mailbox? mb;
        if (domain != null)
        {
            mb = await _db.Mailboxes.Include(m => m.Domain)
                .FirstOrDefaultAsync(m => m.LocalPart == local && m.Domain!.Name == domain && m.Status == MailboxStatus.Active, ct);
        }
        else
        {
            mb = await _db.Mailboxes.Include(m => m.Domain)
                .FirstOrDefaultAsync(m => m.LocalPart == local && m.Status == MailboxStatus.Active, ct);
        }

        if (mb == null || string.IsNullOrEmpty(mb.PasswordHash) || !_hasher.Verify(password, mb.PasswordHash))
            return null;

        mb.LastAccessAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return new MailboxLoginResult(mb.Id, mb.EmailAddress, mb.DomainId);
    }

    public async Task<IReadOnlyList<ImapFolder>> ListFoldersAsync(long mailboxId, CancellationToken ct = default)
    {
        var folders = await _db.Folders.AsNoTracking()
            .Where(f => f.MailboxId == mailboxId)
            .OrderBy(f => f.SortOrder)
            .ToListAsync(ct);
        return folders.Select(f => new ImapFolder(f.Name, '/', HasChildren: false, f.SystemName)).ToList();
    }

    public async Task<ImapFolderSnapshot?> SelectFolderAsync(long mailboxId, string folderName, CancellationToken ct = default)
    {
        var folder = await _db.Folders.AsNoTracking()
            .FirstOrDefaultAsync(f => f.MailboxId == mailboxId && f.Name == folderName, ct);
        if (folder == null) return null;

        var messages = await _db.Messages.AsNoTracking()
            .Where(m => m.FolderId == folder.Id)
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        int seq = 0;
        var list = messages.Select(m => new ImapMessage(
            Seq: ++seq,
            Uid: m.Id,
            SizeBytes: m.SizeBytes,
            InternalDateUtc: (m.DateUtc == default ? m.ReceivedAtUtc : m.DateUtc).ToUniversalTime(),
            Seen: m.IsRead,
            Flagged: m.IsFlagged,
            Deleted: m.IsDeleted,
            Subject: m.Subject,
            MessageIdHeader: m.MessageIdHeader,
            InReplyTo: m.InReplyTo)).ToList();

        int unseen = list.Count(m => !m.Seen);
        return new ImapFolderSnapshot(
            FolderId: folder.Id,
            Name: folder.Name,
            UidValidity: StableUidValidity(folder.Id),
            Exists: list.Count,
            Recent: 0,
            Unseen: unseen,
            Messages: list);
    }

    public async Task<byte[]?> FetchRawAsync(long mailboxId, long folderId, long uid, CancellationToken ct = default)
    {
        // La carpeta ya está limitada al buzón en SelectFolderAsync; verificamos pertenencia
        // adicional para no exponer mensajes de otro buzón (aislamiento).
        bool belongs = await _db.Folders.AnyAsync(f => f.Id == folderId && f.MailboxId == mailboxId, ct);
        if (!belongs) return null;
        var msg = await _db.Messages.AsNoTracking().FirstOrDefaultAsync(
            m => m.FolderId == folderId && m.Id == uid, ct);
        if (msg == null) return null;
        return await _store.ReadAsync(msg.StoreKey, ct);
    }

    public async Task SetFlagsAsync(long mailboxId, long folderId, IReadOnlyList<long> uids, bool? seen, bool? flagged, bool? deleted, CancellationToken ct = default)
    {
        bool belongs = await _db.Folders.AnyAsync(f => f.Id == folderId && f.MailboxId == mailboxId, ct);
        if (!belongs) return;
        var ids = new List<long>();
        foreach (var uid in uids)
        {
            var m = await _db.Messages.FirstOrDefaultAsync(x => x.FolderId == folderId && x.Id == uid, ct);
            if (m == null) continue;
            if (seen.HasValue) m.IsRead = seen.Value;
            if (flagged.HasValue) m.IsFlagged = flagged.Value;
            if (deleted.HasValue) m.IsDeleted = deleted.Value;
            ids.Add(m.Id);
        }
        if (ids.Count > 0) await _db.SaveChangesAsync(ct);
    }

    public async Task<long?> MoveAsync(long mailboxId, long folderId, long uid, string destinationFolder, CancellationToken ct = default)
    {
        var dest = await _db.Folders.FirstOrDefaultAsync(f => f.MailboxId == mailboxId && f.Name == destinationFolder, ct);
        if (dest == null) return null;
        var msg = await _db.Messages.FirstOrDefaultAsync(m => m.FolderId == folderId && m.Id == uid, ct);
        if (msg == null) return null;
        msg.FolderId = dest.Id;
        msg.IsDeleted = false; // mover cancela \Deleted (lo hace al cambiar de carpeta)
        await _db.SaveChangesAsync(ct);
        return msg.Id;
    }

    public async Task ExpungeAsync(long mailboxId, long folderId, CancellationToken ct = default)
    {
        var deleted = await _db.Messages.Where(m => m.FolderId == folderId && m.IsDeleted).ToListAsync(ct);
        foreach (var m in deleted)
        {
            _db.Messages.Remove(m);
            try { await _store.DeleteAsync(m.StoreKey, ct); } catch { /* best effort */ }
        }
        if (deleted.Count > 0) await _db.SaveChangesAsync(ct);
    }

    // El UIDVALIDITY debe ser estable por carpeta. No depende de ids volátiles.
    private static long StableUidValidity(long folderId) =>
        unchecked((long)folderId + 1_000_000_000L * (folderId % 7919 + 1));
}