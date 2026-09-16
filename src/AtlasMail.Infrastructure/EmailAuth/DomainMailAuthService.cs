using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Domain.Entities;
using AtlasMail.Security.EmailAuth;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Infrastructure.EmailAuth;

/// <summary>
/// Gestión de autenticación de correo por dominio (spec §17-19): construye los registros
/// DNS que el administrador debe publicar (SPF, DKIM selector/clave pública, DMARC) y
/// genera claves DKIM bajo demanda. La clave privada se guarda en el modelo pero NUNCA
/// se expone por API (más que un aviso). Soporta múltiples dominios.
/// Remitente sugerido para registros: usa el hostname del servidor.
/// </summary>
public sealed class DomainMailAuthService : IDomainMailAuthService
{
    private readonly IApplicationDbContext _db;

    public DomainMailAuthService(IApplicationDbContext db) => _db = db;

    public async Task<DomainMailAuthStatus> GetStatusAsync(long domainId, CancellationToken ct = default)
    {
        var d = await _db.Domains.FirstOrDefaultAsync(x => x.Id == domainId, ct)
            ?? throw new InvalidOperationException("Dominio no encontrado");
        return BuildStatus(d);
    }

    public async Task<DomainMailAuthStatus> EnableDkimAsync(long domainId, CancellationToken ct = default)
    {
        var d = await _db.Domains.FirstOrDefaultAsync(x => x.Id == domainId, ct)
            ?? throw new InvalidOperationException("Dominio no encontrado");

        if (!d.DkimEnabled || string.IsNullOrEmpty(d.DkimPrivateKey))
        {
            var (priv, _) = Dkim.GenerateKeyPair();
            d.DkimEnabled = true;
            d.DkimSelector = string.IsNullOrWhiteSpace(d.DkimSelector) ? Dkim.Selector : d.DkimSelector;
            d.DkimPrivateKey = priv;
            d.UpdatedAtUtc = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return BuildStatus(d);
    }

    public async Task<DomainMailAuthStatus> SetDmarcPolicyAsync(long domainId, string policy, CancellationToken ct = default)
    {
        var d = await _db.Domains.FirstOrDefaultAsync(x => x.Id == domainId, ct)
            ?? throw new InvalidOperationException("Dominio no encontrado");
        if (policy is not ("none" or "quarantine" or "reject"))
            throw new ArgumentException("Política DMARC inválida (none|quarantine|reject)");

        d.DmarcPolicy = policy;
        d.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return BuildStatus(d);
    }

    private DomainMailAuthStatus BuildStatus(Domain.Entities.Domain d)
    {
        // SPF: recomendar "v=spf1 ip4:<IP-del-servidor> ~all" → no conocemos la IP pública aquí,
        // así que proveemos plantilla con placeholder; enable = el admin confirmó.
        string spfRecord = d.SpfRecord
            ?? $"v=spf1 mx ~all"; // plantilla razonable; el admin debe añadir su/sus IP(s)

        // DKIM: clave pública extraída para el registro TXT del selector
        string? dkimTxt = null;
        if (d.DkimEnabled && !string.IsNullOrEmpty(d.DkimPrivateKey))
        {
            try
            {
                var pub = Dkim.GetPublicKeyPem(d.DkimPrivateKey);
                if (pub != null) dkimTxt = Dkim.PublicKeyRecord(pub);
            }
            catch { /* clave corrupta → sin registro */ }
        }

        string? dmarcRecord = d.DmarcPolicy is null ? null
            : $"v=DMARC1; p={d.DmarcPolicy}; sp={d.DmarcPolicy}; adkim=r; aspf=r; fo=1";

        return new DomainMailAuthStatus(
            d.Id, d.Name,
            SpfEnabled: !string.IsNullOrWhiteSpace(d.SpfRecord),
            SpfRecordToPublish: spfRecord,
            DkimEnabled: d.DkimEnabled,
            DkimSelector: d.DkimSelector,
            DkimPublicKeyRecord: dkimTxt,
            DmarcPolicy: d.DmarcPolicy,
            DmarcRecordToPublish: dmarcRecord,
            DkimPrivateKeyHint: d.DkimEnabled ? "clave privada almacenada (no expuesta)" : null);
    }
}