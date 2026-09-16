using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using AtlasMail.Domain.Enums;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Mime;
using AtlasMail.Domain.Rules;
using AtlasMail.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.Application.Services;

/// <summary>
/// Pipeline de ingesta SMTP local (sección 6):
///   POLICY -> ENVELOPE -> DATA -> MIME PARSE -> SECURITY -> RULES -> STORE -> INDEX -> DELIVER LOCAL
/// La recepción NO depende del webmail. Persiste antes de confirmar; evita pérdida por crash.
/// </summary>
public class InboundDeliveryService : IInboundDeliveryService
{
    private readonly IApplicationDbContext _db;
        private readonly IMessageStore _store;
        private readonly IRuleEngine _rules;
        private readonly IAttachmentScanner _scanner;
        private readonly IAddressResolutionService _resolver;
        private readonly IAuditService _audit;
        private readonly ILogger<InboundDeliveryService> _logger;

        public InboundDeliveryService(IApplicationDbContext db, IMessageStore store,
            IRuleEngine rules, IAttachmentScanner scanner, IAddressResolutionService resolver,
            IAuditService audit, ILogger<InboundDeliveryService> logger)
        {
            _db = db; _store = store; _rules = rules;
            _scanner = scanner; _resolver = resolver; _audit = audit; _logger = logger;
        }

    public async Task<InboundResult> IngestAsync(string envelopeFrom, string envelopeTo, byte[] rawMime,
        string? helo, string? clientIp, bool authenticated, string actor, CancellationToken ct = default)
    {
        // POLICY: decidir si podemos entregar localmente
        if (!EmailAddress.TryParse(envelopeTo, out var rcpt))
            return await Reject(envelopeFrom, envelopeTo, "invalid_recipient", actor, clientIp, ct);

        var resolved = await _resolver.ResolveAsync(rcpt.Full, ct);
        bool domainLocal = resolved.Found || await _resolver.IsDomainLocalAsync(rcpt.Domain, ct);
        if (!domainLocal)
            return await Reject(envelopeFrom, envelopeTo, "relay_denied", actor, clientIp, ct);
        if (!resolved.Found)
            return await Reject(envelopeFrom, envelopeTo, "recipient_not_found", actor, clientIp, ct);

        // Persistir MIME inmediatamente (fail-safe: no perder por crash)
        var parsed = ParseMime(rawMime);
        string storeKey = await _store.SaveAsync(parsed.MessageIdHeader, rawMime, ct);

        // SECURITY: tamaño/adjuntos
        long size = rawMime.Length;
        bool overQuota = false;
        if (resolved.MailboxId.HasValue)
        {
            var mb = await _db.Mailboxes.FindAsync([resolved.MailboxId.Value], ct);
            if (mb != null && !QuotaPolicy.Accepts(mb.UsedBytes, mb.QuotaBytes, size))
                overQuota = true;
        }

        // Spam score (simple: señales locales; no API comercial)
        var spam = ComputeSpam(rawMime, parsed, helo, authenticated);

        // Objetivo de carpeta (reglas)
        var (folderOverride, moveToSpam) = await _rules.EvaluateAsync(envelopeFrom, envelopeTo, parsed.Subject, ct);
        var targetFolder = moveToSpam ? SystemFolder.Spam : SystemFolder.Inbox;
        if (!moveToSpam && folderOverride is not null && folderOverride.Equals("Spam", StringComparison.OrdinalIgnoreCase))
            targetFolder = SystemFolder.Spam;

        SpamDecision decision = !authenticated && spam.Score >= 6 ? SpamDecision.Spam : SpamDecision.Allow;
        if (overQuota) decision = SpamDecision.Quarantine;

        // STORE metadata
        if (!resolved.MailboxId.HasValue)
            return await Reject(envelopeFrom, envelopeTo, "recipient_not_found", actor, clientIp, ct);
        await StoreMetadataAsync(envelopeFrom, envelopeTo, parsed, storeKey, size, decision, spam.Score,
            resolved.MailboxId.Value, targetFolder, ct);

        if (decision == SpamDecision.Spam) targetFolder = SystemFolder.Spam;

        await _audit.RecordAsync("Smtp.Ingest", actor, null, clientIp, "message", envelopeTo, "OK",
            $"decision={decision} score={spam.Score:F1} store={storeKey}", ct);
        _logger.LogInformation("Ingesta SMTP OK {To} de {From} decision={Decision}", envelopeTo, envelopeFrom, decision);
        return decision == SpamDecision.Reject ? InboundResult.Rejected
            : decision == SpamDecision.Quarantine ? InboundResult.Quarantined
            : targetFolder == SystemFolder.Spam ? InboundResult.MovedToSpam
            : InboundResult.Accepted;
    }

    private async Task<InboundResult> Reject(string from, string to, string reason, string actor, string? ip, CancellationToken ct)
    {
        await _audit.RecordAsync("Smtp.Reject", actor, null, ip, "message", to, "DENIED", reason, ct);
        _logger.LogInformation("Ingesta SMTP denegada {To}: {Reason}", to, reason);
        return InboundResult.RelayDenied;
    }

    private static ParsedMessage ParseMime(byte[] raw)
    {
        try { return MimeParser.Parse(raw); }
        catch { return new ParsedMessage { Subject = "(no parseable)", SenderAddress = string.Empty }; }
    }

    private static SpamDecisionResult ComputeSpam(byte[] raw, ParsedMessage parsed, string? helo, bool authenticated)
    {
        double score = 0.0;
        var reasons = new List<string>();
        if (string.IsNullOrWhiteSpace(parsed.SenderAddress)) { score += 1.5; reasons.Add("no_from"); }
        if (string.IsNullOrWhiteSpace(parsed.Subject) && string.IsNullOrWhiteSpace(parsed.PlainBody) && string.IsNullOrWhiteSpace(parsed.HtmlBody)) { score += 1.0; reasons.Add("empty_body"); }
        if (raw.Length < 512) { score += 0.5; reasons.Add("tiny"); }
        var p = (parsed.Subject + " " + parsed.PlainBody + " " + parsed.HtmlBody).ToLowerInvariant();
        foreach (var keyword in new[] { "viagra", "lottery", "congratulations you", "free money", "crypto giveaway", "urgent action required" })
            if (p.Contains(keyword)) { score += 1.5; reasons.Add("keyword:" + keyword); }
        if (authenticated) score = Math.Min(score, 0.5); // autenticado confiable en gran medida
        return new SpamDecisionResult(SpamDecision.Allow, score, string.Join(",", reasons));
    }

    private async Task StoreMetadataAsync(string envelopeFrom, string envelopeTo, ParsedMessage parsed,
        string storeKey, long size, SpamDecision decision, double score, long mailboxId, SystemFolder targetFolder, CancellationToken ct)
    {
        var mb = await _db.Mailboxes.FindAsync([mailboxId], ct) ?? throw new InvalidOperationException("Buzón local no existe");
        var folder = await _db.Folders.FirstAsync(f => f.MailboxId == mailboxId && f.SystemName == targetFolder, ct);

        var message = new Message
        {
            FolderId = folder.Id,
            StoreKey = storeKey,
            MessageIdHeader = string.IsNullOrWhiteSpace(parsed.MessageIdHeader) ? null : parsed.MessageIdHeader,
            SenderAddress = string.IsNullOrWhiteSpace(parsed.SenderAddress) ? envelopeFrom : parsed.SenderAddress,
            SenderName = parsed.SenderName,
            Subject = string.IsNullOrWhiteSpace(parsed.Subject) ? "(sin asunto)" : parsed.Subject,
            DateUtc = parsed.DateUtc ?? DateTime.UtcNow,
            ReceivedAtUtc = DateTime.UtcNow,
            SizeBytes = size,
            BodyPreview = parsed.BuildPreview(),
            IsHtml = parsed.HasHtml,
            SpamDecision = decision,
            SpamScore = score,
            InReplyTo = string.IsNullOrWhiteSpace(parsed.InReplyTo) ? null : parsed.InReplyTo
        };
        _db.Messages.Add(message);

        foreach (var r in parsed.Recipients)
        {
            if (string.IsNullOrWhiteSpace(r.Address)) continue;
            _db.MessageRecipients.Add(new MessageRecipient
            {
                Message = message,
                Type = r.Kind.Equals("Bcc", StringComparison.OrdinalIgnoreCase) ? RecipientType.Bcc
                     : r.Kind.Equals("Cc", StringComparison.OrdinalIgnoreCase) ? RecipientType.Cc : RecipientType.To,
                Address = r.Address,
                DisplayName = r.Name
            });
        }

        foreach (var att in parsed.Attachments)
        {
            _db.Attachments.Add(MessageAttachment(att, message));
        }

        // Actualizar uso de cuota
        mb.UsedBytes = Math.Min(mb.UsedBytes + size, long.MaxValue);
        await _db.SaveChangesAsync(ct);
    }

    private static Attachment MessageAttachment(AttachmentPart att, Message message)
    {
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(att.Data)).ToLowerInvariant();
        return new Attachment
        {
            Message = message,
            FileName = att.FileName,
            ContentType = att.ContentType,
            SizeBytes = att.Data.Length,
            Sha256 = sha,
            ScanStatus = AttachmentScanStatus.Unknown
        };
    }
}