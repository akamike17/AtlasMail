using AtlasMail.Domain.ValueObjects;

namespace AtlasMail.Domain.Rules;

/// <summary>Resultado de evaluar la política de relay para un sobre SMTP.</summary>
public enum RelayDecision
{
    /// <summary>Rechazar; destinatario no local y conexión no autenticada.</summary>
    Deny,
    /// <summary>Entregar localmente.</summary>
    DeliverLocal,
    /// <summary>Encaminar externamente. Sólo permitido para envío autenticado autorizado.</summary>
    DeliverRemote,
    /// <summary>Entregar localmente aplicando catch-all o plus addressing.</summary>
    DeliverLocalRewritten
}

public readonly struct RelayEvaluation
{
    public RelayDecision Decision { get; }
    public string? Reason { get; }

    public RelayEvaluation(RelayDecision decision, string? reason = null)
    {
        Decision = decision;
        Reason = reason;
    }
}

/// <summary>
/// Reglas de relay (sección 5 del spec):
///  - Internet -> únicamente dominios/buzones locales válidos.
///  - Autenticado autorizado -> puede enviar externamente conforme a política.
///  - Internet anónimo -> NO relay arbitrario.
/// NO OPEN RELAY.
/// </summary>
public static class RelayPolicy
{
    /// <summary>Decide si un sobre puede ser aceptado por el SMTP.</summary>
    public static RelayEvaluation Evaluate(
        EmailAddress mailFrom,
        EmailAddress rcptTo,
        bool authenticated,
        bool domainIsLocal,          // ¿el dominio destinatario es local?
        bool rcptExistsLocally,      // ¿existe el buzón/alias local para el localpart?
        bool allowAuthSend)
    {
        if (!authenticated)
        {
            // Conexión anónima de Internet: SOLO entregar localmente a dirección local válida
            if (domainIsLocal && rcptExistsLocally) return new RelayEvaluation(RelayDecision.DeliverLocal);
            if (domainIsLocal) return new RelayEvaluation(RelayDecision.Deny, "Recipient mailbox does not exist locally");
            return new RelayEvaluation(RelayDecision.Deny, "Anonymous relay to external domain is not allowed");
        }

        if (domainIsLocal)
        {
            // Autenticado hacia buzón local: requiere que exista, o que sea catch-all/plus
            if (rcptExistsLocally) return new RelayEvaluation(RelayDecision.DeliverLocal);
            return new RelayEvaluation(RelayDecision.DeliverLocalRewritten,
                "Local recipient will be resolved via alias/catch-all/plus addressing");
        }

        // Autenticado hacia dominio externo: permitido únicamente si la política lo permite
        if (allowAuthSend) return new RelayEvaluation(RelayDecision.DeliverRemote);
        return new RelayEvaluation(RelayDecision.Deny, "Authenticated submission to external domains is disabled by policy");
    }
}