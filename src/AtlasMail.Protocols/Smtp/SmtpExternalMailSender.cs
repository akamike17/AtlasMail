using AtlasMail.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Protocols.Smtp;

/// <summary>
/// Adaptador que expone el SmtpClient como IExternalMailSender para la capa de
/// entrega externa (FASE 2). Configura STARTTLS según la política global.
/// </summary>
public sealed class SmtpExternalMailSender : IExternalMailSender
{
    private readonly SmtpClient _client;

    public SmtpExternalMailSender(ILogger<SmtpClient>? logger = null, TimeSpan? connectTimeout = null)
    {
        _client = new SmtpClient(logger, new SmtpClientOptions { ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(60) });
    }

    public async Task<SmtpSendResult> SendAsync(string host, int port, string mailFrom, IReadOnlyList<string> rcptList,
        byte[] rawMime, string heloName, SmtpSendOptions options, CancellationToken ct = default)
    {
        // STARTTLS oportunista por defecto: si el certificado no valida se degrada a claro
        // (comportamiento estándar de muchos MTA sin DNSSEC/DANE). Si la política exige TLS,
        // se valida el certificado de forma estricta.
        var result = await _client.SendAsync(host, port, mailFrom, rcptList, rawMime, heloName,
            new SmtpClientOptions
            {
                ConnectTimeout = TimeSpan.FromSeconds(60),
                StartTls = options.StartTlsRequired ? StartTlsMode.Required : StartTlsMode.Opportunistic,
                Username = options.Username,
                Password = options.Password,
                TargetName = host
            }, ct);

        return new SmtpSendResult(result.Success, result.Response, result.Temporary);
    }
}