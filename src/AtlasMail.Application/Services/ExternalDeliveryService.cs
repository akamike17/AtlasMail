using AtlasMail.Application.Abstractions;
using AtlasMail.Domain.Rules;
using AtlasMail.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Application.Services;

/// <summary>
/// Entrega externa (FASE 2, secciones 7 y 8): dado un mensaje encolado con destino
/// externo, resuelve MX, conecta por SMTP, aplica política y rate limits, y clasifica
/// el resultado (reintentar / fallo permanente / denegado / sin MX).
/// Independiente de infraestructura: usa abstracciones (IMxResolver,
/// IExternalMailSender, IExternalDeliveryPolicy) y rule pura de rate limit.
/// </summary>
public sealed class ExternalDeliveryService
{
    private readonly IMxResolver _mx;
    private readonly IExternalMailSender _sender;
    private readonly IExternalDeliveryPolicy _policy;
    private readonly ExternalDeliverySettings _settings;
    private readonly SlidingWindowRateLimiter _limiter;
    private readonly string _heloName;
    private readonly ILogger<ExternalDeliveryService> _logger;

    public ExternalDeliveryService(
        IMxResolver mx,
        IExternalMailSender sender,
        IExternalDeliveryPolicy policy,
        ExternalDeliverySettings settings,
        SlidingWindowRateLimiter? limiter = null,
        ILogger<ExternalDeliveryService>? logger = null)
    {
        _mx = mx; _sender = sender; _policy = policy;
        _settings = settings;
        _limiter = limiter ?? new SlidingWindowRateLimiter(60);
        _heloName = string.IsNullOrWhiteSpace(settings.HeloName) ? "atlasmail.local" : settings.HeloName;
        _logger = logger ?? AtlasMail.Application.NullLogger<ExternalDeliveryService>.Instance;
    }

    /// <summary>
    /// Intenta entregar un mensaje encolado a un destino externo.
    /// Retorna el outcome con la clasificación que el worker debe aplicar.
    /// </summary>
    public async Task<ExternalDeliveryOutcome> DeliverAsync(string envelopeFrom, string envelopeTo,
        byte[] rawMime, CancellationToken ct = default)
    {
        var fromDomain = await SenderDomainAsync(envelopeFrom, ct);
        if (!string.IsNullOrEmpty(fromDomain) && !await _policy.AllowDomainExternalSendAsync(fromDomain, ct))
        {
            _logger.LogWarning("Política deniega envío externo desde {From}", envelopeFrom);
            return new ExternalDeliveryOutcome(false, "550 5.7.1 Domain not allowed external send", ExternalOutcomeKind.PolicyDenied);
        }

        if (!EmailAddress.TryParse(envelopeTo, out var rcpt))
            return new ExternalDeliveryOutcome(false, "550 5.1.3 Invalid recipient", ExternalOutcomeKind.PermanentFailure);

        // Rate limit por remitente y por dominio destino
        if (_settings.RateLimitPerMinute > 0 && !_limiter.Check("user:" + envelopeFrom, _settings.RateLimitPerMinute).Allowed)
            return new ExternalDeliveryOutcome(false, "421 4.7.0 Too many messages from sender", ExternalOutcomeKind.TemporaryFailure);
        if (_settings.RateLimitPerDomainPerMinute > 0 && !_limiter.Check("domain:" + rcpt.Domain, _settings.RateLimitPerDomainPerMinute).Allowed)
            return new ExternalDeliveryOutcome(false, "421 4.7.0 Too many messages to domain", ExternalOutcomeKind.TemporaryFailure);

        // Resolución MX (con fallback a A en el resolutor)
        var exchanges = await _mx.ResolveAsync(rcpt.Domain, ct);
        if (exchanges.Count == 0)
        {
            _logger.LogWarning("Sin MX para {Domain}", rcpt.Domain);
            return new ExternalDeliveryOutcome(false, "DNS: no MX or A record for " + rcpt.Domain, ExternalOutcomeKind.NoMxEntry);
        }

        var options = new SmtpSendOptions(
            StartTlsRequired: _settings.StartTlsRequiredForExternal,
            Username: null,
            Password: null);

        // Intentar en orden de preferencia; el primer host que responda 2xx/5xx decide.
        foreach (var mx in exchanges)
        {
            if (ct.IsCancellationRequested)
                return new ExternalDeliveryOutcome(false, "Cancelled", ExternalOutcomeKind.TemporaryFailure);

            var host = mx.Host;
            int port = mx.Port == 0 ? 25 : mx.Port;
            // Si el "host" es una IP (fallback A), ya viene como tal.
            _logger.LogDebug("Entregando {To} via {Host}:{Port}", envelopeTo, host, port);

            var result = await _sender.SendAsync(host, port, envelopeFrom, new[] { envelopeTo },
                rawMime, _heloName, options, ct);

            if (result.Success)
                return new ExternalDeliveryOutcome(true, result.Response, ExternalOutcomeKind.Delivered);

            if (result.Permanent)
                return new ExternalDeliveryOutcome(false, result.Response, ExternalOutcomeKind.PermanentFailure);

            // Temporal: probar siguiente MX si lo hay; si es el último, reintentar.
            _logger.LogDebug("Falló temporal hacia {Host}: {Resp}", host, result.Response);
        }

        return new ExternalDeliveryOutcome(false, "All MX hosts failed (temporary)", ExternalOutcomeKind.TemporaryFailure);
    }

    private static async Task<string?> SenderDomainAsync(string envelopeFrom, CancellationToken ct)
    {
        await Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(envelopeFrom) || envelopeFrom.Trim() == "<>") return null;
        var candidate = envelopeFrom.Trim().Replace("<", "").Replace(">", "");
        return EmailAddress.TryParse(candidate, out var a) ? a.Domain : null;
    }
}