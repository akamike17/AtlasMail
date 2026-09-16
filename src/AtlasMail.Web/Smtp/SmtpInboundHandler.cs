using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Domain.Rules;
using AtlasMail.Domain.ValueObjects;
using AtlasMail.Protocols.Smtp;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Web.Smtp;

/// <summary>
/// Implementa ISmtpMessageHandler: valida el envelope con la política de relay
/// (sección 5) y procesa el mensaje mediante la ingesta local.
/// NO OPEN RELAY: una conexión anónima sólo puede entregar a buzones locales válidos.
/// </summary>
public class SmtpInboundHandler : ISmtpMessageHandler
{
    private readonly IAddressResolutionService _resolver;
    private readonly IInboundDeliveryService _inbound;
    private readonly IApplicationDbContext _db;
    private readonly ILogger<SmtpInboundHandler> _logger;

    public SmtpInboundHandler(IAddressResolutionService resolver, IInboundDeliveryService inbound,
        IApplicationDbContext db, ILogger<SmtpInboundHandler> logger)
    {
        _resolver = resolver; _inbound = inbound; _db = db; _logger = logger;
    }

    public async Task<string> ValidateEnvelopeAsync(string mailFrom, string rcptTo, SmtpSessionContext ctx, CancellationToken ct = default)
    {
        if (!EmailAddress.TryParse(rcptTo, out var rcptAddr)) return "501 5.1.3 Bad recipient address syntax";

        var resolved = await _resolver.ResolveAsync(rcptTo, ct);
        bool domainLocal = resolved.Found || await _resolver.IsDomainLocalAsync(rcptAddr.Domain, ct);

        // Configuración de la política
        bool allowAuthSend = await SendConfig(ct);

        var mailFromAddr = NormalizeMailFrom(mailFrom);

        var decision = RelayPolicy.Evaluate(
            mailFrom: mailFromAddr ?? default,
            rcptTo: rcptAddr,
            authenticated: false, // AUTH aún no en este ciclo; webmail usa submission autenticado por sesión
            domainIsLocal: domainLocal,
            rcptExistsLocally: resolved.Found,
            allowAuthSend: allowAuthSend);

        return decision.Decision switch
        {
            RelayDecision.DeliverLocal or RelayDecision.DeliverLocalRewritten or RelayDecision.DeliverRemote => "250 2.1.0 OK",
            RelayDecision.Deny => "550 5.7.1 Relay access denied",
            _ => "550 5.7.1 Relay access denied"
        };
    }

    private static EmailAddress? NormalizeMailFrom(string? mailFrom)
    {
        if (string.IsNullOrWhiteSpace(mailFrom) || mailFrom.Trim() == "<>" || mailFrom.Trim() == "MAILFROM:<>")
            return null; // null sender (bounce postmaster) permitido
        string candidate = mailFrom.Trim().Replace("<", "").Replace(">", "");
        return EmailAddress.TryParse(candidate, out var addr) ? addr : null;
    }

    private async Task<bool> SendConfig(CancellationToken ct)
    {
        var v = await _db.ConfigurationEntries.FirstOrDefaultAsync(c => c.Key == "security.allowAuthenticatedExternalSend", ct);
        return v == null || v.Value == "true";
    }

    public async Task<string> HandleMessageAsync(string mailFrom, string rcptTo, byte[] rawMime, SmtpSessionContext ctx, CancellationToken ct = default)
    {
        var result = await _inbound.IngestAsync(mailFrom, rcptTo, rawMime, ctx.Helo, ctx.ClientIp, ctx.Authenticated,
            ctx.AuthUsername ?? "smtp:" + ctx.ClientIp, ct);
        return result switch
        {
            InboundResult.Accepted or InboundResult.MovedToSpam => "250 2.0.0 OK queued",
            InboundResult.Quarantined => "250 2.0.0 OK queued (quarantined)",
            InboundResult.RelayDenied => "550 5.7.1 Relay access denied",
            InboundResult.Rejected => "550 5.7.1 Message rejected",
            _ => "451 4.3.0 Temporary failure"
        };
    }
}