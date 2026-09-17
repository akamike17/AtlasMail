using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using AtlasMail.Domain.Mime;
using AtlasMail.Domain.Rules;
using AtlasMail.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.Application.Services;

/// <summary>Envío desde webmail: compose -> MIME -> store -> cola (sección 7).</summary>
public class SubmissionService : ISubmissionService
{
    private readonly IApplicationDbContext _db;
    private readonly IMessageStore _store;
    private readonly IOutboundQueueService _queue;
    private readonly IInboundDeliveryService _inbound;
    private readonly IAuditService _audit;
    private readonly ILogger<SubmissionService> _logger;

    public SubmissionService(IApplicationDbContext db, IMessageStore store, IOutboundQueueService queue,
        IInboundDeliveryService inbound, IAuditService audit, ILogger<SubmissionService> logger)
    {
        _db = db; _store = store; _queue = queue; _inbound = inbound; _audit = audit; _logger = logger;
    }

    public async Task<ComposeResult> SubmitAsync(ComposeMessageRequest req, string actor, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.FromUsername)) return new ComposeResult(false, "Remitente requerido", null, null);
        if (req.To == null || !req.To.Any()) return new ComposeResult(false, "Destinatario requerido", null, null);
        if (req.To.Any(a => !EmailAddress.TryParse(a, out _)))
            return new ComposeResult(false, "Dirección de destinatario inválida", null, null);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == req.FromUsername, ct)
            ?? throw new InvalidOperationException("Usuario no existe");

        var atts = (req.Attachments ?? Array.Empty<ComposeAttachmentDto>()).ToList();
        var mime = MimeBuilder.Build(new MimeBuilder.ComposeRequest
        {
            From = user.Username,
            To = req.To,
            Cc = req.Cc ?? Array.Empty<string>(),
            Subject = req.Subject,
            Body = req.Body,
            Attachments = atts.Select(a => new AttachmentPart
            {
                FileName = a.FileName,
                ContentType = string.IsNullOrWhiteSpace(a.ContentType) ? "application/octet-stream" : a.ContentType,
                Data = a.Data
            }).ToList()
        });

        string storeKey = await _store.SaveAsync(Guid.NewGuid().ToString("N"), mime, ct);

        // Resolver destino
        bool anyDelivered = false;
        long? lastQueueId = null;
        foreach (var rcpt in req.To)
        {
            var resolved = await Resolve(rcpt, ct);
            if (resolved.Local)
            {
                // delivery local directo (síncrono; persistido antes de confirmar)
                var result = await _inbound.IngestAsync(user.Username, resolved.Address, mime, "local", ip, true, actor, ct);
                anyDelivered = result is InboundResult.Accepted or InboundResult.MovedToSpam;
            }
            else
            {
                lastQueueId = await _queue.EnqueueAsync(user.Username, resolved.Address, storeKey,
                    $"<{Guid.NewGuid():N}@atlasmail>", false, ct);
            }
        }

        // Guardar copia en Sent del remitente
        var sentMailbox = await FindSentMailboxAsync(user, ct);
        if (sentMailbox != null)
        {
            await SaveToSentAsync(user.Username, req, mime, storeKey, sentMailbox, ct);
        }

        await _audit.RecordAsync("Mail.Submit", actor, user.Id.ToString(), ip, "message", string.Join(",", req.To), "OK", null, ct);
        return new ComposeResult(true, null, lastQueueId, storeKey);
    }

    private async Task<(string Address, bool Local)> Resolve(string address, CancellationToken ct)
    {
        var local = await _db.Domains.AsNoTracking().Select(d => d.Name).ToListAsync(ct);
        var parsed = EmailAddress.Parse(address);
        if (local.Contains(parsed.Domain, StringComparer.OrdinalIgnoreCase)) return (address, true);
        return (address, false);
    }

    private async Task SaveToSentAsync(string from, ComposeMessageRequest req, byte[] mime, string storeKey, Mailbox sentMailbox, CancellationToken ct)
    {
        var parsed = MimeParser.Parse(mime);
        var folder = await _db.Folders.FirstAsync(f => f.MailboxId == sentMailbox.Id && f.SystemName == SystemFolder.Sent, ct);
        var msg = new Message
        {
            FolderId = folder.Id, StoreKey = storeKey,
            SenderAddress = from,
            Subject = string.IsNullOrWhiteSpace(req.Subject) ? "(sin asunto)" : req.Subject,
            DateUtc = DateTime.UtcNow, ReceivedAtUtc = DateTime.UtcNow,
            SizeBytes = mime.Length, BodyPreview = parsed.BuildPreview(),
            IsHtml = parsed.HasHtml, SpamDecision = SpamDecision.Allow
        };
        _db.Messages.Add(msg);
        foreach (var r in req.To) _db.MessageRecipients.Add(new MessageRecipient { Message = msg, Type = RecipientType.To, Address = r });
        foreach (var c in req.Cc ?? Array.Empty<string>()) _db.MessageRecipients.Add(new MessageRecipient { Message = msg, Type = RecipientType.Cc, Address = c });
        foreach (var a in parsed.Attachments) _db.Attachments.Add(new Attachment
        {
            Message = msg, FileName = a.FileName, ContentType = a.ContentType, SizeBytes = a.Data.Length,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(a.Data)).ToLowerInvariant(),
            ScanStatus = AttachmentScanStatus.Unknown
        });
        sentMailbox.UsedBytes = Math.Min(sentMailbox.UsedBytes + mime.Length, long.MaxValue);
        await _db.SaveChangesAsync(ct);
    }

    private async Task<Mailbox?> FindSentMailboxAsync(User user, CancellationToken ct)
    {
        // El usuario web se mapea a un buzón por localpart dentro de su dominio
        if (!user.DomainId.HasValue) return null;
        return await _db.Mailboxes.FirstOrDefaultAsync(m =>
            m.DomainId == user.DomainId.Value && m.LocalPart == user.Username, ct);
    }
}

/// <summary>Lectura del buzón para webmail (aislado por mailboxId — IDOR: usuario A no ve buzón B).</summary>
public class MailboxService : IMailboxService
{
    private readonly IApplicationDbContext _db;
    private readonly IMessageStore _store;

    public MailboxService(IApplicationDbContext db, IMessageStore store) { _db = db; _store = store; }

    public async Task<IReadOnlyList<FolderDto>> ListFoldersAsync(long mailboxId, CancellationToken ct = default)
    {
        var folders = await _db.Folders.AsNoTracking()
            .Where(f => f.MailboxId == mailboxId)
            .Select(f => new
            {
                f.Id, f.Name, f.SystemName, f.SortOrder,
                MessageCount = f.Messages.Count,
                UnreadCount = f.Messages.Count(m => !m.IsRead && (m.SpamDecision == SpamDecision.Allow || m.SpamDecision == SpamDecision.None))
            })
            .OrderBy(f => f.SortOrder).ToListAsync(ct);
        return folders.Select(f => new FolderDto(f.Id, f.Name, f.SystemName, f.SortOrder, f.MessageCount, f.UnreadCount)).ToList();
    }

    public async Task<IReadOnlyList<MailboxMessageListItem>> ListFolderMessagesAsync(long mailboxId, long folderId, int take = 100, CancellationToken ct = default)
    {
        // Aislamiento: folderId pertenece al mailboxId
        var folder = await _db.Folders.FirstOrDefaultAsync(f => f.Id == folderId && f.MailboxId == mailboxId, ct);
        if (folder == null) return Array.Empty<MailboxMessageListItem>();
        var msgs = await _db.Messages.AsNoTracking()
            .Where(m => m.FolderId == folderId)
            .OrderByDescending(m => m.DateUtc)
            .Take(take)
            .Select(m => new
            {
                m.Id, m.SenderAddress, m.SenderName, m.Subject, m.DateUtc,
                Preview = m.BodyPreview ?? string.Empty, m.IsHtml, m.IsFlagged, m.IsRead, m.SizeBytes,
                HasAttachments = m.Attachments.Any()
            })
            .ToListAsync(ct);
        return msgs.Select(m => new MailboxMessageListItem(m.Id, m.SenderAddress, m.SenderName, m.Subject,
            m.DateUtc, m.Preview, m.IsHtml, m.IsFlagged, m.IsRead, m.SizeBytes, m.HasAttachments)).ToList();
    }

    public async Task<MailboxMessageDetail?> ReadMessageAsync(long mailboxId, long messageId, CancellationToken ct = default)
    {
        var msg = await _db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId && m.Folder!.MailboxId == mailboxId, ct);
        if (msg == null) return null;

        var recipients = await _db.MessageRecipients.AsNoTracking().Where(r => r.MessageId == messageId)
            .Select(r => new RecipientDto(r.Type, r.Address, r.DisplayName)).ToListAsync(ct);
        var atts = await _db.Attachments.AsNoTracking().Where(a => a.MessageId == messageId)
            .Select(a => new AttachmentMetaDto(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.Sha256)).ToListAsync(ct);

        var raw = await _store.ReadAsync(msg.StoreKey, ct);
        string body;
        try
        {
            var parsed = MimeParser.Parse(raw);
            body = parsed.HasHtml ? parsed.HtmlBody : parsed.PlainBody;
        }
        catch { body = "(no desplegable)"; }

        // marcar leído
        if (!msg.IsRead)
        {
            var tracked = await _db.Messages.FindAsync([messageId], ct);
            if (tracked != null) { tracked.IsRead = true; await _db.SaveChangesAsync(ct); }
        }

        return new MailboxMessageDetail(msg.Id, msg.FolderId, msg.SenderAddress, msg.SenderName, msg.Subject,
            msg.DateUtc, body, msg.IsHtml && !string.IsNullOrWhiteSpace(body), msg.IsRead, msg.IsFlagged, recipients, atts);
    }

    public async Task SetReadAsync(long mailboxId, long messageId, bool read, CancellationToken ct = default)
    {
        var msg = await _db.Messages.FirstOrDefaultAsync(m => m.Id == messageId && m.Folder!.MailboxId == mailboxId, ct);
        if (msg != null) { msg.IsRead = read; await _db.SaveChangesAsync(ct); }
    }

    public async Task SetFlaggedAsync(long mailboxId, long messageId, bool flagged, CancellationToken ct = default)
    {
        var msg = await _db.Messages.FirstOrDefaultAsync(m => m.Id == messageId && m.Folder!.MailboxId == mailboxId, ct);
        if (msg != null) { msg.IsFlagged = flagged; await _db.SaveChangesAsync(ct); }
    }

    public async Task MoveAsync(long mailboxId, long messageId, long destinationFolderId, CancellationToken ct = default)
    {
        var msg = await _db.Messages.FirstOrDefaultAsync(m => m.Id == messageId && m.Folder!.MailboxId == mailboxId, ct);
        var dest = await _db.Folders.FirstOrDefaultAsync(f => f.Id == destinationFolderId && f.MailboxId == mailboxId, ct);
        if (msg != null && dest != null) { msg.FolderId = dest.Id; await _db.SaveChangesAsync(ct); }
    }

    public async Task<byte[]> ReadRawAsync(long mailboxId, long messageId, CancellationToken ct = default)
    {
        var msg = await _db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId && m.Folder!.MailboxId == mailboxId, ct)
            ?? throw new InvalidOperationException("Mensaje no encontrado");
        return await _store.ReadAsync(msg.StoreKey, ct);
    }
}

/// <summary>Motor de reglas (sección 13) — modelo simple configurable por domino/global.</summary>
public class RuleEngine : IRuleEngine
{
    private const string RuleSubjSpam = "SPAM_KEYWORDS";
    public async Task<(string? FolderName, bool MoveToSpam)> EvaluateAsync(string from, string to, string subject, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var keywords = new[] { "noreply@", "newsletter", "unsubscribe", "marketing" };
        string combined = (subject ?? "").ToLowerInvariant();
        foreach (var k in keywords) if (combined.Contains(k)) return ("Spam", true);
        return (null, false);
    }
}

/// <summary>Escáner antimalware no-op explícitamente etiquetado para desarrollo (sección 21).</summary>
public class NoOpAttachmentScanner : IAttachmentScanner
{
    private readonly ILogger<NoOpAttachmentScanner> _logger;
    public NoOpAttachmentScanner(ILogger<NoOpAttachmentScanner> logger) => _logger = logger;
    public Task<AttachmentScanStatus> ScanAsync(AttachmentPart attachment, CancellationToken ct = default)
    {
        _logger.LogWarning("Scanner antimalware es no-op (desarrollo). No etiquetar como Clean.");
        return Task.FromResult(AttachmentScanStatus.Unknown);
    }
}

/// <summary>IA desacoplada registrada en DI: Local si Ai:Enabled, Disabled por defecto (sección 31).</summary>