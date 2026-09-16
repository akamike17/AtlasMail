using AtlasMail.Application.Dtos;
using AtlasMail.Domain.Enums;
using AtlasMail.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Application.Services;

/// <summary>
/// Resuelve una dirección local a un buzón. Aplica, en orden:
///   1. buzón directo
///   2. alias -> buzón destino
///   3. plus addressing (usuario+etiqueta -> usuario) si está habilitado en el dominio
///   4. catch-all si está habilitado y ninguna otra cosa coincide
/// Nunca confunde un destinatario inexistente con un relay (sección 42).
/// </summary>
public class AddressResolutionService : IAddressResolutionService
{
    private readonly IApplicationDbContext _db;

    public AddressResolutionService(IApplicationDbContext db) => _db = db;

    public async Task<bool> IsDomainLocalAsync(string domainName, CancellationToken ct = default)
    {
        var n = domainName.Trim().ToLowerInvariant();
        return await _db.Domains.AsNoTracking().AnyAsync(d => d.Name == n && d.Enabled, ct);
    }

    public async Task<RegistryAddress> ResolveAsync(string address, CancellationToken ct = default)
    {
        if (!EmailAddress.TryParse(address, out var parsed))
            return new RegistryAddress("", address, false, null, false, false, null);

        var domain = await _db.Domains.AsNoTracking().FirstOrDefaultAsync(d => d.Name == parsed.Domain && d.Enabled, ct);
        if (domain == null)
            return new RegistryAddress(parsed.LocalPart, parsed.Domain, false, null, false, false, null);

        // 1. buzón directo
        var mailbox = await _db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.DomainId == domain.Id && m.LocalPart == parsed.LocalPart && m.Status == MailboxStatus.Active, ct);
        if (mailbox != null)
            return new RegistryAddress(parsed.LocalPart, parsed.Domain, true, mailbox.Id, false, false, parsed.LocalPart);

        // 2. alias
        var alias = await _db.Aliases.AsNoTracking()
            .Include(a => a.TargetMailbox)
            .FirstOrDefaultAsync(a => a.DomainId == domain.Id && a.LocalPart == parsed.LocalPart && a.Enabled, ct);
        if (alias?.TargetMailbox != null)
            return new RegistryAddress(parsed.LocalPart, parsed.Domain, true, alias.TargetMailbox.Id, true, false, alias.TargetMailbox.LocalPart);

        // 3. plus addressing
        if (parsed.HasPlusTag && domain.PlusAddressingEnabled)
        {
            var basePart = parsed.WithoutPlusTag().LocalPart;
            var baseMailbox = await _db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.DomainId == domain.Id && m.LocalPart == basePart && m.Status == MailboxStatus.Active, ct);
            if (baseMailbox != null)
                return new RegistryAddress(parsed.LocalPart, parsed.Domain, true, baseMailbox.Id, false, false, basePart);
        }

        // 4. catch-all
        if (domain.CatchAllEnabled && !string.IsNullOrWhiteSpace(domain.CatchAllTargetLocalPart))
        {
            var catchMailbox = await _db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(m => m.DomainId == domain.Id && m.LocalPart == domain.CatchAllTargetLocalPart && m.Status == MailboxStatus.Active, ct);
            if (catchMailbox != null)
                return new RegistryAddress(parsed.LocalPart, parsed.Domain, true, catchMailbox.Id, true, true, catchMailbox.LocalPart);
        }

        return new RegistryAddress(parsed.LocalPart, parsed.Domain, false, null, false, false, null);
    }
}