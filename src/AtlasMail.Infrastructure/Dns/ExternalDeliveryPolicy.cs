using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Infrastructure.Dns;

/// <summary>
/// Política de entrega externa (FASE 2): lee de ConfigurationEntries si el envío
/// autenticado a dominios externos está habilitado (por defecto true, consistente
/// con la política de la capa de relay). El worker aplica esto al encaminar.
/// </summary>
public sealed class ExternalDeliveryPolicy : IExternalDeliveryPolicy
{
    private readonly IApplicationDbContext _db;

    public ExternalDeliveryPolicy(IApplicationDbContext db) => _db = db;

    public async Task<bool> AllowDomainExternalSendAsync(string domainName, CancellationToken ct = default)
    {
        var v = await _db.ConfigurationEntries.FirstOrDefaultAsync(c => c.Key == "security.allowAuthenticatedExternalSend", ct);
        // Por defecto permitimos envío externo para envíos originados en buzones locales/autenticados.
        return v == null || string.Equals(v.Value, "true", StringComparison.OrdinalIgnoreCase);
    }
}