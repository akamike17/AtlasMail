using AtlasMail.Protocols.Smtp;

namespace AtlasMail.Web.Smtp;

/// <summary>Inicia/detiene el SmtpServer (si está configurado) como hosted service.</summary>
public class SmtpHostedService : IHostedService
{
    private readonly SmtpServer? _server;
    private readonly IHealthCheckSubscriber _health;
    private readonly ILogger<SmtpHostedService> _logger;

    public SmtpHostedService(SmtpServer? server, IHealthCheckSubscriber health, ILogger<SmtpHostedService> logger)
    {
        _server = server;
        _health = health;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_server != null)
        {
            _server.Start();
            _health.Report("smtp", _server.Running);
            _logger.LogInformation("Servidor SMTP iniciado en puerto {Port}", _server.Port);
        }
        else
        {
            _health.Report("smtp", false);
            _logger.LogInformation("Servidor SMTP desactivado (Smtp:Enabled != 1)");
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server != null) await _server.DisposeAsync();
        _health.Report("smtp", false);
    }
}

/// <summary>Estado de salud de los componentes (sección 32).</summary>
public interface IHealthCheckSubscriber
{
    void Report(string component, bool healthy);
    IReadOnlyDictionary<string, bool> Snapshot();
}

public class HealthCheckSubscriber : IHealthCheckSubscriber
{
    private readonly Dictionary<string, bool> _state = new();
    private readonly object _lock = new();
    public void Report(string component, bool healthy)
    {
        lock (_lock) _state[component] = healthy;
    }
    public IReadOnlyDictionary<string, bool> Snapshot()
    {
        lock (_lock) return new Dictionary<string, bool>(_state);
    }
}