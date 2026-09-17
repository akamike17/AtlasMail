using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Services;
using AtlasMail.Domain.Enums;
using AtlasMail.Domain.ValueObjects;
using AtlasMail.Infrastructure.EmailAuth;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Worker;

/// <summary>
/// Worker de cola de salida (secciones 7, 37, 38 + FASE 2):
///  - reclama elementos con lease/claim,
///  - entrega localmente (al buzón) o externamente por SMTP (resolución MX real),
///  - clasifica 4xx (reintenta) vs 5xx (fallo y bounce/DSN seguro sin backscatter),
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
    private readonly int _concurrency;

    public DeliveryWorker(IServiceScopeFactory scopeFactory, ILogger<DeliveryWorker> logger,
        string hostname = "atlasmail.local", int loopIntervalMs = 2000, int concurrency = 4)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _workerId = "worker-" + Guid.NewGuid().ToString("N")[..8];
        _hostname = hostname;
        _loopIntervalMs = loopIntervalMs;
        _concurrency = Math.Clamp(concurrency, 1, 64);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DeliveryWorker {Worker} iniciado (concurrency={C})", _workerId, _concurrency);
        using var metricScope = _scopeFactory.CreateScope();
        var metrics = metricScope.ServiceProvider.GetService<IMetricsRegistry>();
        var queueProbe = metricScope.ServiceProvider.GetService<IOutboundQueueService>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var tasks = new List<Task<bool>>(_concurrency);
                for (int i = 0; i < _concurrency; i++)
                {
                    var t = SafeProcessAsync(stoppingToken);
                    tasks.Add(t);
                }
                var results = await Task.WhenAll(tasks);

                // gauge de profundidad de cola pendiente (sin datos sensibles)
                if (metrics != null && queueProbe != null)
                {
                    try
                    {
                        metrics.SetGauge("queue.pending", await queueProbe.CountPendingAsync(stoppingToken));
                        metrics.SetGauge("worker.concurrency", _concurrency);
                        metrics.SetGauge("worker.last_heartbeat_unix",
                            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    }
                    catch { /* gauge opcional */ }
                }

                // si nada quedó por hacer, esperar antes de re-sondear (evita spin a alta velocidad)
                if (results.Length == 0 || !results.Any(did => did))
                    await Task.Delay(_loopIntervalMs, stoppingToken);
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

    private async Task<bool> SafeProcessAsync(CancellationToken ct)
    {
        try { return await ProcessOneAsync(ct); }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando un elemento de la cola");
            return true; // hubo un claim; no apresurar el ciclo (reintenta por lease)
        }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var queue = scope.ServiceProvider.GetRequiredService<IOutboundQueueService>();
        var store = scope.ServiceProvider.GetRequiredService<IMessageStore>();
        var inbound = scope.ServiceProvider.GetRequiredService<IInboundDeliveryService>();
        var admin = scope.ServiceProvider.GetRequiredService<IAddressResolutionService>();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        var external = scope.ServiceProvider.GetService<ExternalDeliveryService>();
        var metrics = scope.ServiceProvider.GetService<IMetricsRegistry>();

        var claim = await queue.ClaimNextAsync(_workerId, ct);
        if (claim == null) return false;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        byte[] raw;
        try { raw = await store.ReadAsync(claim.StoreKey, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning("Cola {Id}: no se pudo leer store, marcando failed", claim.QueueItemId);
            await queue.FailAsync(claim.QueueItemId, "store_unreadable: " + ex.Message, null, deadLetter: true, ct);
            return true;
        }

        // FASE 4: firmar DKIM en salida si el dominio del remitente lo tiene habilitado (§18)
        var dkimSigner = scope.ServiceProvider.GetService<DkimOutboundSigner>();
        if (dkimSigner != null)
        {
            try { raw = await dkimSigner.SignIfEnabledAsync(claim.EnvelopeFrom, raw, ct); }
            catch (Exception ex) { _logger.LogDebug("DKIM outbound skip para {Id}: {Msg}", claim.QueueItemId, ex.Message); }
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

        // Entrega externa (FASE 2): se requiere el servicio habilitado.
        if (external == null)
        {
            await queue.FailAsync(claim.QueueItemId, "external_delivery_not_enabled", "550 External delivery disabled", deadLetter: true, ct);
            await audit.RecordAsync("Queue.ExternalDelivery", _workerId, null, null, "queue", claim.QueueItemId.ToString(), "disabled", null, ct);
            return true;
        }

        ExternalDeliveryOutcome outcome;
        try
        {
            outcome = await external.DeliverAsync(claim.EnvelopeFrom, claim.EnvelopeTo, raw, ct);
        }
        catch (Exception ex)
        {
            // Error interno de protocolo/DNS → reintentar (temporal)
            _logger.LogWarning("Error entregando {Id}: {Msg}", claim.QueueItemId, ex.Message);
            await queue.DeferAsync(claim.QueueItemId, "external_error: " + ex.Message, null, ct);
            return true;
        }

        switch (outcome.Kind)
        {
            case ExternalOutcomeKind.Delivered:
                await queue.CompleteAsync(claim.QueueItemId, outcome.RemoteResponse ?? "250 2.0.0 OK", ct);
                var successRcpt = claim.EnvelopeTo;
                await audit.RecordAsync("Queue.ExternalDelivery", _workerId, null, null, "queue", claim.QueueItemId.ToString(), "delivered", "to=" + successRcpt, ct);
                break;

            case ExternalOutcomeKind.TemporaryFailure:
            case ExternalOutcomeKind.NoMxEntry:
                await queue.DeferAsync(claim.QueueItemId,
                    (outcome.Kind == ExternalOutcomeKind.NoMxEntry ? "no_mx_entry: " : "temporary: ") + (outcome.RemoteResponse ?? "retry"),
                    outcome.RemoteResponse, ct);
                break;

            case ExternalOutcomeKind.PermanentFailure:
                // Fallo 5xx permanente: marco failed y genero un bounce/DSN seguro
                await queue.FailAsync(claim.QueueItemId, "permanent: " + outcome.RemoteResponse, outcome.RemoteResponse, deadLetter: false, ct);
                await SafelyBounceAsync(claim, outcome.RemoteResponse, queue, inbound, admin, scope, ct);
                break;

            case ExternalOutcomeKind.PolicyDenied:
                await queue.FailAsync(claim.QueueItemId, "policy_denied: " + outcome.RemoteResponse, outcome.RemoteResponse, deadLetter: false, ct);
                break;
        }
        await audit.RecordAsync("Queue.ExternalDelivery", _workerId, null, null, "queue", claim.QueueItemId.ToString(),
            outcome.Kind.ToString(), outcome.RemoteResponse, ct);
        sw.Stop();
        // FASE 7: métricas de observabilidad (sin datos sensibles)
        metrics?.Observe("delivery_timing", sw.Elapsed);
        metrics?.Increment("mail.delivered");
        switch (outcome.Kind)
        {
            case ExternalOutcomeKind.TemporaryFailure:
            case ExternalOutcomeKind.NoMxEntry:
                metrics?.Increment("mail.deferred");
                break;
            case ExternalOutcomeKind.PermanentFailure:
            case ExternalOutcomeKind.PolicyDenied:
                metrics?.Increment("mail.failed");
                break;
        }
        return true;
    }

    /// <summary>
    /// Genera un DSN/bounce por fallo permanente SOLO si el envelope remitente es un buzón
    /// local válido (previene backscatter a direcciones externas inventadas o a internet).
    /// Nunca rebota si el remitente es null (&lt;&gt;), porque rebotar un bounce es bucle.
    /// </summary>
    private async Task SafelyBounceAsync(DeliveryQueueItemClaim claim, string? remoteResponse,
        IOutboundQueueService queue, IInboundDeliveryService inbound, IAddressResolutionService admin,
        IServiceScope scope, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(claim.EnvelopeFrom) || claim.EnvelopeFrom.Trim() == "<>" || claim.EnvelopeFrom.Trim() == "MAILFROM:<>")
        {
            _logger.LogInformation("Cola {Id}: remitente null, no se genera bounce (anti-backscatter)", claim.QueueItemId);
            return;
        }
        var candidate = claim.EnvelopeFrom.Trim().Replace("<", "").Replace(">", "");
        if (!EmailAddress.TryParse(candidate, out _))
        {
            _logger.LogInformation("Cola {Id}: remitente inválido, sin bounce", claim.QueueItemId);
            return;
        }
        var sender = await admin.ResolveAsync(candidate, ct);
        if (!sender.Found || !sender.MailboxId.HasValue)
        {
            _logger.LogInformation("Cola {Id}: remitente no-local/inválido, sin bounce (anti-backscatter)", claim.QueueItemId);
            return;
        }

        // Construir DSN minimalista (no reenviar adjuntos ni contenido completo)
        var dsn = BuildDsn(candidate, claim.EnvelopeTo, remoteResponse ?? "Delivery permanently failed", _hostname);
        var act = scope.ServiceProvider.GetRequiredService<IAuditService>();
        try
        {
            var result = await inbound.IngestAsync("<>", candidate, dsn, _hostname, null, false, "bounce:" + _workerId, ct);
            _logger.LogInformation("Cola {Id}: bounce entregado a {From} (resultado {Result})", claim.QueueItemId, candidate, result);
            await act.RecordAsync("Bounce.Sent", _workerId, null, null, "queue", claim.QueueItemId.ToString(), candidate, result.ToString(), ct);
        }
        catch (Exception ex)
        {
            // No lanzamos: el item ya está marcado failed; el bounce es best-effort.
            _logger.LogWarning(ex, "Falló bounce a {From}", candidate);
            await act.RecordAsync("Bounce.Failed", _workerId, null, null, "queue", claim.QueueItemId.ToString(), candidate, ex.Message, ct);
        }
    }

    private static byte[] BuildDsn(string originalFrom, string failedRcpt, string reason, string hostname = "atlasmail.local")
    {
        var date = DateTimeOffset.UtcNow.ToString("R");
        var text =
            $"Date: {date}\r\n" +
            $"From: Mail Delivery Subsystem <MAILER-DAEMON@{hostname}>\r\n" +
            $"To: {originalFrom}\r\n" +
            $"Subject: Delivery Status Notification (Failure)\r\n" +
            $"Message-ID: <{Guid.NewGuid():N}@atlasmail>\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            "MIME-Version: 1.0\r\n" +
            "Auto-Submitted: auto-replied\r\n\r\n" +
            $"This is the AtlasMail mail delivery system.\r\n\r\n" +
            $"The following recipient could not be delivered:\r\n  {failedRcpt}\r\n\r\n" +
            $"Remote response: {reason}\r\n\r\n" +
            "No additional content is included to prevent amplification/backscatter.\r\n";
        return System.Text.Encoding.UTF8.GetBytes(text);
    }
}