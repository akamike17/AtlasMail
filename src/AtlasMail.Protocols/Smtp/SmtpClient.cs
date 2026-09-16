using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Protocols.Smtp;

/// <summary>Resultado de una entrega SMTP a un servidor remoto.</summary>
public sealed record SmtpDeliveryResult(bool Success, string Response, bool Temporary)
{
    /// <summary>True si el fallo es permanente (5xx) → bounce/DSN.</summary>
    public bool Permanent => !Success && !Temporary;
}

/// <summary>
/// Cliente SMTP (RFC 5321) para enviar correo a un host remoto mediante MX.
/// Soporta EHLO, STARTTLS (oportunista), AUTH PLAIN opcional, MAIL FROM, RCPT TO,
/// DATA con dot-stuffing, QUIT. Timeouts estrictos (sección 8). No abre relay.
/// Modalidad de seguridad configurable (sección 5):
///   - StartTls.Opportunistic (por defecto): usa STARTTLS si el servidor lo anuncia,
///     pero no lo exige (ENTREGAR en claro si el destino no soporta TLS).
///   - StartTls.Required: aborta si el servidor no ofrece STARTTLS (para MX que lo exigen).
///   - StartTls.Disabled: nunca negocia TLS (test local en claro).
/// </summary>
public sealed class SmtpClient
{
    private readonly ILogger<SmtpClient> _logger;
    private readonly SmtpClientOptions _options;

    public SmtpClient(ILogger<SmtpClient>? logger = null, SmtpClientOptions? options = null)
    {
        _logger = logger ?? AtlasMail.Protocols.NullLogger<SmtpClient>.Instance;
        _options = options ?? new SmtpClientOptions();
    }

    public async Task<SmtpDeliveryResult> SendAsync(string host, int port, string mailFrom, string rcptTo,
        byte[] rawMime, string heloName, CancellationToken ct = default)
    {
        return await SendAsync(host, port, mailFrom, new[] { rcptTo }, rawMime, heloName, _options, ct);
    }

    /// <summary>Une la clasificación 4xx/5xx en un único código fácil de consumir.</summary>
    public string Classify(SmtpDeliveryResult r) => r.Permanent ? "5xx-permanent" : r.Temporary ? "4xx-temporary" : "2xx-ok";

    public async Task<SmtpDeliveryResult> SendAsync(string host, int port, string mailFrom, IReadOnlyList<string> rcptList,
        byte[] rawMime, string heloName, SmtpClientOptions? options, CancellationToken ct = default)
    {
        options ??= _options;
        var result = await SendInternalAsync(host, port, mailFrom, rcptList, rawMime, heloName, options, ct);
        _logger.LogInformation("SMTP entrega a {Host}:{Port} -> {N} rcpt: {Succ} {Resp}",
            host, port, rcptList.Count, result.Success, result.Response);
        return result;
    }

    private async Task<SmtpDeliveryResult> SendInternalAsync(string host, int port, string mailFrom,
        IReadOnlyList<string> rcptList, byte[] rawMime, string heloName, SmtpClientOptions options, CancellationToken ct)
    {
        if (rcptList.Count == 0) return new SmtpDeliveryResult(true, "250 2.0.0 OK (no recipients)", false);

        using var client = new TcpClient();
        client.NoDelay = true;
        try { await client.ConnectAsync(host, port, ct); }
        catch (Exception ex) { return new SmtpDeliveryResult(false, "Connect failed: " + ex.Message, true); }
        client.ReceiveTimeout = (int)options.ConnectTimeout.TotalMilliseconds;
        client.SendTimeout = (int)options.ConnectTimeout.TotalMilliseconds;

        using var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        // Tras STARTTLS re-creamos reader/writer sobre el stream cifrado.
        Stream active = stream;

        try
        {
            string banner = await ReadReplyAsync(reader, ct);
            if (!banner.StartsWith("220")) return new SmtpDeliveryResult(false, "Banner: " + banner, IsTemporary(banner));

            // EHLO para averiguar capacidades
            await writer.WriteLineAsync("EHLO " + heloName);
            var ehlo = await ReadEhloAsync(reader, ct);
            if (ehlo.Response == null && !ehlo.Is250)
                return new SmtpDeliveryResult(false, "EHLO: " + ehlo.ResponseText, false);

            var capabilities = ehlo.Capabilities;

            // STARTTLS oportunista
            if ((options.StartTls == StartTlsMode.Required || options.StartTls == StartTlsMode.Opportunistic)
                && capabilities.ContainsKey("STARTTLS"))
            {
                await writer.WriteLineAsync("STARTTLS");
                var ok = await ReadReplyAsync(reader, ct);
                if (ok.StartsWith("220"))
                {
                    var upgraded = await TryUpgradeToTlsAsync(client, options, ct);
                    if (upgraded == null)
                    {
                        if (options.StartTls == StartTlsMode.Required)
                            return new SmtpDeliveryResult(false, "STARTTLS upgrade failed", true);
                    }
                    else
                    {
                        active = upgraded;
                        reader = new StreamReader(active, Encoding.ASCII);
                        writer = new StreamWriter(active, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                        await writer.WriteLineAsync("EHLO " + heloName);
                        ehlo = await ReadEhloAsync(reader, ct);
                        capabilities = ehlo.Capabilities;
                    }
                }
                else if (options.StartTls == StartTlsMode.Required)
                {
                    return new SmtpDeliveryResult(false, "STARTTLS refused: " + ok, IsTemporary(ok));
                }
            }
            else if (options.StartTls == StartTlsMode.Required)
            {
                return new SmtpDeliveryResult(false, "STARTTLS required but not advertised", true);
            }

            // AUTH PLAIN si hay credenciales y el servidor lo permite
            if (!string.IsNullOrEmpty(options.Username) && capabilities.ContainsKey("AUTH"))
            {
                var mechanisms = capabilities["AUTH"];
                if (mechanisms.Contains("PLAIN", StringComparer.OrdinalIgnoreCase))
                {
                    await writer.WriteLineAsync("AUTH PLAIN " + BuildPlainAuth(options.Username, options.Password));
                    var authr = await ReadReplyAsync(reader, ct);
                    if (!authr.StartsWith("235"))
                        return new SmtpDeliveryResult(false, "AUTH: " + authr, IsTemporary(authr));
                }
                else if (mechanisms.Contains("LOGIN", StringComparer.OrdinalIgnoreCase))
                {
                    return new SmtpDeliveryResult(false, "AUTH LOGIN no soportado por cliente; usar PLAIN", true);
                }
            }

            // MAIL FROM con SIZE si se anunció
            string sizeParam = capabilities.ContainsKey("SIZE") ? " SIZE=" + rawMime.Length : "";
            await writer.WriteLineAsync($"MAIL FROM:<{mailFrom}>{sizeParam}");
            var mfrom = await ReadReplyAsync(reader, ct);
            if (!mfrom.StartsWith("250")) return new SmtpDeliveryResult(false, "MAIL FROM: " + mfrom, IsTemporary(mfrom));

            foreach (var rcpt in rcptList)
            {
                if (ct.IsCancellationRequested) return new SmtpDeliveryResult(false, "Cancelled", true);
                await writer.WriteLineAsync("RCPT TO:<" + rcpt + ">");
                var r = await ReadReplyAsync(reader, ct);
                if (!r.StartsWith("250") && !r.StartsWith("251"))
                    return new SmtpDeliveryResult(false, "RCPT TO " + rcpt + ": " + r, IsTemporary(r));
            }

            await writer.WriteLineAsync("DATA");
            var data = await ReadReplyAsync(reader, ct);
            if (!data.StartsWith("354")) return new SmtpDeliveryResult(false, "DATA: " + data, IsTemporary(data));

            await WriteMessageAsync(writer, rawMime);
            await writer.WriteLineAsync(".");
            var done = await ReadReplyAsync(reader, ct);
            if (!done.StartsWith("250")) return new SmtpDeliveryResult(false, "DATA end: " + done, IsTemporary(done));

            await writer.WriteLineAsync("QUIT");
            try { await ReadReplyAsync(reader, ct); } catch { /* best effort */ }
            return new SmtpDeliveryResult(true, done, false);
        }
        catch (OperationCanceledException) { return new SmtpDeliveryResult(false, "Timeout", true); }
        catch (Exception ex) { return new SmtpDeliveryResult(false, "Protocol error: " + ex.Message, true); }
    }

    private static string BuildPlainAuth(string username, string password)
    {
        // base64("\0user\0pass")
        var bytes = Encoding.UTF8.GetBytes("\0" + username + "\0" + password);
        return Convert.ToBase64String(bytes);
    }

    private async Task<SslStream?> TryUpgradeToTlsAsync(TcpClient client, SmtpClientOptions options, CancellationToken ct)
    {
        try
        {
            var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, options.ServerCertificateValidationCallback);
            await ssl.AuthenticateAsClientAsync(options.TargetName ?? string.Empty, null, options.SslProtocols, checkCertificateRevocation: false);
            _logger.LogDebug("STARTTLS negociado hacia {Host}", options.TargetName);
            return ssl;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo STARTTLS hacia {Host}", options.TargetName);
            return null;
        }
    }

    private static async Task WriteMessageAsync(StreamWriter writer, byte[] mime)
    {
        var text = Encoding.ASCII.GetString(mime);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            string l = line.EndsWith('\r') && !line.EndsWith("\r\n") ? line[..^1] : line;
            if (l.StartsWith('.')) l = "." + l;
            await writer.WriteLineAsync(l);
        }
    }

    private static async Task<string> ReadReplyAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        string? last;
        do
        {
            last = await reader.ReadLineAsync(ct);
            if (last == null) break;
            sb.AppendLine(last);
        } while (last is { Length: >= 4 } && last[3] == '-');
        return sb.ToString().Trim();
    }

    private static async Task<(bool Is250, string? Response, string ResponseText, Dictionary<string, List<string>> Capabilities)> ReadEhloAsync(StreamReader reader, CancellationToken ct)
    {
        var caps = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        string? last;
        bool is250 = false;
        int first = 0;
        do
        {
            last = await reader.ReadLineAsync(ct);
            if (last == null) break;
            if (last.Length >= 3 && int.TryParse(last.AsSpan(0, 3), out var code) && code == 250) is250 = true;
            sb.AppendLine(last);
            var body = last.Length >= 4 ? last[4..] : last;
            int sp = body.IndexOf(' ');
            string key = sp > 0 ? body[..sp].ToUpperInvariant() : body.ToUpperInvariant();
            string arg = sp > 0 ? body[(sp + 1)..].Trim() : string.Empty;
            if (!string.IsNullOrWhiteSpace(key))
            {
                if (!caps.ContainsKey(key)) caps[key] = new List<string>();
                if (!string.IsNullOrWhiteSpace(arg))
                    caps[key].AddRange(arg.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            first++;
        } while (last is { Length: >= 4 } && last[3] == '-');
        return (is250, is250 ? null : sb.ToString().Trim(), sb.ToString().Trim(), caps);
    }

    private static bool IsTemporary(string reply) =>
        reply.StartsWith("4") || reply.StartsWith("450") || reply.StartsWith("451") || reply.StartsWith("452") ||
        reply.StartsWith("421") || reply.StartsWith("422") || reply.StartsWith("250 ");
}

public enum StartTlsMode
{
    /// <summary>Acepta el envío con STARTTLS si está disponible; si no, en claro.</summary>
    Opportunistic = 0,
    /// <summary>Exige STARTTLS; si el servidor no lo ofrece/falla, la entrega falla.</summary>
    Required = 1,
    /// <summary>Nunca negocia TLS (solo para pruebas locales en claro).</summary>
    Disabled = 2,
}

public sealed class SmtpClientOptions
{
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(60);
    public StartTlsMode StartTls { get; set; } = StartTlsMode.Opportunistic;
    public string? Username { get; set; }
    public string? Password { get; set; }
    /// <summary>Nombre de host para SNI/validación de certificado; por defecto el host destino.</summary>
    public string? TargetName { get; set; }
    public System.Security.Authentication.SslProtocols SslProtocols { get; set; } = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
    public RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; set; }
}