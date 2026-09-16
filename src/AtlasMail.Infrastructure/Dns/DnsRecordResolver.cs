using AtlasMail.Application.Abstractions;
using DnsClient;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Infrastructure.Dns;

/// <summary>
/// Resolución de registros DNS para autenticación de correo (FASE 4):
/// TXT (SPF/DKIM/DMARC) y direcciones A/AAAA (mecanismos SPF a/mx).
/// </summary>
public sealed class DnsRecordResolver : IDnsRecordResolver
{
    private readonly LookupClient _dns;
    private readonly ILogger _logger;

    public DnsRecordResolver(TimeSpan? timeout = null, ILogger? logger = null)
    {
        _dns = new LookupClient(new LookupClientOptions
        {
            UseCache = true,
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            Retries = 1,
            ThrowDnsErrors = false,
            UseTcpOnly = false,
            UseTcpFallback = true,
        });
        _logger = logger ?? AtlasMail.Infrastructure.NullLogger<DnsRecordResolver>.Instance;
    }

    public DnsRecordResolver(LookupClientOptions options, ILogger? logger = null)
    {
        _dns = new LookupClient(options);
        _logger = logger ?? AtlasMail.Infrastructure.NullLogger<DnsRecordResolver>.Instance;
    }

    public async Task<IReadOnlyList<string>> GetTxtAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var resp = await _dns.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
            var records = resp.Answers.TxtRecords().SelectMany(r => r.Text).ToList();
            _logger.LogDebug("TXT {Name} -> {N} fragmentos", name, records.Count);
            return records;
        }
        catch (OperationCanceledException) { return Array.Empty<string>(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error TXT {Name}", name);
            return Array.Empty<string>();
        }
    }

    public async Task<IReadOnlyList<string>> GetAddressesAsync(string hostname, CancellationToken ct = default)
    {
        var result = new List<string>();
        foreach (var qtype in new[] { QueryType.A, QueryType.AAAA })
        {
            try
            {
                var resp = await _dns.QueryAsync(hostname, qtype, cancellationToken: ct);
                foreach (var a in resp.Answers.ARecords()) result.Add(a.Address.ToString());
                foreach (var a in resp.Answers.AaaaRecords()) result.Add(a.Address.ToString());
            }
            catch (OperationCanceledException) { }
            catch { /* best effort */ }
        }
        return result;
    }
}