using AtlasMail.Protocols.Imap;
using Microsoft.Extensions.DependencyInjection;

namespace AtlasMail.Web.Imap;

/// <summary>Inicia/detiene el servidor IMAP (FASE 3) si está configurado como hosted service.</summary>
public class ImapHostedService : IHostedService
{
    private readonly ImapServer? _server;
    private readonly Smtp.IHealthCheckSubscriber _health;
    private readonly ILogger<ImapHostedService> _logger;

    public ImapHostedService(ImapServer? server, Smtp.IHealthCheckSubscriber health, ILogger<ImapHostedService> logger)
    {
        _server = server; _health = health; _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_server != null)
        {
            _server.Start();
            _health.Report("imap", _server.Running);
            _logger.LogInformation("Servidor IMAP iniciado en puerto {Port}", _server.EffectivePort);
        }
        else
        {
            _health.Report("imap", false);
            _logger.LogInformation("Servidor IMAP desactivado (Imap:Enabled != 1)");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server != null) await _server.DisposeAsync();
        _health.Report("imap", false);
    }
}