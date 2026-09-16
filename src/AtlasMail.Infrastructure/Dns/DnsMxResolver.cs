using AtlasMail.Application.Abstractions;
using DnsClient;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Infrastructure.Dns;

/// <summary>
/// Resolutor MX real (FASE 2, sección 8) usando consultas DNS estándar.
///  - Consulta los registros MX del dominio, ordenados por preferencia.
///  - Fallback RFC 5321 §5.1: si no hay MX, usa el registro A.
///  - Raise/URP: devuelve lista vacía si el dominio no es resolubles (el worker aplaza).
/// Timeouts estrictos configurables.
/// </summary>
public sealed class DnsMxResolver : IMxResolver
{
    private readonly LookupClient _dns;
    private readonly ILogger _logger;

    public DnsMxResolver(TimeSpan? timeout = null, ILogger? logger = null)
    {
        var opts = new LookupClientOptions
        {
            UseCache = true,
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            Retries = 1,
            ThrowDnsErrors = false,
            UseTcpOnly = false,
            UseTcpFallback = true,
        };
        _dns = new LookupClient(opts);
        _logger = logger ?? AtlasMail.Infrastructure.NullLogger<DnsMxResolver>.Instance;
    }

    public DnsMxResolver(LookupClientOptions options, ILogger? logger = null)
    {
        _dns = new LookupClient(options);
        _logger = logger ?? AtlasMail.Infrastructure.NullLogger<DnsMxResolver>.Instance;
    }

    public async Task<IReadOnlyList<MailExchange>> ResolveAsync(string domainName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(domainName)) return Array.Empty<MailExchange>();

        try
        {
            var response = await _dns.QueryAsync(domainName, QueryType.MX, cancellationToken: ct);
            var mx = response.Answers.MxRecords()?.ToList();
            if (mx != null && mx.Count > 0)
            {
                // Ordenar por preferencia ascendente (menor = mayor prioridad)
                var result = mx
                    .Select(r => new MailExchange(r.Exchange.Value.TrimEnd('.'), r.Preference, 25))
                    .OrderBy(r => r.Preference)
                    .ToList();
                _logger.LogDebug("MX {Domain} -> {Hosts}", domainName, string.Join(", ", result.Select(r => $"{r.Preference}:{r.Host}")));
                return result;
            }

            // Fallback RFC 5321: dominio sin MX se entrega a su registro A
            var a = await _dns.QueryAsync(domainName, QueryType.A, cancellationToken: ct);
            var ar = a.Answers.ARecords()?.FirstOrDefault();
            if (ar != null)
            {
                _logger.LogDebug("MX {Domain} vacío; fallback A -> {Ip}", domainName, ar.Address);
                return new[] { new MailExchange(ar.Address.ToString(), 0, 25) };
            }

            _logger.LogWarning("Sin MX ni A reportados para {Domain}", domainName);
            return Array.Empty<MailExchange>();
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<MailExchange>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error resolviendo MX de {Domain}", domainName);
            return Array.Empty<MailExchange>();
        }
    }
}