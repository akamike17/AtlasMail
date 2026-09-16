using AtlasMail.Application.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Web.Smtp;

/// <summary>
/// Health check de DNS (FASE 2): resuelve periódicamente el MX de un dominio conocido
/// para reportar si la resolución de registros MX funciona (secci�n 32). Reporta al
/// IHealthCheckSubscriber para /api/health y /health.
/// </summary>
public class DnsHealthProbe : BackgroundService
{
    private readonly IMxResolver _mx;
    private readonly IHealthCheckSubscriber _health;
    private readonly ILogger<DnsHealthProbe> _logger;
    private readonly string _probeDomain;
    private readonly TimeSpan _interval;

    public DnsHealthProbe(IMxResolver mx, IHealthCheckSubscriber health, ILogger<DnsHealthProbe> logger,
        string probeDomain = "gmail.com", TimeSpan? interval = null)
    {
        _mx = mx; _health = health; _logger = logger;
        _probeDomain = probeDomain;
        _interval = interval ?? TimeSpan.FromSeconds(120);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Primer chequeo inmediato + luego periódico
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var exchanges = await _mx.ResolveAsync(_probeDomain, stoppingToken);
                bool ok = exchanges.Count > 0;
                _health.Report("dns", ok);
                _logger.LogInformation("DNS health probe {Domain} -> {State} ({Count} MX)", _probeDomain, ok ? "OK" : "FAIL", exchanges.Count);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _health.Report("dns", false);
                _logger.LogWarning(ex, "DNS health probe {Domain} falló", _probeDomain);
            }
            try { await Task.Delay(_interval, stoppingToken); } catch { break; }
        }
    }
}