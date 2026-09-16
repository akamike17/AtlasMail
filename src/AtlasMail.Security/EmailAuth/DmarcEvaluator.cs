using System.Text;
using AtlasMail.Application.Abstractions;

namespace AtlasMail.Security.EmailAuth;

/// <summary>Resultado DMARC (RFC 7489 §6.6).</summary>
public enum DmarcResult
{
    Pass,
    Fail,
    None,
    TempError,
    PermError,
    NoRecord
}

/// <summary>El resultado DMARC completo con la política a aplicar.</summary>
public sealed record DmarcEvaluation(
    DmarcResult Result,
    string Policy,          // none / quarantine / reject
    string? Rua,
    string? Ruf,
    string Detail,
    string? SpfDomain,
    SpfResult Spf,
    string? DkimDomain,
    DkimResult Dkim)
{
    public bool ShouldReject => Policy.Equals("reject", StringComparison.OrdinalIgnoreCase) && Result == DmarcResult.Fail;
    public bool ShouldQuarantine => Policy.Equals("quarantine", StringComparison.OrdinalIgnoreCase) && Result == DmarcResult.Fail;
}

/// <summary>
/// Evaluador DMARC (RFC 7489). Dado el remitente del envelope, el dominio From del header,
/// el resultado SPF, el resultado DKIM y el registro DMARC (o el resolver DNS), evalúa la
/// alineación (relaxed/strict) y devuelve la política aplicable. Es puro y testeable.
/// </summary>
public sealed class DmarcEvaluator
{
    private readonly IDnsRecordResolver? _dns;

    public DmarcEvaluator(IDnsRecordResolver? dns = null) => _dns = dns;

    public async Task<DmarcEvaluation> EvaluateAsync(
        string envelopeFromDomain,
        string headerFromDomain,
        SpfEvaluation spf,
        DkimVerification dkim,
        string? explicitDmarcRecord = null,
        CancellationToken ct = default)
    {
        var record = explicitDmarcRecord;
        if (record == null)
        {
            if (_dns == null) return new DmarcEvaluation(DmarcResult.TempError, "", null, null, "no-dns", envelopeFromDomain, spf.Result, dkim.Domain ?? "", dkim.Result);
            var txts = await _dns.GetTxtAsync("_dmarc." + headerFromDomain, ct);
            record = txts.FirstOrDefault(t => t.TrimStart().StartsWith("v=DMARC1"));
            if (record == null)
                return new DmarcEvaluation(DmarcResult.NoRecord, "", null, null, "no-record:" + headerFromDomain, envelopeFromDomain, spf.Result, dkim.Domain ?? "", dkim.Result);
        }
        if (!record.TrimStart().StartsWith("v=DMARC1"))
            return new DmarcEvaluation(DmarcResult.PermError, "", null, null, "invalid-version", envelopeFromDomain, spf.Result, dkim.Domain ?? "", dkim.Result);

        var parsed = ParseRecord(record);
        string policy = GetOrDefault(parsed, "p", "none");
        string? rua = GetOrDefault(parsed, "rua", null);
        string? ruf = GetOrDefault(parsed, "ruf", null);
        string alignmentMode = GetOrDefault(parsed, "adkim", "r") + "/" + GetOrDefault(parsed, "aspf", "r");

        // Nota: alignment se evalúa por separado; la decisión DMARC combina SPF+alignment o DKIM+alignment
        bool spfAligned = SpfAligned(spf, envelopeFromDomain, headerFromDomain, GetOrDefault(parsed, "aspf", "r"));
        bool dkimAligned = dkim.Passed && DkimAligned(dkim.Domain ?? "", headerFromDomain, GetOrDefault(parsed, "adkim", "r"));

        bool anyAligned = spfAligned || dkimAligned;
        var result = anyAligned ? DmarcResult.Pass : DmarcResult.Fail;
        string detail = anyAligned
            ? (spfAligned ? "spf-aligned" : "dkim-aligned")
            : "no-alignment (" + alignmentMode + ")";

        return new DmarcEvaluation(result, policy, rua, ruf, detail, envelopeFromDomain, spf.Result, dkim.Domain ?? "", dkim.Result);
    }

    private static bool SpfAligned(SpfEvaluation spf, string envelopeDomain, string headerDomain, string mode)
    {
        if (spf.Result != SpfResult.Pass) return false;
        return Align(envelopeDomain, headerDomain, mode);
    }

    private static bool DkimAligned(string dkimDomain, string headerDomain, string mode) =>
        Align(dkimDomain, headerDomain, mode);

    /// <summary>Alineación relaxed ('r'): el dominio SPF/DKIM debe ser el mismo dominio orgánico
    /// (mismo registrable domain). Alineación strict ('s'): exactamente igual.</summary>
    public static bool Align(string a, string b, string mode)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        if (mode.StartsWith("s", StringComparison.OrdinalIgnoreCase)) return false;
        return RegistrableDomain(a).Equals(RegistrableDomain(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extrae el "registrable domain" (dominio orgánico). Para ccTLD de 2 letras
    /// (co.uk, com.mx) usa los últimos 3; para TLD normales los últimos 2.</summary>
    public static string RegistrableDomain(string domain)
    {
        var parts = domain.ToLowerInvariant().TrimEnd('.').Split('.');
        if (parts.Length <= 2) return string.Join(".", parts);
        var tld = parts[^1];
        if (tld.Length == 2) return string.Join(".", parts[^3..]); // ejemplo.co.uk / empresa.com.mx
        return string.Join(".", parts[^2..]);
    }

    private static Dictionary<string, string> ParseRecord(string record)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = record.Split(';');
        foreach (var p in parts)
        {
            int eq = p.IndexOf('=');
            if (eq > 0)
            {
                string k = p[..eq].Trim();
                string v = p[(eq + 1)..].Trim();
                if (result.ContainsKey(k)) result[k] = result[k] + " " + v;
                else result[k] = v;
            }
        }
        return result;
    }

    private static string GetOrDefault(Dictionary<string, string> d, string key, string def) =>
        d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;
}