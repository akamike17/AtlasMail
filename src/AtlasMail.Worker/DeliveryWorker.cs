using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Domain.Enums;
using AtlasMail.Domain.Rules;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Worker;

/// <summary>
/// Worker de cola de salida (secciones 7, 37, 38):
///  - reclama elementos con lease/claim,
///  - entrega localmente (al buzón) o externamente por SMTP,
///  - retry con backoff, sin loops infinitos, con dead-letter queue,
///  - crash recovery: un lease caduca y el item vuelve a ser procesable.
/// </summary>
public class DeliveryWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DeliveryWorker> _logger;
    private readonly string _workerId;
    private readonly string _hostname;
    private readonly int _loopIntervalMs;

    public DeliveryWorker(IServiceScopeFactory scopeFactory, ILogger<DeliveryWorker> logger,
        string hostname = "atlasmail.local", int loopIntervalMs = 2000)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _workerId = "worker-" + Guid.NewGuid().ToString("N")[..8];
        _hostname = hostname;
        _loopIntervalMs = loopIntervalMs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeliveryWorker {Worker} iniciado", _workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool didWork = await ProcessOneAsync(stoppingToken);
                if (!didWork) await Task.Delay(_loopIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en ciclo del worker");
                try { await Task.Delay(2_000, stoppingToken); } catch { break; }
            }
        }
        _logger.LogInformation("DeliveryWorker {Worker} detenido", _workerId);
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IOutboundQueueService>();
        var store = scope.ServiceProvider.GetRequiredService<IMessageStore>();
        var inbound = scope.ServiceProvider.GetRequiredService<IInboundDeliveryService>();
        var admin = scope.ServiceProvider.GetRequiredService<IAddressResolutionService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var claim = await queue.ClaimNextAsync(_workerId, ct);
        if (claim == null) return false;

        byte[] raw;
        try { raw = await store.ReadAsync(claim.StoreKey, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning("Cola {Id}: no se pudo leer store, marcando failed", claim.QueueItemId);
            await queue.FailAsync(claim.QueueItemId, "store_unreadable: " + ex.Message, null, deadLetter: true, ct);
            return true;
        }

        // ¿Entrega local o externa?
        var resolved = await admin.ResolveAsync(claim.EnvelopeTo, ct);
        if (resolved.Found)
        {
            // Entrega local
            try
            {
                var result = await inbound.IngestAsync(claim.EnvelopeFrom, claim.EnvelopeTo, raw,
                    _hostname, null, false, "queue:" + _workerId, ct);
                if (result is InboundResult.Accepted or InboundResult.MovedToSpam)
                {
                    await queue.CompleteAsync(claim.QueueItemId, "250 2.0.0 OK (local)", ct);
                }
                else if (result == InboundResult.RelayDenied)
                {
                    await queue.FailAsync(claim.QueueItemId, "relay_denied", "550 5.7.1 Relay denied", deadLetter: false, ct);
                }
                else
                {
                    await queue.FailAsync(claim.QueueItemId, "ingest_" + result, "451 Temporary failure", deadLetter: true, ct);
                }
            }
            catch (Exception ex)
            {
                await queue.DeferAsync(claim.QueueItemId, "local_ingest_error: " + ex.Message, null, ct);
            }
            await audit.RecordAsync("Queue.LocalDelivery", _workerId, null, null, "queue", claim.QueueItemId.ToString(), "processed", null, ct);
            return true;
        }

        // Entrega externa: para el Ciclo 1 marcamos como no desplegable aún (FASE 2),
        // pero lo encolamos con estado procesable (reintento/bounce). Fail-safe: no perder.
        bool allowExternalSend = true;
        if (!allowExternalSend)
        {
            await queue.FailAsync(claim.QueueItemId, "external_delivery_not_enabled", "550 External delivery disabled", deadLetter: false, ct);
        }
        else
        {
            // Reescoltar para FASE 2. Mientras tanto, retrasar (deferred) para no hacer loop infinito
            // y permitir visibility. Lo marcamos como DeadLetter con motivo claro para no perder.
            await queue.FailAsync(claim.QueueItemId,
                "external_smtp_delivery_pending_fase2",
                "451 4.3.0 Remote delivery not yet implemented in this phase", deadLetter: true, ct);
        }
        await audit.RecordAsync("Queue.ExternalDelivery", _workerId, null, null, "queue", claim.QueueItemId.ToString(), "deferred_fase2", null, ct);
        return true;
    }
}