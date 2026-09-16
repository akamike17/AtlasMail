using System.Text;
using AtlasMail.Application;
using AtlasMail.Security.EmailAuth;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Infrastructure.EmailAuth;

/// <summary>
/// Firma DKIM en salida (spec §18): antes de entregar un mensaje cuyo envelope-from
/// pertenece a un dominio local con DKIM habilitado y clave privada, inserta la cabecera
/// DKIM-Signature y devuelve el MIME firmado. Si el dominio no tiene DKIM, devuelve el
/// MIME sin alterar. Nunca registra la clave privada.
/// </summary>
public sealed class DkimOutboundSigner
{
    private readonly IApplicationDbContext _db;

    public DkimOutboundSigner(IApplicationDbContext db) => _db = db;

    /// <summary>Firma el MIME si el dominio del remitente tiene DKIM habilitado.</summary>
    public async Task<byte[]> SignIfEnabledAsync(string envelopeFrom, byte[] mime, CancellationToken ct = default)
    {
        var fromDomain = ExtractDomain(envelopeFrom);
        if (string.IsNullOrEmpty(fromDomain)) return mime;

        var domain = await _db.Domains.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Name == fromDomain && d.Enabled && d.DkimEnabled
                && !string.IsNullOrEmpty(d.DkimSelector) && !string.IsNullOrEmpty(d.DkimPrivateKey), ct);
        if (domain == null) return mime;

        string sig;
        try
        {
            sig = Dkim.Sign(mime, domain.Name, domain.DkimPrivateKey!, domain.DkimSelector!);
        }
        catch (Exception)
        {
            // clave corrupta/ilegible: no firmar, no romper la entrega
            return mime;
        }

        return InsertSignatureHeader(mime, sig);
    }

    /// <summary>Inserta el header DKIM-Signature al inicio (después del BOM/primera línea válida).</summary>
    private static byte[] InsertSignatureHeader(byte[] mime, string sig)
    {
        var text = Encoding.UTF8.GetString(mime);
        var crlf = text.Contains("\r\n");
        string nl = crlf ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        // La firma se inserta lo más arriba posible pero respetando que no haya líneas antes de headers
        string header = "DKIM-Signature: " + sig;
        var builder = new StringBuilder();
        builder.Append(header).Append(nl);
        builder.Append(text);
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static string? ExtractDomain(string envelopeFrom)
    {
        var e = envelopeFrom.Trim();
        if (e.StartsWith("<")) e = e.Trim('<', '>');
        if (e.StartsWith("MAIL FROM:")) e = e["MAIL FROM:".Length..].Trim().Trim('<', '>');
        int at = e.IndexOf('@');
        return at >= 0 ? e[(at + 1)..] : null;
    }
}