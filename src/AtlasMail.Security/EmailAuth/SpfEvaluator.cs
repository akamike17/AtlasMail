using System.Net;
using System.Net.Sockets;
using System.Text;
using AtlasMail.Application.Abstractions;

namespace AtlasMail.Security.EmailAuth;

/// <summary>Resultado SPF (RFC 7208).</summary>
public enum SpfResult
{
    Pass,
    Fail,
    SoftFail,
    Neutral,
    None,
    TempError,
    PermError
}

/// <summary>Resultado de una evaluación SPF con la regla que la motivó.</summary>
public sealed record SpfEvaluation(SpfResult Result, string Rule, string? Detail)
{
    /// <summary>Dominio evaluado (envelope-from), para usar en DMARC alignment.</summary>
    public string? Domain { get; init; }
}

/// <summary>
/// Evaluador SPF (RFC 7208). Puro y testeable: recibe el registro SPF, la IP del
/// remitente, el envelope-from y un IDnsRecordResolver para mecanismos a/mx/include.
/// Soporta mecanismos ip4, ip6, a, mx, include, exists, all; qualifiers +, -, ~, ?;
/// la directiva redirect y macros comunes %{i} %{s} %{d} %{d4} %{d6} %{l} %{o}.
/// </summary>
public sealed class SpfEvaluator
{
    private readonly IDnsRecordResolver? _dns;
    private const int MaxDnsLookups = 10; // RFC 7208 §4.6.4

    public SpfEvaluator(IDnsRecordResolver? dns = null) => _dns = dns;

    public async Task<SpfEvaluation> EvaluateAsync(
        string clientIp,
        string? heloDomain,
        string envelopeFrom,
        string? explicitSpfRecord = null,
        CancellationToken ct = default)
    {
        // None: sin dominio de origen
        if (string.IsNullOrWhiteSpace(envelopeFrom) || envelopeFrom == "<>")
            return new SpfEvaluation(SpfResult.None, "no-envelope-from", null);

        string fromDomain = ExtractDomain(envelopeFrom);
        if (string.IsNullOrEmpty(fromDomain))
            return new SpfEvaluation(SpfResult.None, "no-from-domain", null);

        var record = explicitSpfRecord;
        if (record == null)
        {
            if (_dns == null) return new SpfEvaluation(SpfResult.TempError, "no-dns", null) { Domain = fromDomain };
            var txts = await _dns.GetTxtAsync(fromDomain, ct);
            record = txts.FirstOrDefault(t => t.TrimStart().StartsWith("v=spf1"));
            if (record == null)
            {
                // Sin registro pero con dominio: None
                return new SpfEvaluation(SpfResult.None, "no-spf-record", fromDomain) { Domain = fromDomain };
            }
        }
        if (!record.TrimStart().StartsWith("v=spf1"))
            return new SpfEvaluation(SpfResult.PermError, "invalid-version", record) { Domain = fromDomain };

        try
        {
            var outcome = await EvaluateRecordAsync(record, clientIp, fromDomain, heloDomain, envelopeFrom, 0, ct);
            outcome ??= new SpfEvaluation(SpfResult.PermError, "no-all", record);
            return outcome with { Domain = fromDomain };
        }
        catch (OperationCanceledException) { throw; }
        catch (DnsLookupLimitedException)
        {
            return new SpfEvaluation(SpfResult.PermError, "dns-lookups-exceeded", "max 10 DNS lookups") { Domain = fromDomain };
        }
        catch (Exception)
        {
            return new SpfEvaluation(SpfResult.TempError, "evaluation-error", null) { Domain = fromDomain };
        }
    }

    private async Task<SpfEvaluation?> EvaluateRecordAsync(
        string record, string clientIp, string currentDomain, string? helo, string envelopeFrom, int depth, CancellationToken ct)
    {
        if (record == "v=spf1" || string.IsNullOrWhiteSpace(record)) 
            return new SpfEvaluation(SpfResult.None, "empty-record", record);

        var terms = Tokenize(record);
        string? redirect = null;
        foreach (var term in terms)
        {
            if (term == "v=spf1") continue;
            if (term.StartsWith("%", StringComparison.Ordinal)) continue;

            char q = '+';
            if ("+-~?".Contains(term[0])) { q = term[0]; }

            string body = (q == '+' ? term : term[1..]).Trim();

            if (body.StartsWith("ip4:", StringComparison.Ordinal))
            {
                var cidr = MacroExpand(body[4..], currentDomain, helo, envelopeFrom, clientIp) ?? body[4..];
                if (IpMatches(clientIp, cidr, AddressFamily.InterNetwork))
                    return Terminate(q, "ip4:" + cidr);
            }
            else if (body.StartsWith("ip6:", StringComparison.Ordinal))
            {
                var cidr = MacroExpand(body[4..], currentDomain, helo, envelopeFrom, clientIp) ?? body[4..];
                if (IpMatches(clientIp, cidr, AddressFamily.InterNetworkV6))
                    return Terminate(q, "ip6:" + cidr);
            }
            else if (body.StartsWith("a:", StringComparison.Ordinal))
            {
                if (await AddressDnsMatchesAsync(body[2..], clientIp, depth, ct))
                    return Terminate(q, "a:" + body[2..]);
            }
            else if (body == "a")
            {
                if (await AddressDnsMatchesAsync(currentDomain, clientIp, depth, ct))
                    return Terminate(q, "a");
            }
            else if (body.StartsWith("mx:", StringComparison.Ordinal))
            {
                if (await MxDnsMatchesAsync(body[3..], clientIp, depth, ct))
                    return Terminate(q, "mx:" + body[3..]);
            }
            else if (body == "mx")
            {
                if (await MxDnsMatchesAsync(currentDomain, clientIp, depth, ct))
                    return Terminate(q, "mx");
            }
            else if (body.StartsWith("include:", StringComparison.Ordinal))
            {
                var includeDomain = MacroExpand(body[8..], currentDomain, helo, envelopeFrom, clientIp) ?? body[8..];
                if (_dns == null) return new SpfEvaluation(SpfResult.TempError, "no-dns", null);
                var txts = await _dns.GetTxtAsync(includeDomain, ct);
                var incRecord = txts.FirstOrDefault(t => t.TrimStart().StartsWith("v=spf1"));
                if (incRecord == null)
                {
                    // include sin registro → PermError del conjunto; per RFC, permerror
                    return new SpfEvaluation(SpfResult.PermError, "include-no-record:" + includeDomain, null);
                }
                var sub = await EvaluateRecordAsync(incRecord, clientIp, includeDomain, helo, envelopeFrom, depth + 1, ct);
                if (sub == null) return null;
                // include: Pass/Fail/SoftFail (cualquier 4xx/5xx-lite) → si es Pass, el conjunto es Pass;
                // si el subresultado es un término explícito con + → Pass. else si Default, Neutral.
                if (sub.Result == SpfResult.Pass && sub.Rule.StartsWith("+") || sub.Result == SpfResult.Pass)
                    return new SpfEvaluation(SpfResult.Pass, "include:" + includeDomain + ":" + sub.Rule, null);
                if (sub.Result == SpfResult.PermError)
                    return sub;
                if (sub.Result is SpfResult.Fail or SpfResult.SoftFail or SpfResult.Neutral)
                    // RFC: si el include devuelve Pass→Pass; Fail→PermError en el conjunto; 
                    // otros (SoftFail/Neutral/None) → None/continue. Simplificación estándar:
                    continue;
            }
            else if (body.StartsWith("exists:", StringComparison.Ordinal))
            {
                if (_dns == null) return new SpfEvaluation(SpfResult.TempError, "no-dns", null);
                var macroDomain = MacroExpand(body[7..], currentDomain, helo, envelopeFrom, clientIp);
                var ips = await _dns.GetAddressesAsync(macroDomain ?? "", ct);
                if (ips.Count > 0) return Terminate(q, "exists:" + macroDomain);
            }
            else if (body.StartsWith("redirect=", StringComparison.Ordinal))
            {
                redirect = MacroExpand(body[9..], currentDomain, helo, envelopeFrom, clientIp) ?? body[9..];
            }
            else if (body == "all")
            {
                return Terminate(q, "all");
            }
            // mecanismo desconocido → ignorar (neutral)
        }

        if (redirect != null)
        {
            if (_dns == null) return new SpfEvaluation(SpfResult.TempError, "no-dns", null);
            var txts = await _dns.GetTxtAsync(redirect, ct);
            var rec = txts.FirstOrDefault(t => t.TrimStart().StartsWith("v=spf1"));
            if (rec == null) return new SpfEvaluation(SpfResult.PermError, "redirect-no-record:" + redirect, null);
            var sub = await EvaluateRecordAsync(rec, clientIp, redirect, helo, envelopeFrom, depth + 1, ct);
            return sub;
        }
        // Si no hubo match, no hay redirect y no se definió ningún mecanismo "all" → PermError (registro incompleto)
        if (!terms.Any(t => t.TrimEnd().EndsWith("all", StringComparison.Ordinal)))
            return new SpfEvaluation(SpfResult.PermError, "missing-all", record);
        // Sin match ni all (all dio ~all pero no coincidió) → Neutral
        return new SpfEvaluation(SpfResult.Neutral, "no-match", null);
    }

    private static IPAddress? NormalizeIp(string ip)
    {
        if (IPAddress.TryParse(ip, out var a)) return a;
        return null;
    }

    private static bool IpMatches(string clientIp, string cidr, AddressFamily family)
    {
        if (!IPAddress.TryParse(clientIp, out var client)) return false;
        var parts = cidr.Split('/');
        string net = parts[0];
        if (!IPAddress.TryParse(net, out var network)) return false;
        if (network.AddressFamily != family || client.AddressFamily != family) return false;
        int prefix = parts.Length == 2 && int.TryParse(parts[1], out var p) ? p : FullPrefix(family);
        prefix = Math.Min(prefix, FullPrefix(family));
        return PrefixMatch(client, network, prefix);
    }

    private static int FullPrefix(AddressFamily f) => f == AddressFamily.InterNetworkV6 ? 128 : 32;

    private static bool PrefixMatch(IPAddress a, IPAddress b, int prefix)
    {
        var ab = a.GetAddressBytes(); var bb = b.GetAddressBytes();
        if (ab.Length != bb.Length) return false;
        int full = prefix / 8;
        for (int i = 0; i < full; i++) if (ab[i] != bb[i]) return false;
        if (full < ab.Length && prefix % 8 != 0)
        {
            int bits = prefix % 8;
            byte mask = (byte)(0xFF << (8 - bits));
            if ((ab[full] & mask) != (bb[full] & mask)) return false;
        }
        return true;
    }

    private async Task<bool> AddressDnsMatchesAsync(string domain, string clientIp, int depth, CancellationToken ct)
    {
        DomainCheck(depth);
        if (_dns == null) return false;
        var addresses = await _dns.GetAddressesAsync(domain, ct);
        return addresses.Any(ip => SameIp(ip, clientIp));
    }

    private async Task<bool> MxDnsMatchesAsync(string domain, string clientIp, int depth, CancellationToken ct)
    {
        // Para SPF mx: resolver el A de los hosts MX; simplificación: consultamos MX y A del dominio.
        DomainCheck(depth);
        if (_dns == null) return false;
        var addresses = await _dns.GetAddressesAsync(domain, ct);
        return addresses.Any(ip => SameIp(ip, clientIp));
    }

    private static bool SameIp(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        // soportar ::ffff:1.2.3.4 (IPv4-mapped)
        if (a.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase)) a = a[7..];
        if (b.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase)) b = b[7..];
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static void DomainCheck(int depth)
    {
        if (depth > MaxDnsLookups) throw new DnsLookupLimitedException();
    }

    private static SpfEvaluation Terminate(char q, string rule)
    {
        string ruleWithQual = q == '+' ? rule : q + rule;
        return q switch
        {
            '-' => new SpfEvaluation(SpfResult.Fail, ruleWithQual, null),
            '~' => new SpfEvaluation(SpfResult.SoftFail, ruleWithQual, null),
            '?' => new SpfEvaluation(SpfResult.Neutral, ruleWithQual, null),
            _ => new SpfEvaluation(SpfResult.Pass, ruleWithQual, null)
        };
    }

    /// <summary>Macros SPF (RFC 7208 §7): %{d} %{i} %{s} %{l} %{o} %{h} %{d4}/%{d6} y %%.</summary>
    private static string? MacroExpand(string template, string currentDomain, string? helo, string envelopeFrom, string clientIp)
    {
        string? local = envelopeFrom.Contains('@') ? envelopeFrom[..envelopeFrom.IndexOf('@')] : envelopeFrom;
        string? host = envelopeFrom.Contains('@') ? envelopeFrom[(envelopeFrom.IndexOf('@') + 1)..] : currentDomain;
        var sb = new StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c != '%' || i + 1 >= template.Length) { sb.Append(c); i++; continue; }
            // %%
            if (template[i + 1] == '%') { sb.Append('%'); i += 2; continue; }
            // lee el token: %{X} o %X
            if (template[i + 1] == '{')
            {
                int close = template.IndexOf('}', i + 2);
                if (close < 0) { sb.Append(template[i..]); break; } // sin cerrar → resto literal
                string token = template[(i + 2)..close];
                sb.Append(MacroValue(token, currentDomain, helo, host, local, clientIp));
                i = close + 1;
            }
            else
            {
                sb.Append(MacroValue(template[i + 1].ToString(), currentDomain, helo, host, local, clientIp));
                i += 2;
            }
        }
        return sb.ToString();
    }

    private static string MacroValue(string token, string domain, string? helo, string host, string local, string clientIp)
    {
        char c = char.ToLowerInvariant(token[0]);
        string value = c switch
        {
            'd' => domain,
            's' => host,
            'l' => local,
            'i' => clientIp,
            'o' => host,
            'h' => helo ?? "",
            _ => token
        };
        if (token.Length == 2 && token[1] == '4' && c == 'd') return string.Join(".", domain.Split('.').Take(4));
        if (token.Length == 2 && token[1] == '6' && c == 'd') return string.Join(".", domain.Split('.').TakeLast(4));
        return value;
    }

    private static List<string> Tokenize(string record)
    {
        // separar por espacios, ignorando el prefijo v=spf1 duplicado
        return record.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    public static string ExtractDomain(string envelopeFrom)
    {
        var e = envelopeFrom.Trim();
        if (e.StartsWith("<")) e = e.Trim('<', '>');
        int at = e.IndexOf('@');
        return at >= 0 ? e[(at + 1)..] : string.Empty;
    }
}

/// <summary>Se lanza cuando se superan los límites de consultas DNS del SPF.</summary>
public sealed class DnsLookupLimitedException : Exception
{
    public DnsLookupLimitedException() : base("SPF DNS lookup limit exceeded") { }
}