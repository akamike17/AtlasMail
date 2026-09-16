using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Protocols.Smtp;

/// <summary>Resultado de una entrega SMTP a un servidor remoto.</summary>
public sealed record SmtpDeliveryResult(bool Success, string Response, bool Temporary);

/// <summary>
/// Cliente SMTP (RFC 5321) para enviar correo a un host remoto mediante MX.
/// Soporta EHLO, MAIL FROM, RCPT TO, DATA con dot-stuffing, QUIT.
/// Timeouts estrictos (sección 8). No abre relay.
/// </summary>
public sealed class SmtpClient
{
    private readonly ILogger<SmtpClient> _logger;
    public SmtpClient(ILogger<SmtpClient>? logger = null) => _logger = logger ?? AtlasMail.Protocols.NullLogger<SmtpClient>.Instance;

    public async Task<SmtpDeliveryResult> SendAsync(string host, int port, string mailFrom, string rcptTo,
        byte[] rawMime, string heloName, CancellationToken ct = default)
    {
        var result = await SendInternalAsync(host, port, mailFrom, rcptTo, rawMime, heloName, ct);
        _logger.LogInformation("SMTP entrega a {Host}:{Port} -> {Rcpt}: {Succ} {Resp}", host, port, rcptTo, result.Success, result.Response);
        return result;
    }

    private async Task<SmtpDeliveryResult> SendInternalAsync(string host, int port, string mailFrom, string rcptTo,
        byte[] rawMime, string heloName, CancellationToken ct)
    {
        using var client = new TcpClient();
        client.NoDelay = true;
        try { await client.ConnectAsync(host, port, ct); }
        catch (Exception ex) { return new SmtpDeliveryResult(false, "Connect failed: " + ex.Message, true); }
        client.ReceiveTimeout = 60000;
        client.SendTimeout = 60000;
        using var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        try
        {
            string banner = await ReadReplyAsync(reader, ct); // 220
            if (!banner.StartsWith("220")) return new SmtpDeliveryResult(false, "Banner: " + banner, true);

            await writer.WriteLineAsync("EHLO " + heloName);
            var ehlo = await ReadReplyAsync(reader, ct);
            if (!ehlo.StartsWith("250")) return new SmtpDeliveryResult(false, "EHLO: " + ehlo, false);

            await writer.WriteLineAsync("MAIL FROM:<" + mailFrom + ">");
            var mfrom = await ReadReplyAsync(reader, ct);
            if (!mfrom.StartsWith("250")) return new SmtpDeliveryResult(false, "MAIL FROM: " + mfrom, IsTemporary(mfrom));

            await writer.WriteLineAsync("RCPT TO:<" + rcptTo + ">");
            var rcpt = await ReadReplyAsync(reader, ct);
            if (!rcpt.StartsWith("250") && !rcpt.StartsWith("251"))
                return new SmtpDeliveryResult(false, "RCPT TO: " + rcpt, IsTemporary(rcpt));

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

    private static async Task WriteMessageAsync(StreamWriter writer, byte[] mime)
    {
        // Escribir línea por línea aplicando dot-stuffing.
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

    private static bool IsTemporary(string reply) =>
        reply.StartsWith("4") || reply.StartsWith("450") || reply.StartsWith("451") || reply.StartsWith("452") ||
        reply.StartsWith("421") || reply.StartsWith("422");
}