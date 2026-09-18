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

    /// <summary>
    /// Autentica un usuario SMTP (AUTH PLAIN/LOGIN). Devuelve "235 2.7.0 OK" en éxito,
    /// "535 5.7.8 Authentication credentials invalid" en fallo.
    /// El servidor establece ctx.Authenticated/AuthUsername tras éxito.
    /// </summary>
    Task<string> AuthenticateAsync(string username, string password, SmtpSessionContext ctx, CancellationToken ct = default);
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
        // CTS del temporizador absoluto de la fase DATA; declarado aquí para que sea visible en el finally.
        CancellationTokenSource? dataCts = null;
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = (int)_options.CommandTimeout.TotalMilliseconds;
            NetworkStream networkStream = client.GetStream();
            Stream stream = networkStream;
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            var ctx = new SmtpSessionContext { ClientIp = clientIp, ServerHostname = _options.Hostname };
            bool tlsActive = false;
            // Handler scoped por conexión (para que DbContext scoped sea seguro)
            scope = _scopeFactory?.CreateScope();
            var handler = scope != null ? scope.ServiceProvider.GetRequiredService<ISmtpMessageHandler>() : _handlerDelegate!();

            await writer.WriteAsync($"220 {_options.Hostname} AtlasMail SMTP\r\n");

            string? mailFrom = null;
            string? rcptTo = null;
            var dataBuffer = new MemoryStream();
            bool inData = false;
            bool dataOverflow = false;
            DateTime dataStarted = default;
            // CTS vinculado a la sesión que vence tras DataTimeout durante la fase DATA. Permite que
            // ReadLineAsync se cancele por tiempo aunque el cliente siga "inactivo" sin enviar líneas
            // (cubre el stream DATA infinito). Se crea en cada DATA y se libera al salir.
            // (declaración hoisted a nivel de método: ver `dataCts` arriba, junto a `scope`)
            int cmdCount = 0;
            var welcomeReceived = false;

            void EndDataPhase()
            {
                dataCts?.Cancel();
                dataCts?.Dispose();
                dataCts = null;
            }

            while (!token.IsCancellationRequested)
            {
                string? line;
                try
                {
                    // Durante DATA, leer con el token del temporizador absoluto: si el cliente deja de
                    // enviar líneas (o sigue enviando sin "."), el read se desbloquea al vencer
                    // DataTimeout y cerramos la sesión — sin crecer en memoria (ya acotada por MaxMessageBytes).
                    var readToken = inData && _options.DataTimeoutEnabled && dataCts != null ? dataCts.Token : token;
                    line = await reader.ReadLineAsync(readToken);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception) { break; }
                if (line == null) break; // EOF

                if (line.Length > 1000) { await writer.WriteAsync("500 Line too long\r\n"); continue; }

                if (inData)
                {
                    // Límite ABSOLUTO/temporizador de la fase DATA: un cliente puede seguir mandando
                    // líneas sin el "." final (stream DATA infinito). Tras DataTimeout cerramos la
                    // sesión SMTP de forma segura (421) — evita conexiones eternas consumiendo
                    // recursos; la memoria ya está acotada por MaxMessageBytes en dataOverflow.
                    if (_options.DataTimeoutEnabled && DateTime.UtcNow - dataStarted > _options.DataTimeout)
                    {
                        await writer.WriteAsync("421 4.4.2 Timeout receiving DATA, connection closing\r\n");
                        break;
                    }
                    // Fin de DATA = línea con ".".
                    if (line == "." || line == "\u0015.")
                    {
                        inData = false;
                        if (dataOverflow)
                        {
                            dataOverflow = false;
                            dataBuffer.SetLength(0);
                            mailFrom = null; rcptTo = null;
                            EndDataPhase();
                            // NO entregar mensajes truncados: 552 y descartar (NO pérdida silenciosa, §49/§53).
                            await writer.WriteAsync("552 5.3.4 Message size exceeds fixed maximum\r\n");
                            continue;
                        }
                        EndDataPhase();
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
                    if (dataBuffer.Length + bytes.Length > _options.MaxMessageBytes)
                        dataOverflow = true; // marcar y seguir consumiendo hasta "." (no entregar truncado)
                    else
                        dataBuffer.Write(bytes, 0, bytes.Length);
                    continue;
                }

                // Flooding: límite de comandos por conexión (NOOP/EHLO/RSET no productivos).
                if (++cmdCount > _options.MaxCommandsPerConnection)
                {
                    await writer.WriteAsync($"421 4.7.0 Too many commands, connection closing\r\n");
                    break;
                }

                var cmd = line.TrimEnd('\r');
                string upper = cmd.ToUpperInvariant();

                if (welcomeReceived && ctx.Authenticated == false)
                {
                    // mantener orden: no restringir comandos; verificar límites abajo
                }

                // §seguridad: si TLS es obligatorio, rechazar AUTH en claro (antes de cualquier
                // intercambio de credenciales). El cliente debe negociar STARTTLS primero.
                if (_options.RequireTlsForAuth && !tlsActive && upper.StartsWith("AUTH"))
                {
                    await writer.WriteAsync("530 5.7.0 Must issue a STARTTLS command first\r\n");
                    continue;
                }

                if (upper == "QUIT") { await writer.WriteAsync("221 Bye\r\n"); break; }
                else if (upper == "STARTTLS")
                {
                    if (_options.TlsCertificate == null)
                    {
                        await writer.WriteAsync("454 4.7.0 TLS not available\r\n");
                        continue;
                    }
                    if (tlsActive)
                    {
                        await writer.WriteAsync("503 5.5.1 TLS already active\r\n");
                        continue;
                    }
                    await writer.WriteAsync("220 2.0.0 Ready to start TLS\r\n");
                    try
                    {
                        await writer.FlushAsync(token);
                        var ssl = new System.Net.Security.SslStream(networkStream, leaveInnerStreamOpen: false, DoNotValidateClientCert);
                        await ssl.AuthenticateAsServerAsync(_options.TlsCertificate, clientCertificateRequired: false,
                            enabledSslProtocols: System.Security.Authentication.SslProtocols.Tls12 |
                                                 System.Security.Authentication.SslProtocols.Tls13, checkCertificateRevocation: false);
                        stream = ssl;
                        reader = new StreamReader(stream, Encoding.ASCII);
                        writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                        tlsActive = true;
                        // El cliente DEBE repetir EHLO tras STARTTLS (RFC 3207). Reset estado de sobre.
                        welcomeReceived = false;
                        mailFrom = null; rcptTo = null;
                        continue;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "STARTTLS falló con {Ip}", clientIp);
                        await writer.WriteAsync("454 4.7.0 TLS negotiation failed\r\n");
                        break;
                    }
                }
                else if (upper == "EHLO" || upper.StartsWith("EHLO ") || upper == "HELO" || upper.StartsWith("HELO "))
                {
                    // EHLO extensible (los clientes reales envían "EHLO hostname")
                    var ehlo = upper == "EHLO" || upper.StartsWith("EHLO ");
                    ctx.Helo = GetArg(cmd);
                    welcomeReceived = true;
                    if (ehlo)
                    {
                        bool tlsAvailable = _options.TlsCertificate != null;
                        bool advertiseAuth = tlsActive || !_options.RequireTlsForAuth;
                        await writer.WriteAsync($"250-{_options.Hostname} Hello\r\n");
                        await writer.WriteAsync("250-SIZE " + _options.MaxMessageBytes + "\r\n");
                        await writer.WriteAsync("250-8BITMIME\r\n");
                        if (tlsAvailable && !tlsActive)
                            await writer.WriteAsync("250-STARTTLS\r\n");
                        // No anunciar AUTH en claro cuando TLS es obligatorio y aún no se negoció.
                        if (advertiseAuth)
                            await writer.WriteAsync("250-AUTH PLAIN LOGIN\r\n");
                        await writer.WriteAsync("250-ENHANCEDSTATUSCODES\r\n");
                        await writer.WriteAsync("250 HELP\r\n");
                    }
                    else
                    {
                        await writer.WriteAsync($"250 {_options.Hostname} Hello\r\n");
                    }
                }
                else if (upper == "AUTH PLAIN" || upper.StartsWith("AUTH PLAIN "))
                {
                    // AUTH PLAIN: credenciales inline (base64 "\0user\0pass") o vía desafío
                    string b64 = upper == "AUTH PLAIN" ? string.Empty : cmd[(cmd.ToUpperInvariant().LastIndexOf("PLAIN") + 6)..].Trim();
                    string? resp = null;
                    if (string.IsNullOrWhiteSpace(b64))
                    {
                        await writer.WriteAsync("334 \r\n");
                        string? challenge = null;
                        try { challenge = await reader.ReadLineAsync(token); } catch { }
                        if (challenge == null) break;
                        resp = challenge.TrimEnd('\r');
                    }
                    else resp = b64;
                    var auth = DecodePlainAuth(resp);
                    if (auth == null) { await writer.WriteAsync("501 5.7.0 Invalid AUTH PLAIN\r\n"); continue; }
                    var ar = await SafeHandleAsync(() => handler.AuthenticateAsync(auth.Value.Username, auth.Value.Password, ctx, token));
                    if (ar.StartsWith("235"))
                    {
                        ctx.Authenticated = true;
                        ctx.AuthUsername = auth.Value.Username;
                        await writer.WriteAsync(ar + "\r\n");
                    }
                    else
                    {
                        ctx.Authenticated = false;
                        await writer.WriteAsync((ar.StartsWith("535") ? ar : "535 5.7.8 Authentication credentials invalid") + "\r\n");
                    }
                }
                else if (upper == "AUTH LOGIN" || upper.StartsWith("AUTH LOGIN "))
                {
                    // AUTH LOGIN (legacy): desafío USERNAME, luego PASSWORD base64
                    string b64User = upper == "AUTH LOGIN" ? string.Empty : cmd[(cmd.ToUpperInvariant().LastIndexOf("LOGIN") + 6)..].Trim();
                    string? username = null;
                    if (!string.IsNullOrWhiteSpace(b64User)) username = DecodeUtf8(b64User);
                    else
                    {
                        await writer.WriteAsync("334 VXNlcm5hbWU6\r\n");
                        string? u = null;
                        try { u = await reader.ReadLineAsync(token); } catch { }
                        if (u == null) break;
                        username = DecodeUtf8(u.TrimEnd('\r'));
                    }
                    await writer.WriteAsync("334 UGFzc3dvcmQ6\r\n");
                    string? p = null;
                    try { p = await reader.ReadLineAsync(token); } catch { }
                    if (p == null) break;
                    var password = DecodeUtf8(p.TrimEnd('\r'));
                    if (username == null || password == null) { await writer.WriteAsync("501 5.7.0 Invalid AUTH LOGIN\r\n"); continue; }
                    var ar = await SafeHandleAsync(() => handler.AuthenticateAsync(username, password, ctx, token));
                    if (ar.StartsWith("235"))
                    {
                        ctx.Authenticated = true;
                        ctx.AuthUsername = username;
                        await writer.WriteAsync(ar + "\r\n");
                    }
                    else
                    {
                        ctx.Authenticated = false;
                        await writer.WriteAsync((ar.StartsWith("535") ? ar : "535 5.7.8 Authentication credentials invalid") + "\r\n");
                    }
                }
                else if (upper.StartsWith("AUTH"))
                {
                    await writer.WriteAsync("504 5.5.4 Unrecognized authentication type\r\n");
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
                    dataStarted = DateTime.UtcNow; // arrancar el temporizador absoluto de la fase DATA
                    dataOverflow = false;
                    // Temporizador absoluto de la fase DATA: cancela el read si el cliente no termina.
                    dataCts?.Dispose();
                    dataCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    if (_options.DataTimeoutEnabled) dataCts.CancelAfter(_options.DataTimeout);
                    await writer.WriteAsync("354 End data with <CR><LF>.<CR><LF>\r\n");
                }
                else if (upper == "RSET")
                {
                    mailFrom = null; rcptTo = null; inData = false; dataBuffer = new MemoryStream();
                    EndDataPhase();
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
            dataCts?.Cancel();
            dataCts?.Dispose();
            // Cierre TCP limpio: enviar FIN (no RST) para que la última respuesta (p.ej. 421 tras flood)
            // llegue al cliente. Un Close() directo con datos sin leer puede disparar un RST que
            // descarta el 421 pendiente y hace intermitente el cierre por flood.
            try { if (client.Connected && client.Client.Connected) client.Client.Shutdown(SocketShutdown.Both); } catch { /* best effort */ }
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

    private static (string Username, string Password)? DecodePlainAuth(string base64)
    {
        try
        {
            var raw = Convert.FromBase64String(base64.Trim());
            var parts = System.Text.Encoding.UTF8.GetString(raw).Split('\0');
            if (parts.Length < 3) return null;
            string user = parts[^2], pass = parts[^1];
            return string.IsNullOrWhiteSpace(user) || pass == null ? null : (user, pass);
        }
        catch { return null; }
    }

    private static string? DecodeUtf8(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim())); }
        catch { return null; }
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

    // El servidor no requiere certificado de cliente; aceptamos el (ausente) del cliente de correo.
    private static bool DoNotValidateClientCert(object sender, System.Security.Cryptography.X509Certificates.X509Certificate? cert,
        System.Security.Cryptography.X509Certificates.X509Chain? chain, System.Net.Security.SslPolicyErrors errors) => true;
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
    public int MaxCommandsPerConnection { get; set; } = 1000;
    public int Backlog { get; set; } = 100;
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Límite absoluto de la fase DATA: un cliente que siga enviando sin el "." final
    /// (stream infinito) cierra la sesión con 421 al superar este tiempo. Requiere activar
    /// <see cref="DataTimeoutEnabled"/>. La memoria ya queda acotada por <see cref="MaxMessageBytes"/>.</summary>
    public TimeSpan DataTimeout { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Activa/desactiva el temporizador absoluto de DATA (por defecto activo).</summary>
    public bool DataTimeoutEnabled { get; set; } = true;
    /// <summary>Certificado TLS del servidor (STARTTLS). Si es null, no se anuncia STARTTLS.</summary>
    public System.Security.Cryptography.X509Certificates.X509Certificate2? TlsCertificate { get; set; }
    /// <summary>Si true y hay certificado, exige TLS antes de permitir AUTH (rechaza AUTH en claro).</summary>
    public bool RequireTlsForAuth { get; set; }
}