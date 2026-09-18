using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Services;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using AtlasMail.Domain.Rules;
using AtlasMail.Domain.ValueObjects;
using AtlasMail.Protocols.Smtp;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Web.Smtp;

/// <summary>
/// Implementa ISmtpMessageHandler: valida el envelope con la política de relay
/// (sección 5) y procesa el mensaje mediante la ingesta local.
/// NO OPEN RELAY: una conexión anónima sólo puede entregar a buzones locales válidos.
/// Autenticación AUTH PLAIN/LOGIN contra buzones locales (FASE 2): un cliente
/// autenticado puede enviar a dominios externos conforme a política.
/// </summary>
public class SmtpInboundHandler : ISmtpMessageHandler
{
    private readonly IAddressResolutionService _resolver;
    private readonly IInboundDeliveryService _inbound;
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IGroupService? _groups;
    private readonly ILogger<SmtpInboundHandler> _logger;
    // Rate-limit de brute-force SMTP AUTH por IP (spec §49). Estado thread-safe POR IP sin lock
    // global; expira y elimina entradas antiguas; ventana/límite configurables (singleton DI).
    private readonly AuthRateLimiter _authLimiter;

    public SmtpInboundHandler(IAddressResolutionService resolver, IInboundDeliveryService inbound,
        IApplicationDbContext db, IPasswordHasher hasher, IGroupService? groups,
        ILogger<SmtpInboundHandler> logger, AuthRateLimiter authLimiter)
    {
        _resolver = resolver; _inbound = inbound; _db = db; _hasher = hasher; _groups = groups; _logger = logger;
        _authLimiter = authLimiter ?? new AuthRateLimiter();
    }

    public async Task<string> AuthenticateAsync(string username, string password, SmtpSessionContext ctx, CancellationToken ct = default)
    {
        // §49 brute-force: bloquear IP con demasiados intentos de AUTH fallidos recientes.
        var ip = ctx.ClientIp;
        bool authAllowed = _authLimiter.IsAllowed(ip);
        if (!authAllowed)
        {
            _logger.LogWarning("SMTP AUTH bloqueado por rate-limit desde {Ip}", ip);
            await Task.Delay(500, ct);
            return "535 5.7.8 Too many authentication failures, try again later";
        }

        // username local form: user@dominio o user; password es la del buzón (o usuario web).
        string localPart;
        string? domainPart;
        if (EmailAddress.TryParse(username, out var parsed))
        {
            localPart = parsed.LocalPart;
            domainPart = parsed.Domain;
        }
        else
        {
            localPart = (username ?? string.Empty).Trim();
            domainPart = null;
        }

        // Buscar buzón por localpart (+ dominio si se dio), con password hash
        Mailbox mb;
        if (!string.IsNullOrWhiteSpace(domainPart))
        {
            mb = await _db.Mailboxes.Include(m => m.Domain)
                .FirstOrDefaultAsync(m => m.LocalPart == localPart && m.Domain!.Name == domainPart && m.Status == MailboxStatus.Active, ct);
        }
        else
        {
            // Sin dominio: intentar en cualquier dominio local (debe haber uno y solo uno con ese localpart)
            mb = await _db.Mailboxes.AsNoTracking()
                .FirstOrDefaultAsync(m => m.LocalPart == localPart && m.Status == MailboxStatus.Active, ct);
        }

        if (mb == null || string.IsNullOrEmpty(mb.PasswordHash))
        {
            _authLimiter.RecordFailure(ip);
            _logger.LogInformation("SMTP AUTH falló {User}: buzón no encontrado o sin hash", username);
            return "535 5.7.8 Authentication credentials invalid";
        }

        if (_hasher.Verify(password, mb.PasswordHash))
        {
            ctx.Authenticated = true;
            ctx.AuthUsername = mb.EmailAddress;
            _logger.LogInformation("SMTP AUTH OK {User}", mb.EmailAddress);
            return "235 2.7.0 Authentication successful";
        }

        _authLimiter.RecordFailure(ip);
        _logger.LogInformation("SMTP AUTH falló {User}: password incorrecto", username);
        return "535 5.7.8 Authentication credentials invalid";
    }

    public async Task<string> ValidateEnvelopeAsync(string mailFrom, string rcptTo, SmtpSessionContext ctx, CancellationToken ct = default)
    {
        if (!EmailAddress.TryParse(rcptTo, out var rcptAddr)) return "501 5.1.3 Bad recipient address syntax";

        var resolved = await _resolver.ResolveAsync(rcptTo, ct);
        bool domainLocal = resolved.Found || await _resolver.IsDomainLocalAsync(rcptAddr.Domain, ct);

        // FASE 6: una lista de distribución local es destinatario local válido
        bool isLocalList = !resolved.Found && _groups != null && await _groups.IsDistributionAsync(rcptTo, ct);
        bool rcptExistsLocally = resolved.Found || isLocalList;

        // Configuración de la política
        bool allowAuthSend = await SendConfig(ct);

        var mailFromAddr = NormalizeMailFrom(mailFrom);

        var decision = RelayPolicy.Evaluate(
            mailFrom: mailFromAddr ?? default,
            rcptTo: rcptAddr,
            authenticated: ctx.Authenticated,
            domainIsLocal: domainLocal,
            rcptExistsLocally: rcptExistsLocally,
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