using System.Security.Cryptography;
using System.Text;

namespace AtlasMail.Security.EmailAuth;

/// <summary>Algoritmo de canonicalización DKIM (RFC 6376 §3.4).</summary>
public enum DkimCanonicalization
{
    Simple,
    Relaxed
}

/// <summary>Resultado de una verificación DKIM.</summary>
public enum DkimResult
{
    Pass,
    Fail,
    PermError,
    TempError,
    NoSignature,
    BodyHashMismatch
}

/// <summary>Resultado de verificación con los detalles de la firma.</summary>
public sealed record DkimVerification(DkimResult Result, string? Selector, string? Domain, string Detail)
{
    public bool Passed => Result == DkimResult.Pass;
}

/// <summary>
/// Firma y verificación DKIM (RFC 6376) con RSA-SHA256.
///  - Firmar: selecciona headers, genera DKIM-Signature (b=, bh=), canonalización simple/relaxed.
///  - Verificar: parsea la firma, reconstruye el header para firmar (s=, d=, h=, canonicalization),
///    verifica la firma con la clave pública obtenida del DNS (proporcionada por el llamador).
/// La clave privada nunca se registra; solo se expone la pública para el registro DNS.
/// </summary>
public static class Dkim
{
    public const string Selector = "atlasmail"; // selector por defecto
    public const int RsaKeyBits = 2048;

    public static (string PrivateKeyPem, string PublicKeyPem) GenerateKeyPair(int bits = RsaKeyBits)
    {
        using var rsa = RSA.Create(bits);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    /// <summary>Deriva la clave pública (PEM) de una clave privada PKCS8 PEM.</summary>
    public static string GetPublicKeyPem(string privateKeyPem)
    {
        using var rsa = PrivateKeyRsa(privateKeyPem);
        return rsa.ExportSubjectPublicKeyInfoPem();
    }

    private static RSA PrivateKeyRsa(string pem)
    {
        var rsa = RSA.Create();
        var normalized = pem.Replace("\r\n", "\n");
        var base64 = normalized
            .Replace("-----BEGIN PRIVATE KEY-----", "").Replace("-----END PRIVATE KEY-----", "")
            .Replace("-----BEGIN RSA PRIVATE KEY-----", "").Replace("-----END RSA PRIVATE KEY-----", "")
            .Replace("\n", "").Replace(" ", "").Trim();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(base64), out _);
        return rsa;
    }

    /// <summary>Construye la cadena de registro DNS TXT del selector (registro k=rsa; p=...).</summary>
    public static string PublicKeyRecord(string publicKeyPem)
    {
        var key = publicKeyPem
            .Replace("-----BEGIN PUBLIC KEY-----", "")
            .Replace("-----END PUBLIC KEY-----", "")
            .Replace("\n", "").Replace("\r", "").Trim();
        return $"v=DKIM1; k=rsa; p={key}";
    }

    /// <summary>Firma un mensaje MIME y devuelve el header DKIM-Signature a insertar (sin el nombre del header).</summary>
    public static string Sign(
        byte[] rawMime,
        string domain,
        string privateKeyPem,
        string selector = Selector,
        string[]? signedHeaders = null,
        DkimCanonicalization headerC = DkimCanonicalization.Relaxed,
        DkimCanonicalization bodyC = DkimCanonicalization.Relaxed,
        DateTimeOffset? timestamp = null)
    {
        var headers = ParseHeaders(rawMime);
        if (signedHeaders == null)
        {
            signedHeaders = new[]
            {
                "From", "To", "Cc", "Subject", "Date", "Message-ID", "MIME-Version",
                "Content-Type", "Content-Transfer-Encoding", "In-Reply-To", "References"
            };
        }

        var bodyHash = HashBody(rawMime, bodyC);
        string b64BodyHash = Convert.ToBase64String(bodyHash);

        var ts = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString();
        // v, a, c, d, s, t, bh son cabeceras de la firma (no se firman ellos ni b/bh).

        // Construimos el campo h= a partir de los headers que realmente existen
        var headersToSign = signedHeaders
            .SelectMany(h => headers.Where(x => x.Name.Equals(h, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.Name)
            .ToList();
        if (headersToSign.Count == 0) { headersToSign.Add(headers.Count > 0 ? headers[0].Name : "From"); }

        // La línea de la firma tal como se firmará (con b= vacío al final), igual que la
        // recontruye Verify. RFC 6376: la cabecera DKIM-Signature a firmar incluye b= sin valor.
        string sigHeaderForData = "v=1; a=rsa-sha256; c=" + CanonInt(headerC) + "/" + CanonInt(bodyC) + "; d=" + domain
            + "; h=" + string.Join(":", headersToSign) + "; s=" + selector + "; t=" + ts + "; bh=" + b64BodyHash + "; b=";

        var dataToSign = BuildDataToSign(headers, headersToSign, headerC, sigHeaderForData);

        string signatureBase64;
        using (var rsa = ImportPrivate(privateKeyPem))
        {
            signatureBase64 = Convert.ToBase64String(rsa.SignData(dataToSign, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        // Relaxed: campos ordenados con '; ' y parámetros sin espacios extra.
        var fields = "v=1; a=rsa-sha256; c=" + CanonInt(headerC) + "/" + CanonInt(bodyC)
            + "; d=" + domain + "; h=" + string.Join(":", headersToSign) + "; s=" + selector
            + "; t=" + ts + "; bh=" + b64BodyHash + "; b=" + signatureBase64;
        return fields;
    }

    public static DkimVerification Verify(
        byte[] rawMime,
        string signatureHeader,
        string publicKey)
    {
        var headers = ParseHeaders(rawMime);
        var sig = ParseSignature(signatureHeader);
        if (sig == null) return new DkimVerification(DkimResult.PermError, null, null, "unparseable-signature");

        string? domain = Get(sig, "d");
        string? selector = Get(sig, "s");
        string? a = Get(sig, "a");
        string? bh = Get(sig, "bh");
        string? h = Get(sig, "h");
        string? b = Get(sig, "b");
        string? c = Get(sig, "c");

        if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(selector)) 
            return new DkimVerification(DkimResult.PermError, selector, domain, "missing d/s");
        if (!string.Equals(a, "rsa-sha256", StringComparison.OrdinalIgnoreCase))
            return new DkimVerification(DkimResult.PermError, selector, domain, "unsupported-alg:" + a);

        var bodyC = DkimCanonicalization.Relaxed;
        var headerC = DkimCanonicalization.Relaxed;
        if (!string.IsNullOrEmpty(c) && c.Contains('/'))
        {
            var parts = c.Split('/');
            headerC = ParseCanon(parts[0]);
            bodyC = ParseCanon(parts.Length > 1 ? parts[1] : "simple");
        }

        // verificar body hash
        var bodyHash = HashBody(rawMime, bodyC);
        if (bh != null && !FixedTimingEquals(bodyHash, Convert.FromBase64String(Unwrap(bh))))
            return new DkimVerification(DkimResult.BodyHashMismatch, selector, domain, "body-hash-mismatch");

        // headers a firmar
        var headersToSign = (h ?? string.Empty).Split(':').Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        if (headersToSign.Count == 0) return new DkimVerification(DkimResult.PermError, selector, domain, "no-h=");

        // Reconstruir la firma sin b= (pero manteniendo la primera instancia de b vacía)
        string sigHeaderForData = RebuildSignatureHeader(sig, domain, selector);
        var dataToVerify = BuildDataToSign(headers, headersToSign, headerC, sigHeaderForData);

        try
        {
            var pubKey = ImportPublic(publicKey);
            bool ok = pubKey.VerifyData(dataToVerify,
                Convert.FromBase64String(Unwrap(b ?? "")),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return ok
                ? new DkimVerification(DkimResult.Pass, selector, domain, "signature-verified")
                : new DkimVerification(DkimResult.Fail, selector, domain, "signature-mismatch");
        }
        catch (FormatException)
        {
            return new DkimVerification(DkimResult.PermError, selector, domain, "bad-base64");
        }
        catch (CryptographicException ex)
        {
            return new DkimVerification(DkimResult.PermError, selector, domain, "crypto:" + ex.Message);
        }
    }

    private static string CanonInt(DkimCanonicalization c) => c == DkimCanonicalization.Simple ? "simple" : "relaxed";
    private static DkimCanonicalization ParseCanon(string s) => s.Equals("simple", StringComparison.OrdinalIgnoreCase) ? DkimCanonicalization.Simple : DkimCanonicalization.Relaxed;

    private static RSA ImportPrivate(string pem)
    {
        var rsa = RSA.Create();
        // Normalizar PEM (puede venir con \r\n)
        pem = pem.Replace("\r\n", "\n");
        var base64 = pem
            .Replace("-----BEGIN PRIVATE KEY-----", "").Replace("-----END PRIVATE KEY-----", "")
            .Replace("-----BEGIN RSA PRIVATE KEY-----", "").Replace("-----END RSA PRIVATE KEY-----", "")
            .Replace("\n", "").Replace(" ", "").Trim();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(base64), out _);
        return rsa;
    }

    private static RSA ImportPublic(string pem)
    {
        var rsa = RSA.Create();
        pem = pem.Trim().Replace("\n", "").Replace("\r", "");
        if (pem.StartsWith("-----BEGIN"))
        {
            rsa.ImportFromPem(pem);
        }
        else
        {
            // P-params p=... desde DNS (asumimos k=rsa; p=base64)
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pem), out _);
        }
        return rsa;
    }

    // b= en la firma: recortar solo espacios/CRLF, preservar padding base64
    private static string Unwrap(string v) => v.Trim().TrimEnd('\r', '\n', ' ');

    private static string Get(Dictionary<string, string> sig, string key) =>
        sig.TryGetValue(key, out var v) ? v.Trim() : string.Empty;

    private static bool FixedTimingEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    private static Dictionary<string, string> ParseSignature(string sigHeader)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // separar '; ' fuera de valores
        var parts = sigHeader.Split(';');
        foreach (var p in parts)
        {
            int eq = p.IndexOf('=');
            if (eq > 0)
            {
                string k = p[..eq].Trim();
                string v = p[(eq + 1)..].Trim();
                // si ya existe (b= puede duplicarse), concatenar; tomamos en base a que el valor puede tener ';'
                if (result.ContainsKey(k) && !string.IsNullOrEmpty(result[k])) result[k] = result[k] + "=" + v;
                else result[k] = v;
            }
        }
        return result;
    }

    /// <summary>Reconstruye la línea DKIM-Signature sin data de la firma (b= sin valor) para el signing.</summary>
    private static string RebuildSignatureHeader(Dictionary<string, string> sig, string domain, string selector)
    {
        // RFC 6376 §3.7 algoritmo 2: v,a,c,d,h,s,t,bh; con b= vacío
        var sb = new StringBuilder("v=1;");
        sb.Append(" a=").Append(Get(sig, "a")).Append(';');
        if (sig.ContainsKey("c") && !string.IsNullOrEmpty(Get(sig, "c"))) sb.Append(" c=").Append(Get(sig, "c")).Append(';');
        sb.Append(" d=").Append(Get(sig, "d")).Append(';');
        sb.Append(" h=").Append(Get(sig, "h")).Append(';');
        sb.Append(" s=").Append(Get(sig, "s")).Append(';');
        if (sig.ContainsKey("t") && !string.IsNullOrEmpty(Get(sig, "t"))) sb.Append(" t=").Append(Get(sig, "t")).Append(';');
        sb.Append(" bh=").Append(Get(sig, "bh")).Append(";");
        sb.Append(" b=");
        return sb.ToString();
    }

    private sealed record Header(string Name, string Value, int Order);

    private static List<Header> ParseHeaders(byte[] mime)
    {
        var text = Encoding.UTF8.GetString(mime);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var headers = new List<Header>();
        string? curName = null; var curValue = new StringBuilder();
        int order = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line)) break; // fin de headers
            if ((line[0] == ' ' || line[0] == '\t') && curName != null)
            {
                curValue.Append(" ").Append(line.Trim());
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon <= 0) break;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (curName != null)
            {
                headers.Add(new Header(curName, curValue.ToString(), order++));
            }
            curName = name; curValue.Clear(); curValue.Append(value);
        }
        if (curName != null) headers.Add(new Header(curName, curValue.ToString(), order++));
        return headers;
    }

    /// <summary>Construye el data a firmar: cabeceras seleccionadas + firma DKIM.</summary>
    private static byte[] BuildDataToSign(List<Header> headers, List<string> headersToSign, DkimCanonicalization canon, string sigHeader)
    {
        var sb = new StringBuilder();
        // 1) cabeceras del mensaje (en orden) que estén en la lista h=
        foreach (var name in headersToSign)
        {
            foreach (var h in headers.Where(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                sb.Append(CanonLine(h.Name, h.Value, canon)).Append("\r\n");
            }
        }
        // 2) la línea DKIM-Signature: v=1; a=...; ...; b= (sin `=value`)
        sb.Append(sigHeader).Append("\r\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string CanonLine(string name, string value, DkimCanonicalization canon)
    {
        if (canon == DkimCanonicalization.Simple)
        {
            return name + ":" + value.Trim();
        }
        // relaxed: lower name, colapsar espacios
        return name.ToLowerInvariant() + ":" + CollapseWs(value).Trim();
    }

    private static string CollapseWs(string s) =>
        new System.Text.RegularExpressions.Regex("[ \\t]+").Replace(s, " ").Trim();

    private static byte[] HashBody(byte[] mime, DkimCanonicalization canon)
    {
        var text = Encoding.UTF8.GetString(mime);
        string body;
        int headerEnd = text.IndexOf("\r\n\r\n");
        if (headerEnd >= 0) body = text[(headerEnd + 4)..];
        else body = "";

        if (canon == DkimCanonicalization.Relaxed)
        {
            // convierten a LF, colapsan líneas vacías al final, recortan trailing ws por línea
            body = body.Replace("\r\n", "\n");
            var lines = body.Split('\n').ToList();
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1])) lines.RemoveAt(lines.Count - 1);
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append(CollapseWs(l).TrimEnd()).Append("\n");
            return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(body));
    }
}