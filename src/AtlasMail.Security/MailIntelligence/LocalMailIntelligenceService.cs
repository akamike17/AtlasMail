using AtlasMail.Application.Abstractions;

namespace AtlasMail.Security.MailIntelligence;

/// <summary>
/// IA local heurística (spec §31). NO usa modelos externos ni envía contenido fuera del servidor:
/// prioridad, clasificación, riesgo de phishing asistido y resumen extractivo son deterministas.
/// Es una capa de ASISTENCIA — nunca reemplaza el juicio del usuario ni la decisión de seguridad
/// (antispam/antimalware). El servidor funciona perfectamente sin esta IA (Disabled por defecto).
/// </summary>
public sealed class LocalMailIntelligenceService : IMailIntelligenceService
{
    public bool Enabled => true;

    // Señales de urgencia (asunto + cuerpo)
    private static readonly HashSet<string> UrgentTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "urgente", "asap", "immediately", "urgent", "inmediato", "critical", "critical alert",
        "acción requerida", "action required", "hoy", "today", "deadline", "vencimiento", "expira",
        "q4", "final", "final warning", "último aviso"
    };

    private static readonly HashSet<string> SecurityTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "contraseña", "clic", "click", "login", "cuenta bloqueada", "account locked",
        "verificación", "verification", "sospechosa", "suspicious", "credenciales", "credentials"
    };

    private static readonly HashSet<string> FinanceTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "invoice", "factura", "pago", "payment", "balance", "saldo", "reembolso", "refund",
        "payment due", "pago pendiente", "tarjeta", "card", "transferencia", "bank"
    };

    private static readonly HashSet<string> MarketingTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "descuento", "discount", "oferta", "offer", "promo", "publicidad", "advertis", "sale",
        "gratis", "free", "cupón", "coupon"
    };

    // Señales de phishing asistido (heurístico, alto falso-positivo posible)
    private static readonly string[] PhishingSignalsList =
    {
        "actúa ahora, o tu cuenta", "verify your account", "verifica tu cuenta ahora",
        "confirm your identity", "confirm your password", "urgent account action required",
        "clic aquí para restablecer", "click here to reset", "suspicious activity, sign in",
        "your account will be suspended", "tu cuenta será suspendida", "unusual sign-in",
        "login details attached", "password attached"
    };

    private static readonly string[] UrgencyPhish =
    {
        "inmediatamente", "immediately", "your account will be closed",
        "action taken within 24 hours"
    };

    public int Priority(SubjectBody sb)
    {
        var text = Combine(sb).ToLowerInvariant();
        int score = 25; // base moderada
        int urgent = UrgentTokens.Count(t => text.Contains(t, StringComparison.Ordinal));
        int sec = SecurityTokens.Count(t => text.Contains(t, StringComparison.Ordinal));
        score += Math.Min(urgent, 10) * 6;                       // hasta +60 por urgencia
        score += Math.Min(sec, 3) * 5;                            // hasta +15 por seguridad
        if (text.Contains('!')) score += 5;
        if (sb.Subject.Length > 60) score += 3;
        return Math.Clamp(score, 0, 100);
    }

    public MailIntelligenceCategory Classify(SubjectBody sb)
    {
        var text = Combine(sb).ToLowerInvariant();
        if (SecurityTokens.Count(t => text.Contains(t, StringComparison.Ordinal)) >= 2) return MailIntelligenceCategory.Security;
        if (UrgentTokens.Count(t => text.Contains(t, StringComparison.Ordinal)) >= 2) return MailIntelligenceCategory.Urgent;
        if (FinanceTokens.Count(t => text.Contains(t, StringComparison.Ordinal)) >= 1) return MailIntelligenceCategory.Finance;
        if (MarketingTokens.Count(t => text.Contains(t, StringComparison.Ordinal)) >= 1) return MailIntelligenceCategory.Marketing;
        if (text.Contains("unsubscribe", StringComparison.Ordinal) || text.Contains("no reply", StringComparison.Ordinal)
            || text.Contains("newsletter", StringComparison.Ordinal) || text.Contains("no contestar")) return MailIntelligenceCategory.Newsletter;
        if (text.Contains("linkedin", StringComparison.Ordinal) || text.Contains("facebook") || text.Contains("twitter")
            || text.Contains("x.com") || text.Contains("instagram")) return MailIntelligenceCategory.Social;
        if (text.Contains("status", StringComparison.Ordinal) || text.Contains("update", StringComparison.Ordinal)
            || text.Contains("notificación", StringComparison.Ordinal) || text.Contains("notification")) return MailIntelligenceCategory.Notification;
        return MailIntelligenceCategory.General;
    }

    public PhishingAssessment AssessPhishing(SubjectBody sb)
    {
        var text = Combine(sb).ToLowerInvariant();
        var signals = new List<string>();
        int score = 0;
        foreach (var s in PhishingSignalsList)
        {
            if (text.Contains(s, StringComparison.Ordinal))
            {
                signals.Add(s);
                score += 20;
            }
        }
        foreach (var u in UrgencyPhish)
        {
            if (text.Contains(u, StringComparison.Ordinal)) { signals.Add("urgencia: " + u); score += 10; }
        }
        // URLs con IP, redirectors, URL shorteners (ásistido; no bloqueo por sí solo)
        if (ContainsIpUrl(text)) { signals.Add("url_bruta_ip"); score += 15; }
        if (ContainsRedirector(text)) { signals.Add("url_redirector"); score += 10; }
        if (score > 100) score = 100;
        return new PhishingAssessment(score, signals);
    }

    public string Summarize(SubjectBody sb, int maxChars = 180)
    {
        var body = (sb.Body ?? "").Trim();
        if (string.IsNullOrWhiteSpace(body))
            return string.IsNullOrWhiteSpace(sb.Subject) ? "(sin contenido)" : sb.Subject.Trim();
        // resumen extractivo: primeras frases significativas
        var sentences = System.Text.RegularExpressions.Regex.Split(body, @"(?<=[.!?])\s+");
        var sb2 = new System.Text.StringBuilder();
        foreach (var s in sentences)
        {
            var clean = s.Trim();
            if (clean.Length == 0 || IsBoilerplate(clean)) continue;
            if (sb2.Length + clean.Length > maxChars) break;
            sb2.Append(clean).Append(' ');
        }
        var result = sb2.ToString().Trim();
        if (result.Length == 0) result = body.Trim();
        if (result.Length > maxChars) result = result[..maxChars].TrimEnd() + "…";
        return result;
    }

    private static bool IsBoilerplate(string s)
    {
        var lower = s.ToLowerInvariant();
        return lower.StartsWith("this e-mail") || lower.StartsWith("este correo")
            || lower.StartsWith("confidential") || lower.StartsWith("confidencial")
            || lower.StartsWith("content may contain") || lower.StartsWith("sent from");
    }

    private static string Combine(SubjectBody sb)
        => (sb.Subject ?? "") + " " + (sb.Body ?? "");

    private static bool ContainsIpUrl(string text)
        => System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(?:https?://)?\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(?:/\S*)?\b");

    private static bool ContainsRedirector(string text)
        => text.Contains("bit.ly") || text.Contains("tinyurl") || text.Contains("goo.gl")
        || text.Contains("t.co") || text.Contains("rebrand.ly") || text.Contains("is.gd");
}

/// <summary>IA deshabilitada (default): servidor funciona sin ella, §31.</summary>
public sealed class DisabledMailIntelligence : IMailIntelligenceService
{
    public bool Enabled => false;
    public int Priority(SubjectBody sb) => 0;
    public MailIntelligenceCategory Classify(SubjectBody sb) => MailIntelligenceCategory.General;
    public PhishingAssessment AssessPhishing(SubjectBody sb) => new(0, Array.Empty<string>());
    public string Summarize(SubjectBody sb, int maxChars = 180) => "(IA deshabilitada)";
}