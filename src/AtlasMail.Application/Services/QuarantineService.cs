using AtlasMail.Application.Abstractions;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Application.Services;

/// <summary>
/// Cuarentena administrable (spec §22): listar, inspeccionar metadatos/adjuntos (sin el MIME
/// completo), liberar, eliminar y bloquear remitentes. Accesible por SecurityAdmin; el usuario
/// tiene vista limitada por política (aquí: el usuario solo ve su conteo, delegado al dashboard).
/// </summary>
public class QuarantineService : IQuarantineService
{
    private readonly IApplicationDbContext _db;
    private readonly IMessageStore _store;

    public QuarantineService(IApplicationDbContext db, IMessageStore store)
    {
        _db = db; _store = store;
    }

    public async Task<IReadOnlyList<QuarantineItem>> ListAsync(int skip = 0, int take = 50, long? mailboxId = null, CancellationToken ct = default)
    {
        var q = _db.Messages.AsNoTracking()
            .Where(m => m.IsQuarantined)
            .AsQueryable();
        if (mailboxId.HasValue)
        {
            var folderIds = await _db.Folders.AsNoTracking()
                .Where(f => f.MailboxId == mailboxId.Value).Select(f => f.Id).ToListAsync(ct);
            q = q.Where(m => folderIds.Contains(m.FolderId));
        }

        var rows = await q
            .OrderByDescending(m => m.QuarantinedAtUtc ?? m.ReceivedAtUtc)
            .Skip(skip).Take(take)
            .Select(m => new
            {
                m.Id, m.FolderId, m.SenderAddress, m.Subject, m.QuarantineReason, m.SpamScore,
                m.ReceivedAtUtc, m.QuarantinedAtUtc, m.Folder!.MailboxId
            })
            .ToListAsync(ct);

        var result = new List<QuarantineItem>(rows.Count);
        foreach (var r in rows)
        {
            string mailboxAddress = await MailboxAddress(r.MailboxId, ct);
            int attCount = await _db.Attachments.CountAsync(a => a.MessageId == r.Id, ct);
            var worst = await WorstScan(r.Id, ct);
            result.Add(new QuarantineItem(
                r.Id, r.MailboxId, mailboxAddress, r.SenderAddress,
                string.IsNullOrWhiteSpace(r.Subject) ? "(sin asunto)" : r.Subject,
                r.QuarantineReason ?? "unknown", r.SpamScore, attCount, worst,
                r.ReceivedAtUtc, r.QuarantinedAtUtc ?? r.ReceivedAtUtc));
        }
        return result;
    }

    public async Task<QuarantineInspect?> InspectAsync(long messageId, CancellationToken ct = default)
    {
        var m = await _db.Messages.AsNoTracking()
            .Include(x => x.Attachments)
            .FirstOrDefaultAsync(x => x.Id == messageId && x.IsQuarantined, ct);
        if (m == null) return null;

        var atts = m.Attachments.Select(a => new QuarantineAttachment(
            a.FileName, a.ContentType, a.SizeBytes, a.Sha256, a.ScanStatus.ToString())).ToList();

        var summary = m.Attachments.Count == 0 ? "sin adjuntos"
            : string.Join(", ", m.Attachments.Select(a => $"{a.FileName}={a.ScanStatus}"));
        if (!string.IsNullOrEmpty(m.QuarantineReason)) summary = $"{m.QuarantineReason} | {summary}";

        return new QuarantineInspect(
            m.Id, m.SenderAddress, m.Subject, m.SizeBytes, m.ReceivedAtUtc,
            m.BodyPreview, m.IsHtml, summary, atts);
    }

    public async Task<bool> ReleaseAsync(long messageId, CancellationToken ct = default)
    {
        var m = await _db.Messages.FirstOrDefaultAsync(x => x.Id == messageId && x.IsQuarantined, ct);
        if (m == null) return false;
        m.IsQuarantined = false;
        m.QuarantineReason = null;
        m.QuarantinedAtUtc = null;
        m.SpamDecision = m.SpamScore >= 6 ? SpamDecision.Spam : SpamDecision.Allow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteAsync(long messageId, CancellationToken ct = default)
    {
        var m = await _db.Messages
            .Include(x => x.Attachments)
            .FirstOrDefaultAsync(x => x.Id == messageId && x.IsQuarantined, ct);
        if (m == null) return false;

        _db.Attachments.RemoveRange(m.Attachments);
        _db.Messages.Remove(m);
        await _db.SaveChangesAsync(ct);
        // eliminar blob después de confirmar (fail-safe: metadata primero)
        await _store.DeleteAsync(m.StoreKey, ct);
        return true;
    }

    public async Task<bool> BlockSenderAsync(string value, SenderMatchKind kind, string? reason, string? actor, CancellationToken ct = default)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v.Length == 0) return false;
        if (kind == SenderMatchKind.Exact && !v.Contains('@')) return false; // exacto debe ser address

        var exists = await _db.BlockedSenders.AnyAsync(b => b.Value == v && b.MatchKind == kind, ct);
        if (exists) return false;
        _db.BlockedSenders.Add(new BlockedSender { Value = v, MatchKind = kind, Reason = reason, CreatedBy = actor });
        await _db.SaveChangesAsync(ct);

        // eliminar mensajes en cuarentena del remitente bloqueado (mismo criterio que la blocklist)
        List<Message> blocked;
        if (kind == SenderMatchKind.Domain)
            blocked = await _db.Messages.Where(m => m.IsQuarantined && m.SenderAddress.ToLower().EndsWith("@" + v)).ToListAsync(ct);
        else
            blocked = await _db.Messages.Where(m => m.IsQuarantined && m.SenderAddress.ToLower() == v).ToListAsync(ct);
        foreach (var b in blocked) await DeleteAsync(b.Id, ct);
        return true;
    }

    public async Task<IReadOnlyList<BlockedSenderInfo>> ListBlockedAsync(CancellationToken ct = default)
    {
        return await _db.BlockedSenders.AsNoTracking()
            .OrderByDescending(b => b.CreatedAtUtc)
            .Select(b => new BlockedSenderInfo(b.Id, b.Value, b.MatchKind.ToString(), b.Reason, b.CreatedAtUtc, b.CreatedBy))
            .ToListAsync(ct);
    }

    public async Task<bool> UnblockAsync(long id, CancellationToken ct = default)
    {
        var b = await _db.BlockedSenders.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (b == null) return false;
        _db.BlockedSenders.Remove(b);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Comprueba si una dirección de remitente coincide con la blocklist (ingesta).</summary>
    public async Task<IBlockedMatch?> MatchBlockedAsync(string senderAddress, CancellationToken ct = default)
    {
        var s = senderAddress.Trim().ToLowerInvariant();
        var b = await _db.BlockedSenders.AsNoTracking().FirstOrDefaultAsync(x =>
            (x.MatchKind == SenderMatchKind.Exact && x.Value == s) ||
            (x.MatchKind == SenderMatchKind.Domain && s.EndsWith("@" + x.Value)), ct);
        if (b == null) return null;
        return new BlockedMatch(b.MatchKind == SenderMatchKind.Domain ? "domain" : "address");
    }

    private sealed record BlockedMatch(string Kind) : IBlockedMatch;

    private async Task<string> MailboxAddress(long mailboxId, CancellationToken ct)
    {
        var x = await _db.Mailboxes.AsNoTracking()
            .Where(m => m.Id == mailboxId)
            .Select(m => new { m.LocalPart, DomainName = m.Domain != null ? m.Domain.Name : null })
            .FirstOrDefaultAsync(ct);
        if (x == null) return $"#{mailboxId}";
        return string.IsNullOrEmpty(x.DomainName) ? x.LocalPart : $"{x.LocalPart}@{x.DomainName}";
    }

    private async Task<string> WorstScan(long messageId, CancellationToken ct)
    {
        var scans = await _db.Attachments.AsNoTracking()
            .Where(a => a.MessageId == messageId).Select(a => a.ScanStatus).ToListAsync(ct);
        if (scans.Contains(AttachmentScanStatus.Malicious)) return "Malicious";
        if (scans.Contains(AttachmentScanStatus.Suspicious)) return "Suspicious";
        if (scans.Contains(AttachmentScanStatus.ScannerUnavailable)) return "ScannerUnavailable";
        if (scans.Count > 0 && scans.All(s => s == AttachmentScanStatus.Clean)) return "Clean";
        if (scans.Count > 0) return "Unknown";
        return "-";
    }
}