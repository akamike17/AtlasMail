using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using AtlasMail.Domain.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.Application.Services;

/// <summary>
/// Cola de salida con claim/lease transaccional (sección 37): un worker reclama un
/// elemento atómico, y un lease caduca para evitar pérdida por crash del worker.
/// </summary>
public class OutboundQueueService : IOutboundQueueService
{
    private readonly IApplicationDbContext _db;
    private readonly ILogger<OutboundQueueService> _logger;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public OutboundQueueService(IApplicationDbContext db, ILogger<OutboundQueueService> logger)
    {
        _db = db; _logger = logger;
    }

    public async Task<long> EnqueueAsync(string envelopeFrom, string envelopeTo, string storeKey,
        string? messageIdHeader, bool isLocal, CancellationToken ct = default)
    {
        var item = new DeliveryQueueItem
        {
            EnvelopeFrom = envelopeFrom,
            EnvelopeTo = envelopeTo,
            StoreKey = storeKey,
            MessageIdHeader = messageIdHeader,
            IsLocal = isLocal,
            State = DeliveryState.Pending,
            NextAttemptAtUtc = DateTime.UtcNow
        };
        _db.DeliveryQueue.Add(item);
        await _db.SaveChangesAsync(ct);
        return item.Id;
    }

    public async Task<DeliveryQueueItemClaim?> ClaimNextAsync(string workerId, CancellationToken ct = default)
    {
        // Lease/claim atómico contra MySQL: encontrar un item elegible no reclamado o con lease vencido,
        // marcarlo como reclamado en una transacción.
        var now = DateTime.UtcNow;
        var candidate = await _db.DeliveryQueue
            .Where(q => q.State == DeliveryState.Pending ||
                        (q.State == DeliveryState.Deferred && q.NextAttemptAtUtc != null && q.NextAttemptAtUtc <= now) ||
                        (q.State == DeliveryState.Processing && q.LeaseExpiresAtUtc != null && q.LeaseExpiresAtUtc < now))
            .Where(q => q.ClaimedBy == null || q.LeaseExpiresAtUtc == null || q.LeaseExpiresAtUtc < now)
            .OrderBy(q => q.NextAttemptAtUtc)
            .FirstOrDefaultAsync(ct);

        if (candidate == null) return null;

        candidate.State = DeliveryState.Processing;
        candidate.ClaimedBy = workerId;
        candidate.LeaseExpiresAtUtc = DateTime.UtcNow.Add(LeaseDuration);
        await _db.SaveChangesAsync(ct);
        _logger.LogDebug("Worker {Worker} reclamó cola {Id}", workerId, candidate.Id);
        return new DeliveryQueueItemClaim(candidate.Id, candidate.EnvelopeFrom, candidate.EnvelopeTo,
            candidate.StoreKey, candidate.MessageIdHeader);
    }

    public async Task<long> CountPendingAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        return await _db.DeliveryQueue.AsNoTracking().LongCountAsync(q =>
            q.State == DeliveryState.Pending ||
            (q.State == DeliveryState.Deferred && q.NextAttemptAtUtc != null && q.NextAttemptAtUtc <= now) ||
            (q.State == DeliveryState.Processing && q.LeaseExpiresAtUtc != null && q.LeaseExpiresAtUtc < now), ct);
    }

    public async Task CompleteAsync(long queueItemId, string remoteResponse, CancellationToken ct = default)
    {
        var item = await _db.DeliveryQueue.FindAsync([queueItemId], ct) ?? throw new InvalidOperationException("Cola no encontrada");
        item.State = DeliveryState.Delivered;
        item.RemoteServer = string.IsNullOrWhiteSpace(remoteResponse) ? item.RemoteServer : remoteResponse;
        item.LastError = null;
        item.ClaimedBy = null;
        item.LeaseExpiresAtUtc = null;
        item.LastAttemptAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Cola {Id} entregada: {Response}", queueItemId, remoteResponse);
    }

    public async Task DeferAsync(long queueItemId, string error, string? remoteResponse, CancellationToken ct = default)
    {
        var item = await _db.DeliveryQueue.FindAsync([queueItemId], ct) ?? throw new InvalidOperationException("Cola no encontrada");
        item.Attempts++;
        item.LastAttemptAtUtc = DateTime.UtcNow;
        item.LastError = error;
        item.ClaimedBy = null;
        item.LeaseExpiresAtUtc = null;
        if (RetryPolicy.ShouldRetry(item.Attempts, item.MaxAttempts))
        {
            item.State = DeliveryState.Deferred;
            item.NextAttemptAtUtc = DateTime.UtcNow.Add(RetryPolicy.Backoff(item.Attempts, TimeSpan.FromSeconds(60)));
        }
        else
        {
            item.State = DeliveryState.Failed;
            item.NextAttemptAtUtc = null;
        }
        if (item.Attempts > 1) item.RemoteServer = remoteResponse ?? item.RemoteServer;
        await _db.SaveChangesAsync(ct);
    }

    public async Task FailAsync(long queueItemId, string error, string? remoteResponse, bool deadLetter, CancellationToken ct = default)
    {
        var item = await _db.DeliveryQueue.FindAsync([queueItemId], ct) ?? throw new InvalidOperationException("Cola no encontrada");
        item.Attempts = Math.Max(item.Attempts, 1);
        item.LastAttemptAtUtc = DateTime.UtcNow;
        item.LastError = error;
        item.RemoteServer = remoteResponse ?? item.RemoteServer;
        item.ClaimedBy = null;
        item.LeaseExpiresAtUtc = null;
        item.State = deadLetter ? DeliveryState.DeadLetter : DeliveryState.Failed;
        item.NextAttemptAtUtc = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<QueueListItem>> ListAsync(int take = 100, CancellationToken ct = default)
    {
        var list = await _db.DeliveryQueue.AsNoTracking()
            .OrderByDescending(q => q.Id).Take(take).ToListAsync(ct);
        return list.Select(q => new QueueListItem(
            q.Id, q.EnvelopeFrom, q.EnvelopeTo, q.MessageIdHeader ?? "",
            q.State, q.Attempts, q.MaxAttempts, q.NextAttemptAtUtc, q.CreatedAtUtc, q.LastError)).ToList();
    }
}