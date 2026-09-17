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
        private readonly IEmailAuthenticationService? _emailAuth;
        private readonly IQuarantineService? _quarantine;
        private readonly IGroupService? _groups;
        private readonly ILogger<InboundDeliveryService> _logger;

        public InboundDeliveryService(IApplicationDbContext db, IMessageStore store,
            IRuleEngine rules, IAttachmentScanner scanner, IAddressResolutionService resolver,
            IAuditService audit, IEmailAuthenticationService? emailAuth, IQuarantineService? quarantine,
            IGroupService? groups, ILogger<InboundDeliveryService> logger)
        {
            _db = db; _store = store; _rules = rules;
            _scanner = scanner; _resolver = resolver; _audit = audit; _emailAuth = emailAuth; _quarantine = quarantine; _groups = groups; _logger = logger;
        }

        // Constructor legacy para tests que no dependen de auth de correo/cuarentena
        public InboundDeliveryService(IApplicationDbContext db, IMessageStore store,
            IRuleEngine rules, IAttachmentScanner scanner, IAddressResolutionService resolver,
            IAuditService audit, ILogger<InboundDeliveryService> logger)
            : this(db, store, rules, scanner, resolver, audit, emailAuth: null, quarantine: null, groups: null, logger) { }

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

        // FASE 6: lista de distribución local → entregar a cada miembro local
        if (!resolved.Found && _groups != null)
        {
            bool isDistribution = await _groups.IsDistributionAsync(rcpt.Full, ct);
            if (isDistribution)
            {
                var expanded = await _groups.ExpandAsync(rcpt.Full, ct);
                if (expanded.Count == 0)
                    return await Reject(envelopeFrom, envelopeTo, "list_empty", actor, clientIp, ct);
                int deliveredLocal = 0, skipped = 0;
                foreach (var member in expanded)
                {
                    var memberResolved = await _resolver.ResolveAsync(member, ct);
                    if (memberResolved.Found && memberResolved.MailboxId.HasValue)
                    {
                        var sub = await IngestAsync(envelopeFrom, member, rawMime, helo, clientIp, authenticated, actor, ct);
                        if (sub != InboundResult.RelayDenied && sub != InboundResult.Rejected) deliveredLocal++;
                    }
                    else skipped++;
                }
                await _audit.RecordAsync("Smtp.List", actor, null, clientIp, "message", envelopeTo, "OK",
                    $"list={rcpt.Full} local_delivered={deliveredLocal} skipped={skipped}", ct);
                _logger.LogInformation("Lista {List}: {D} locales entregados, {S} omitidos", rcpt.Full, deliveredLocal, skipped);
                return deliveredLocal > 0 ? InboundResult.Accepted : InboundResult.Rejected;
            }
        }

        if (!resolved.Found)
            return await Reject(envelopeFrom, envelopeTo, "recipient_not_found", actor, clientIp, ct);

        // FASE 5: remitente bloqueado → rechazar antes de persistir
        if (!authenticated && _quarantine != null)
        {
            var blocked = await _quarantine.MatchBlockedAsync(envelopeFrom, ct);
            if (blocked != null)
                return await Reject(envelopeFrom, envelopeTo, $"sender_blocked ({blocked.Kind})", actor, clientIp, ct);
        }

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

        // FASE 5: antimalware heurístico en cada adjunto (llenar ScanStatus + score)
        double malwareScore = 0.0;
        bool hasMalware = false;
        if (parsed.Attachments.Count > 0)
        {
            foreach (var att in parsed.Attachments)
            {
                AttachmentScanStatus status;
                try { status = await _scanner.ScanAsync(att, ct); }
                catch { status = AttachmentScanStatus.ScannerUnavailable; }
                att.ScanStatus = status;   // MessageAttachment lo copia a Attachment.ScanStatus
                if (status == AttachmentScanStatus.Malicious) { malwareScore += 4.0; hasMalware = true; }
                else if (status == AttachmentScanStatus.Suspicious) malwareScore += 1.5;
            }
        }

        // Spam score (simple: señales locales; no API comercial)
        var spam = ComputeSpam(rawMime, parsed, helo, authenticated);

        // FASE 4: autenticación de correo (SPF/DKIM/DMARC) en recepción
        double authScore = 0.0;
        string authSummary = "auth=none";
        bool dmarcReject = false, dmarcQuarantine = false;
        if (!authenticated && _emailAuth != null)
        {
            string fromDomain = FromHeaderDomain(parsed.SenderAddress) ?? EnvelopeDomain(envelopeFrom);
            if (!string.IsNullOrEmpty(fromDomain))
            {
                try
                {
                    var auth = await _emailAuth.AuthenticateAsync(fromDomain, envelopeFrom, clientIp, rawMime, ct);
                    authSummary = string.Join(",", auth.AuthResults);
                    if (auth.SpfResult.Equals("Fail", StringComparison.OrdinalIgnoreCase)) authScore += 2.5;
                    else if (auth.SpfResult.Equals("SoftFail", StringComparison.OrdinalIgnoreCase)) authScore += 1.0;
                    if (auth.DkimResult.Equals("Fail", StringComparison.OrdinalIgnoreCase)) authScore += 2.0;
                    if (!auth.DmarcAligned && auth.DmarcResult.Equals("Fail", StringComparison.OrdinalIgnoreCase)) authScore += 2.0;
                    dmarcReject = auth.ShouldReject;
                    dmarcQuarantine = auth.ShouldQuarantine;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Fallo auth de correo para {From}: {Msg}", envelopeFrom, ex.Message);
                }
            }
        }

        // Objetivo de carpeta (reglas)
        var (folderOverride, moveToSpam) = await _rules.EvaluateAsync(envelopeFrom, envelopeTo, parsed.Subject, ct);
        var targetFolder = moveToSpam ? SystemFolder.Spam : SystemFolder.Inbox;
        if (!moveToSpam && folderOverride is not null && folderOverride.Equals("Spam", StringComparison.OrdinalIgnoreCase))
            targetFolder = SystemFolder.Spam;

        double totalScore = spam.Score + authScore + malwareScore;
        SpamDecision decision = !authenticated && totalScore >= 6 ? SpamDecision.Spam : SpamDecision.Allow;
        string? quarantineReason = null;
        if (hasMalware && !authenticated)
        {
            decision = SpamDecision.Quarantine; quarantineReason = "malware";
        }
        else if (dmarcReject && !authenticated)
        {
            decision = SpamDecision.Reject;
        }
        else if (dmarcQuarantine && !authenticated)
        {
            decision = SpamDecision.Quarantine; quarantineReason = "dmarc";
        }
        else if (overQuota)
        {
            decision = SpamDecision.Quarantine; quarantineReason = "over-quota";
        }

        // STORE metadata
        if (!resolved.MailboxId.HasValue)
            return await Reject(envelopeFrom, envelopeTo, "recipient_not_found", actor, clientIp, ct);
        await StoreMetadataAsync(envelopeFrom, envelopeTo, parsed, storeKey, size, decision, totalScore,
            resolved.MailboxId.Value, targetFolder, quarantineReason, ct);

        if (decision == SpamDecision.Spam) targetFolder = SystemFolder.Spam;

        await _audit.RecordAsync("Smtp.Ingest", actor, null, clientIp, "message", envelopeTo, "OK",
            $"decision={decision} score={totalScore:F1} auth=[{authSummary}]{(quarantineReason != null ? $" q={quarantineReason}" : "")} store={storeKey}", ct);
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
        string storeKey, long size, SpamDecision decision, double score, long mailboxId, SystemFolder targetFolder,
        string? quarantineReason, CancellationToken ct)
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
            InReplyTo = string.IsNullOrWhiteSpace(parsed.InReplyTo) ? null : parsed.InReplyTo,
            IsQuarantined = decision == SpamDecision.Quarantine,
            QuarantineReason = quarantineReason,
            QuarantinedAtUtc = decision == SpamDecision.Quarantine ? DateTime.UtcNow : null
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
            ScanStatus = att.ScanStatus
        };
    }

    private static string? FromHeaderDomain(string? senderAddress)
    {
        if (string.IsNullOrWhiteSpace(senderAddress)) return null;
        int at = senderAddress.IndexOf('@');
        return at >= 0 ? senderAddress[(at + 1)..] : null;
    }

    private static string? EnvelopeDomain(string envelopeFrom)
    {
        var e = envelopeFrom.Trim();
        if (e.StartsWith("<")) e = e.Trim('<', '>');
        if (e.StartsWith("MAIL FROM:")) e = e["MAIL FROM:".Length..].Trim().Trim('<', '>');
        int at = e.IndexOf('@');
        return at >= 0 ? e[(at + 1)..] : null;
    }
}