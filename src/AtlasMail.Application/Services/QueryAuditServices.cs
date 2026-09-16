using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using AtlasMail.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Application.Services;

/// <summary>Búsqueda de mensajes con aislamiento por buzón (sección 12).</summary>
public class MailSearchService : IMailSearchService
{
    private readonly IApplicationDbContext _db;

    public MailSearchService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(long mailboxId, MessageSearchQuery q, CancellationToken ct = default)
    {
        var query = _db.Messages.AsNoTracking().Where(m => m.Folder!.MailboxId == mailboxId);

        if (!string.IsNullOrWhiteSpace(q.Sender)) query = query.Where(m => m.SenderAddress.Contains(q.Sender));
        if (!string.IsNullOrWhiteSpace(q.Subject)) query = query.Where(m => m.Subject.Contains(q.Subject));
        if (!string.IsNullOrWhiteSpace(q.Body)) query = query.Where(m => m.BodyPreview != null && m.BodyPreview.Contains(q.Body));
        if (q.FromDate.HasValue) query = query.Where(m => m.DateUtc >= q.FromDate.Value);
        if (q.ToDate.HasValue) query = query.Where(m => m.DateUtc <= q.ToDate.Value);
        if (!string.IsNullOrWhiteSpace(q.Recipient))
        {
            var rcp = q.Recipient;
            query = query.Where(m => m.Recipients.Any(r => r.Address.Contains(rcp)));
        }
        if (!string.IsNullOrWhiteSpace(q.AttachmentName))
        {
            var att = q.AttachmentName;
            query = query.Where(m => m.Attachments.Any(a => a.FileName.Contains(att)));
        }

        int take = Math.Clamp(q.Take, 1, 500);
        var items = await query.OrderByDescending(m => m.DateUtc).Take(take)
            .Select(m => new
            {
                m.Id, m.SenderAddress, m.Subject, m.DateUtc,
                Preview = m.BodyPreview ?? string.Empty, m.IsHtml, m.IsRead, m.IsFlagged, m.SizeBytes
            }).ToListAsync(ct);
        return items.Select(m => new SearchResult(m.Id, m.SenderAddress, m.Subject, m.DateUtc, m.Preview,
            m.IsHtml, m.IsRead, m.IsFlagged, m.SizeBytes)).ToList();
    }
}

/// <summary>Message trace (sección 27): timeline por MessageId/remitente/destinatario/fecha.</summary>
public class MessageTraceService : IMessageTraceService
{
    private readonly IApplicationDbContext _db;

    public MessageTraceService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<MessageTraceItem>> SearchAsync(string? messageId, string? sender, string? recipient,
        DateTime? fromDate, DateTime? toDate, int take = 100, CancellationToken ct = default)
    {
        var q = _db.DeliveryQueue.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(messageId)) q = q.Where(dq => dq.MessageIdHeader != null && dq.MessageIdHeader.Contains(messageId));
        if (!string.IsNullOrWhiteSpace(sender)) q = q.Where(dq => dq.EnvelopeFrom.Contains(sender));
        if (!string.IsNullOrWhiteSpace(recipient)) q = q.Where(dq => dq.EnvelopeTo.Contains(recipient));
        if (fromDate.HasValue) q = q.Where(dq => dq.CreatedAtUtc >= fromDate.Value);
        if (toDate.HasValue) q = q.Where(dq => dq.CreatedAtUtc <= toDate.Value);

        take = Math.Clamp(take, 1, 500);
        var items = await q.OrderByDescending(dq => dq.Id).Take(take)
            .Select(dq => new
            {
                dq.Id, dq.MessageIdHeader, dq.EnvelopeFrom, dq.EnvelopeTo, dq.CreatedAtUtc,
                dq.State, dq.Attempts, dq.LastError,
                History = dq.AttemptHistory.Select(a => new
                { a.AttemptNumber, a.StartedAtUtc, a.FinishedAtUtc, a.Success, a.RemoteResponse, a.Error }).ToList()
            })
            .ToListAsync(ct);

        return items.Select(it => new MessageTraceItem(it.Id, it.MessageIdHeader, it.EnvelopeFrom, it.EnvelopeTo,
            it.MessageIdHeader, it.CreatedAtUtc, it.State, it.Attempts, it.LastError,
            it.History.OrderBy(a => a.AttemptNumber).Select(a => new TraceAttempt(
                a.AttemptNumber, a.StartedAtUtc, a.FinishedAtUtc, a.Success, a.RemoteResponse, a.Error)).ToList())).ToList();
    }
}

/// <summary>Registro de auditoría (sección 25).</summary>
public class AuditService : IAuditService
{
    private readonly IApplicationDbContext _db;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IApplicationDbContext db, ILogger<AuditService> logger) { _db = db; _logger = logger; }

    public async Task RecordAsync(string action, string? actor, string? actorId, string? ip, string? target,
        string? targetId, string? result, string? metadata = null, CancellationToken ct = default)
    {
        var ev = new Domain.Entities.AuditEvent
        {
            Action = action, Actor = actor, ActorId = actorId, IpAddress = ip,
            Target = target, TargetId = targetId, Result = result, Metadata = metadata
        };
        await _db.AuditEvents.AddAsync(ev, ct);
        try { await _db.SaveChangesAsync(ct); }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo persistir evento de auditoría {Action}", action); }
    }

    public async Task<IReadOnlyList<AuditItem>> ListRecentAsync(int take = 200, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 1000);
        var list = await _db.AuditEvents.AsNoTracking().OrderByDescending(e => e.TimestampUtc).Take(take)
            .Select(e => new AuditItem(e.Id, e.TimestampUtc, e.Actor, e.IpAddress, e.Action, e.Target, e.TargetId, e.Result, e.Metadata))
            .ToListAsync(ct);
        return list;
    }
}

/// <summary>Dashboard administrativo (sección 26).</summary>
public class AdminDashboardService : IAdminDashboardService
{
    private readonly IApplicationDbContext _db;

    public AdminDashboardService(IApplicationDbContext db) => _db = db;

    public async Task<DashboardDto> GetAsync(CancellationToken ct = default)
    {
        int domainCount = await _db.Domains.CountAsync(ct);
        int userCount = await _db.Users.CountAsync(ct);
        int mailboxCount = await _db.Mailboxes.CountAsync(ct);
        long totalMessages = await _db.Messages.LongCountAsync(ct);
        long storage = await _db.Mailboxes.AsNoTracking().SumAsync(m => (long?)m.UsedBytes, ct) ?? 0;
        int pending = await _db.DeliveryQueue.CountAsync(q => q.State == DeliveryState.Pending, ct);
        int processing = await _db.DeliveryQueue.CountAsync(q => q.State == DeliveryState.Processing, ct);
        int deferred = await _db.DeliveryQueue.CountAsync(q => q.State == DeliveryState.Deferred, ct);
        int failed = await _db.DeliveryQueue.CountAsync(q => q.State == DeliveryState.Failed || q.State == DeliveryState.DeadLetter, ct);
        int spam = await _db.Messages.CountAsync(m => m.SpamDecision == SpamDecision.Spam, ct);
        int quarantine = await _db.Messages.CountAsync(m => m.SpamDecision == SpamDecision.Quarantine, ct);
        int audit = await _db.AuditEvents.CountAsync(ct);
        return new DashboardDto(domainCount, userCount, mailboxCount, totalMessages, storage,
            new QueueHealth(pending, processing, deferred, failed), spam, quarantine, audit);
    }
}

/// <summary>Clasificador de spam determinista y auditable (sección 20) — usado por tests y pipeline.</summary>
public static class SpamScoring
{
    /// <summary>Calcula un score básico sin dependencia externa.</summary>
    public static double Score(bool hasFrom, bool emptyBody, long size, string text, bool authenticated)
    {
        double score = 0.0;
        if (!hasFrom) score += 1.5;
        if (emptyBody) score += 1.0;
        if (size < 512) score += 0.5;
        var lower = text.ToLowerInvariant();
        foreach (var kw in new[] { "viagra", "lottery", "crypto giveaway", "free money" })
            if (lower.Contains(kw)) score += 1.5;
        if (authenticated) score = Math.Min(score, 0.5);
        return score;
    }
}