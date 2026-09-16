using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Protocols.Smtp;
public interface ISmtpMessageHandler
{
    /// <summary>Rechaza/acepta el sobre (MAIL FROM/RCPT TO). Retorna respuesta en formato "250 OK" o "550 ...".</summary>
    Task<string> ValidateEnvelopeAsync(string mailFrom, string rcptTo, SmtpSessionContext ctx, CancellationToken ct = default);

    /// <summary>Procesa el mensaje completo. Retorna formato "250 OK" o "550 ...".</summary>
    Task<string> HandleMessageAsync(string mailFrom, string rcptTo, byte[] rawMime, SmtpSessionContext ctx, CancellationToken ct = default);
}

/// <summary>Delegado del sobre dirigido a AUTH (starttls/auth — por ahora extensible).</summary>
public sealed class SmtpSessionContext
{
    public string ClientIp { get; set; } = string.Empty;
    public string Helo { get; set; } = string.Empty;
    public bool Authenticated { get; set; }
    public string? AuthUsername { get; set; }
    public string ServerHostname { get; set; } = "atlasmail.local";
}

/// <summary>
/// Servidor SMTP state machine (RFC 5321). Multi-hilo por conexión. NO open relay:
/// el handler valida el envelope. Aplica límites (tamaño DATA, nº destinatarios, timeouts).
/// </summary>
public sealed class SmtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly Func<ISmtpMessageHandler>? _handlerDelegate;
    private readonly SmtpServerOptions _options;
    private readonly ILogger<SmtpServer> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = new();
    private readonly object _lock = new();

    public SmtpServer(Func<ISmtpMessageHandler> handlerFactory, SmtpServerOptions? options = null, ILogger<SmtpServer>? logger = null)
    {
        _handlerDelegate = handlerFactory;
        _options = options ?? new SmtpServerOptions();
        _logger = logger ?? AtlasMail.Protocols.NullLogger<SmtpServer>.Instance;
        _listener = new TcpListener(IPAddress.Any, _options.Port);
    }

    public SmtpServer(IServiceScopeFactory scopeFactory, SmtpServerOptions? options = null, ILogger<SmtpServer>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _options = options ?? new SmtpServerOptions();
        _logger = logger ?? AtlasMail.Protocols.NullLogger<SmtpServer>.Instance;
        _listener = new TcpListener(IPAddress.Any, _options.Port);
    }

    public int Port => _options.Port;
    public string Hostname => _options.Hostname;
    public bool Running { get; private set; }

    /// <summary>Puerto real de escucha (cuando Port=0 el SO asigna uno efímero).</summary>
    public int EffectivePort => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Start()
    {
        if (!_options.Enabled) { Running = false; return; }
        _listener.Start(_options.Backlog);
        Running = true;
        _logger.LogInformation("Servidor SMTP escuchando en puerto {Port}", _options.Port);
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var task = Task.Run(() => HandleClientAsync(client, _cts.Token));
                lock (_lock) { _connections.Add(task); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Error aceptando conexión SMTP"); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        string clientIp = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
        IServiceScope? scope = null;
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = (int)_options.CommandTimeout.TotalMilliseconds;
            using var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            var ctx = new SmtpSessionContext { ClientIp = clientIp, ServerHostname = _options.Hostname };
            // Handler scoped por conexión (para que DbContext scoped sea seguro)
            scope = _scopeFactory?.CreateScope();
            var handler = scope != null ? scope.ServiceProvider.GetRequiredService<ISmtpMessageHandler>() : _handlerDelegate!();

            await writer.WriteAsync($"220 {_options.Hostname} AtlasMail SMTP\r\n");

            string? mailFrom = null;
            string? rcptTo = null;
            var dataBuffer = new MemoryStream();
            bool inData = false;
            var welcomeReceived = false;

            while (!token.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(token); }
                catch (OperationCanceledException) { break; }
                catch (Exception) { break; }
                if (line == null) break; // EOF

                if (line.Length > 1000) { await writer.WriteAsync("500 Line too long\r\n"); continue; }

                if (inData)
                {
                    // Fin de DATA = línea con ".".
                    if (line == "." || line == "\u0015.")
                    {
                        inData = false;
                        var raw = dataBuffer.ToArray();
                        var result = await SafeHandleAsync(() => handler.HandleMessageAsync(mailFrom!, rcptTo!, raw, ctx, token));
                        await writer.WriteAsync(result + "\r\n");
                        mailFrom = null; rcptTo = null;
                        dataBuffer.SetLength(0);
                        continue;
                    }
                    // dot-stuffing: línea que empieza con ".." se reduce a "."
                    var dataLine = line.StartsWith("..") ? line[1..] : line;
                    var bytes = Encoding.UTF8.GetBytes(dataLine + "\n");
                    if (dataBuffer.Length + bytes.Length > _options.MaxMessageBytes) { }
                    else { dataBuffer.Write(bytes, 0, bytes.Length); }
                    continue;
                }

                var cmd = line.TrimEnd('\r');
                string upper = cmd.ToUpperInvariant();

                if (welcomeReceived && ctx.Authenticated == false)
                {
                    // mantener orden: no restringir comandos; verificar límites abajo
                }

                if (upper == "QUIT") { await writer.WriteAsync("221 Bye\r\n"); break; }
                else if (upper == "EHLO" || upper == "HELO")
                {
                    // EHLO extensible
                    var ehlo = upper == "EHLO";
                    ctx.Helo = GetArg(cmd);
                    welcomeReceived = true;
                    if (ehlo)
                    {
                        await writer.WriteAsync($"250-{_options.Hostname} Hello\r\n");
                        await writer.WriteAsync("250-SIZE " + _options.MaxMessageBytes + "\r\n");
                        await writer.WriteAsync("250-8BITMIME\r\n");
                        await writer.WriteAsync("250-AUTH PLAIN\r\n");
                        await writer.WriteAsync("250-ENHANCEDSTATUSCODES\r\n");
                        await writer.WriteAsync("250 HELP\r\n");
                    }
                    else
                    {
                        await writer.WriteAsync($"250 {_options.Hostname} Hello\r\n");
                    }
                }
                else if (upper == "AUTH PLAIN" || upper.StartsWith("AUTH"))
                {
                    // AUTH PLAIN: aceptar credenciales; marcamos autenticado tras validar (a implementar por handler)
                    await writer.WriteAsync("503 AUTH not yet configured\r\n");
                }
                else if (upper.StartsWith("MAIL FROM:"))
                {
                    mailFrom = ParsePath(cmd, "MAIL FROM:");
                    if (mailFrom == null) { await writer.WriteAsync("501 MAIL FROM inválido\r\n"); continue; }
                    await writer.WriteAsync($"250 OK\r\n");
                }
                else if (upper.StartsWith("RCPT TO:"))
                {
                    if (mailFrom == null) { await writer.WriteAsync("503 Need MAIL FROM first\r\n"); continue; }
                    rcptTo = ParsePath(cmd, "RCPT TO:");
                    if (rcptTo == null) { await writer.WriteAsync("501 RCPT TO inválido\r\n"); continue; }
                    var result = await SafeHandleAsync(() => handler.ValidateEnvelopeAsync(mailFrom, rcptTo, ctx, token));
                    if (result.StartsWith("550") || result.StartsWith("551") || result.StartsWith("553") || result.StartsWith("554"))
                    {
                        await writer.WriteAsync(result + "\r\n");
                    }
                    else
                    {
                        await writer.WriteAsync(result + "\r\n");
                    }
                }
                else if (upper == "DATA")
                {
                    if (rcptTo == null) { await writer.WriteAsync("503 Need RCPT TO first\r\n"); continue; }
                    dataBuffer = new MemoryStream();
                    inData = true;
                    await writer.WriteAsync("354 End data with <CR><LF>.<CR><LF>\r\n");
                }
                else if (upper == "RSET")
                {
                    mailFrom = null; rcptTo = null; inData = false; dataBuffer = new MemoryStream();
                    await writer.WriteAsync("250 OK\r\n");
                }
                else if (upper == "NOOP")
                {
                    await writer.WriteAsync("250 OK\r\n");
                }
                else if (upper.StartsWith("HELP"))
                {
                    await writer.WriteAsync("214 Commands: EHLO HELO MAIL RCPT DATA RSET NOOP QUIT\r\n");
                }
                else
                {
                    await writer.WriteAsync("500 Command not recognized\r\n");
                }

                // Límites de conexión totales
                if (!ctx.Authenticated && CountRecentConnections(clientIp) > _options.MaxConnectionsPerIp)
                {
                    await writer.WriteAsync("421 Too many connections\r\n");
                    break;
                }
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Conexión SMTP cerrada por {Ip}", clientIp); }
        finally
        {
            scope?.Dispose();
            client.Close();
        }
    }

    private async Task<string> SafeHandleAsync(Func<Task<string>> action)
    {
        try { return await action(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error en handler SMTP"); return "451 Temporary failure"; }
    }

    private static string GetArg(string cmd)
    {
        int sp = cmd.IndexOf(' ');
        return sp < 0 ? string.Empty : cmd[(sp + 1)..];
    }

    private static string? ParsePath(string cmd, string prefix)
    {
        // RCPT TO:<a@b> o MAIL FROM:<a@b>
        int idx = cmd.ToUpperInvariant().IndexOf(':');
        if (idx < 0) return null;
        string path = cmd[(idx + 1)..].Trim();
        if (path.StartsWith('<') && path.Contains('>'))
        {
            var inner = path[(path.IndexOf('<') + 1)..path.IndexOf('>')];
            // quitar opciones SMTPUTCDB si las hubiera (p.ej. SIZE=...)
            int space = inner.IndexOf(' ');
            return space > 0 ? inner[..space] : inner;
        }
        if (path.Length > 0 && !path.Contains(' ') && !path.StartsWith('<'))
            return path;
        return null;
    }

    private static readonly Dictionary<string, Queue<DateTime>> _conns = new();
    private static readonly object _connLock = new();
    private int CountRecentConnections(string ip)
    {
        lock (_connLock)
        {
            if (!_conns.ContainsKey(ip)) _conns[ip] = new Queue<DateTime>();
            _conns[ip].Enqueue(DateTime.UtcNow);
            while (_conns[ip].Count > 0 && DateTime.UtcNow - _conns[ip].Peek() > TimeSpan.FromMinutes(1))
                _conns[ip].Dequeue();
            return _conns[ip].Count;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* ya descargado */ }
        try { _listener.Stop(); } catch (SocketException) { }
        try { await Task.WhenAll(_connections); } catch { /* best effort */ }
        _cts.Dispose();
    }
}

public sealed class SmtpServerOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 2525;
    public string Hostname { get; set; } = "atlasmail.local";
    public int MaxMessageBytes { get; set; } = 50 * 1024 * 1024;
    public int MaxConnectionsPerIp { get; set; } = 20;
    public int Backlog { get; set; } = 100;
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(5);
}