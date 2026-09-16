using System.Text;
using AtlasMail.Application.Abstractions;
using AtlasMail.Security.EmailAuth;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Infrastructure.EmailAuth;

/// <summary>
/// Orquesta la autenticación de correo en recepción (FASE 4): evalúa SPF (con la IP del
/// remitente), DKIM (extrae firmas DKIM-Signature del mensaje y las verifica contra la clave
/// pública del selector en DNS) y DMARC (combina SPF/DKIM con alignment y política).
/// Devuelve el reporte para que la ingesta lo integre al scoring y la política.
/// </summary>
public sealed class EmailAuthenticationService : IEmailAuthenticationService
{
    private readonly IDnsRecordResolver _dns;
    private readonly SpfEvaluator _spf;
    private readonly DmarcEvaluator _dmarc;
    private readonly ILogger<EmailAuthenticationService> _logger;
    private readonly bool _dmarcEnforce; // si false, se audita pero no se rechaza jamás

    public EmailAuthenticationService(IDnsRecordResolver dns,
        bool dmarcEnforce = false,
        ILogger<EmailAuthenticationService>? logger = null)
    {
        _dns = dns;
        _spf = new SpfEvaluator(dns);
        _dmarc = new DmarcEvaluator(dns);
        _dmarcEnforce = dmarcEnforce;
        _logger = logger ?? AtlasMail.Infrastructure.NullLogger<EmailAuthenticationService>.Instance;
    }

    public async Task<EmailAuthReport> AuthenticateAsync(
        string fromHeaderDomain, string envelopeFrom, string? clientIp,
        byte[] rawMime, CancellationToken ct = default)
    {
        // 1) SPF
        var spf = await _spf.EvaluateAsync(clientIp ?? "0.0.0.0", null, envelopeFrom, null, ct);

        // 2) DKIM: verificar la primera firma que pase / o la más reciente
        var dkimVer = VerifyFirstDkim(rawMime, ct);

        // 3) DMARC
        var dmarc = await _dmarc.EvaluateAsync(spf.Domain ?? "", fromHeaderDomain, spf, dkimVer, null, ct);

        var authResults = new List<string>
        {
            $"spf={spf.Result.ToString().ToLowerInvariant()} smtp.mailfrom={spf.Domain ?? ""}",
            dkimVer.Result == DkimResult.NoSignature
                ? "dkim=none"
                : $"dkim={dkimVer.Result.ToString().ToLowerInvariant()} header.d={(dkimVer.Domain ?? "")}",
            $"dmarc={dmarc.Result.ToString().ToLowerInvariant()} policy={(dmarc.Policy ?? "none")}",
        };

        bool shouldReject = dmarc.ShouldReject && _dmarcEnforce;
        bool shouldQuarantine = dmarc.ShouldQuarantine && _dmarcEnforce;

        _logger.LogInformation("EmailAuth {From}: spf={Spf} dkim={Dkim} dmarc={Dmarc} pol={Pol}",
            fromHeaderDomain, spf.Result, dkimVer.Result, dmarc.Result, dmarc.Policy);

        return new EmailAuthReport(
            SpfResult: spf.Result.ToString(),
            SpfRule: spf.Rule,
            DkimResult: dkimVer.Result.ToString(),
            DkimSelector: dkimVer.Selector,
            DkimDomain: dkimVer.Domain,
            DmarcResult: dmarc.Result.ToString(),
            DmarcPolicy: dmarc.Policy ?? "none",
            DmarcAligned: dmarc.Result == DmarcResult.Pass,
            ShouldReject: shouldReject,
            ShouldQuarantine: shouldQuarantine,
            AuthResults: authResults);
    }

    /// <summary>Extrae todas las DKIM-Signature y devuelve la verificación de la primera que pase.
    /// Si ninguna es válida/signature incorrecta, devuelve la primera con resultado de fallo.</summary>
    private DkimVerification VerifyFirstDkim(byte[] rawMime, CancellationToken ct)
    {
        var signatures = ExtractDkimSignatures(rawMime);
        if (signatures.Count == 0)
            return new DkimVerification(DkimResult.NoSignature, null, null, "no-signature");

        DkimVerification? lastFail = null;
        foreach (var (sig, selector, domain) in signatures)
        {
            // TLS: obtener clave pública: <selector>._domainkey.<domain>
            try
            {
                var name = $"{selector}._domainkey.{domain}";
                var txts = _dns.GetTxtAsync(name, ct).GetAwaiter().GetResult();
                string pk = ExtractDkimKeyFromTxt(txts);
                if (string.IsNullOrEmpty(pk)) { lastFail ??= new DkimVerification(DkimResult.PermError, selector, domain, "no-public-key"); continue; }
                var ver = Dkim.Verify(rawMime, sig, pk);
                if (ver.Passed) return ver;
                lastFail ??= ver;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastFail ??= new DkimVerification(DkimResult.TempError, selector, domain, "dns-error:" + ex.Message);
            }
        }
        return lastFail ?? new DkimVerification(DkimResult.PermError, null, null, "verification-failed");
    }

    private static List<(string Sig, string Selector, string Domain)> ExtractDkimSignatures(byte[] rawMime)
    {
        var result = new List<(string, string, string)>();
        var text = Encoding.UTF8.GetString(rawMime);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) break; // fin de headers
            if (!line.StartsWith("DKIM-Signature:", StringComparison.OrdinalIgnoreCase)) continue;
            var val = line[(line.IndexOf(':') + 1)..].Trim();
            var sel = ExtractTag(val, "s");
            var dom = ExtractTag(val, "d");
            if (sel != null && dom != null) result.Add((val, sel, dom));
        }
        return result;
    }

    private static string? ExtractTag(string sig, string tag)
    {
        // tolerante: k=v; separado por ';'
        foreach (var part in sig.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim().Equals(tag, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }
        return null;
    }

    private static string ExtractDkimKeyFromTxt(IReadOnlyList<string> txts)
    {
        foreach (var txt in txts)
        {
            var t = txt.Trim();
            if (t.StartsWith("v=DKIM1", StringComparison.OrdinalIgnoreCase) || t.Contains("; p=", StringComparison.Ordinal))
            {
                var p = ExtractTag(t, "p");
                return p ?? string.Empty;
            }
            if (t.StartsWith("p=", StringComparison.OrdinalIgnoreCase)) return t[2..].Trim();
        }
        return txts.FirstOrDefault() ?? string.Empty;
    }
}