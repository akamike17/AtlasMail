using System.Text;
using System.Text.RegularExpressions;

namespace AtlasMail.Domain.Mime;

/// <summary>Archivo adjunto extraído y parseado.</summary>
public sealed class AttachmentPart
{
    public string FileName { get; set; } = "attachment";
    public string ContentType { get; set; } = "application/octet-stream";
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public string? ContentId { get; set; }
}

/// <summary>Resultado del parseo MIME de un mensaje.</summary>
public sealed class ParsedMessage
{
    public string MessageIdHeader { get; set; } = string.Empty;
    public string SenderAddress { get; set; } = string.Empty;
    public string? SenderName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public DateTime? DateUtc { get; set; }
    public string InReplyTo { get; set; } = string.Empty;

    public List<(string Address, string? Name, string Kind)> Recipients { get; } = new(); // Kind: To/Cc/Bcc

    public string PlainBody { get; set; } = string.Empty;
    public string HtmlBody { get; set; } = string.Empty;
    public bool HasHtml { get; set; }
    public List<AttachmentPart> Attachments { get; } = new();

    /// <summary>Preview de texto plano (primeras ~160 chars sin saltos).</summary>
    public string BuildPreview(int maxChars = 160)
    {
        var src = !string.IsNullOrWhiteSpace(PlainBody) ? PlainBody : HtmlBody;
        if (string.IsNullOrEmpty(src)) return string.Empty;
        var clean = Regex.Replace(src, @"\s+", " ").Trim();
        return clean.Length <= maxChars ? clean : clean[..maxChars] + "…";
    }
}

/// <summary>
/// Parser MIME RFC 5322 + 2045-2047 (incremental). Preserva el mensaje original;
/// sólo estructura/leer. Nunca ejecuta contenido. Arroja MimeParseException si no se puede.
/// </summary>
public static class MimeParser
{
    public static ParsedMessage Parse(byte[] raw)
    {
        var text = DecodeBytesAsText(raw);
        int headerEnd = FindHeaderEnd(text);
        string headerBlock = headerEnd > 0 ? text[..headerEnd] : text;
        // cuerpo = todo tras la línea en blanco (CRLF CRLF o LF LF)
        string body = string.Empty;
        if (headerEnd >= 0 && headerEnd < text.Length)
        {
            int skip = headerEnd;
            // consumir los delimitadores de fin de cabeceras (4 si CRLFCRLF, 2 si LFLF)
            if (skip + 1 < text.Length && text[skip] == '\r' && text[skip + 1] == '\n') skip = Math.Min(skip + 4, text.Length);
            else if (skip < text.Length && text[skip] == '\n') skip = Math.Min(skip + 2, text.Length);
            body = text[skip..];
        }

        var headers = ParseHeaders(headerBlock);
        var result = new ParsedMessage
        {
            MessageIdHeader = TrimHeader(headers, "Message-ID") ?? string.Empty,
            Subject = DecodeEncodedWord(TrimHeader(headers, "Subject") ?? string.Empty),
            SenderAddress = ParseFrom(TrimHeader(headers, "From")).Address,
            SenderName = ParseFrom(TrimHeader(headers, "From")).Name,
            DateUtc = TryParseDate(TrimHeader(headers, "Date")),
            InReplyTo = TrimHeader(headers, "In-Reply-To") ?? string.Empty,
        };

        result.Recipients.AddRange(ParseAddressList(headers, "To", "To"));
        result.Recipients.AddRange(ParseAddressList(headers, "Cc", "Cc"));
        result.Recipients.AddRange(ParseAddressList(headers, "Bcc", "Bcc"));

        // Determinar tipo de cuerpo
        var contentType = headers.FirstOrDefault(h => h.Item1.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Item2;
        string contentTypeLower = (contentType ?? "text/plain").ToLowerInvariant();
        string topCte = (headers.FirstOrDefault(h => h.Item1.Equals("Content-Transfer-Encoding", StringComparison.OrdinalIgnoreCase)).Item2 ?? "7bit").ToLowerInvariant();

        // Si es multipart/alternative, escoger el html; de lo contrario parsear según content-type
        if (contentTypeLower.StartsWith("multipart/"))
        {
            string boundary = GetBoundary(contentType);
            if (!string.IsNullOrEmpty(boundary))
            {
                var parts = SplitMultipart(body, boundary);
                foreach (var part in parts) ProcessPart(part, result);
            }
            else
            {
                result.PlainBody = body;
            }
        }
        else if (contentTypeLower.Contains("html"))
        {
            result.HtmlBody = Encoding.UTF8.GetString(DecodeTransfer(body, topCte));
            result.HasHtml = true;
        }
        else
        {
            result.PlainBody = Encoding.UTF8.GetString(DecodeTransfer(body, topCte));
        }

        return result;
    }

    private static void ProcessPart(string partText, ParsedMessage result)
    {
        int headerEnd = FindHeaderEnd(partText);
        string hdr = headerEnd > 0 ? partText[..headerEnd] : partText;
        string body = string.Empty;
        if (headerEnd >= 0 && headerEnd < partText.Length)
        {
            int skip = headerEnd;
            if (skip + 1 < partText.Length && partText[skip] == '\r' && partText[skip + 1] == '\n') skip = Math.Min(skip + 4, partText.Length);
            else if (skip < partText.Length && partText[skip] == '\n') skip = Math.Min(skip + 2, partText.Length);
            body = partText[skip..];
        }
        var headers = ParseHeaders(hdr);

        string ct = (headers.FirstOrDefault(h => h.Item1.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Item2 ?? "text/plain").ToLowerInvariant();
        string disposition = (headers.FirstOrDefault(h => h.Item1.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase)).Item2 ?? string.Empty).ToLowerInvariant();
        string cte = (headers.FirstOrDefault(h => h.Item1.Equals("Content-Transfer-Encoding", StringComparison.OrdinalIgnoreCase)).Item2 ?? "7bit").ToLowerInvariant();
        byte[] decoded = DecodeTransfer(body, cte);

        if (ct.StartsWith("multipart/"))
        {
            string boundary = GetBoundary(headers.FirstOrDefault(h => h.Item1.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Item2);
            if (!string.IsNullOrEmpty(boundary))
                foreach (var sub in SplitMultipart(body, boundary)) ProcessPart(sub, result);
            return;
        }

        bool isAttachment = disposition.Contains("attachment") || (!disposition.Contains("inline") && !ct.Contains("text/") && !ct.Contains("message/"));

        if (isAttachment)
        {
            string filename = ExtractFilename(disposition) ?? ExtractFilenameHeaders(headers) ?? "attachment.bin";
            result.Attachments.Add(new AttachmentPart
            {
                FileName = SanitizeFilename(filename),
                ContentType = ct,
                Data = decoded,
                ContentId = StripBrackets(TrimHeader(headers, "Content-ID"))
            });
        }
        else if (ct.Contains("html") && !result.HasHtml)
        {
            result.HtmlBody = Encoding.UTF8.GetString(decoded);
            result.HasHtml = true;
        }
        else if (ct.Contains("text/plain") && string.IsNullOrEmpty(result.PlainBody))
        {
            result.PlainBody = Encoding.UTF8.GetString(decoded);
        }
        else if (ct.Contains("text/"))
        {
            if (string.IsNullOrEmpty(result.PlainBody)) result.PlainBody = Encoding.UTF8.GetString(decoded);
        }
    }

    private static (string Address, string? Name) ParseFrom(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return (string.Empty, null);
        var m = Regex.Match(headerValue, @"""([^""]*)""\s*<([^>]+)>");
        if (m.Success) return (m.Groups[2].Value.Trim(), m.Groups[1].Value.Trim());
        m = Regex.Match(headerValue, @"<([^>]+)>");
        if (m.Success) return (m.Groups[1].Value.Trim(), null);
        return (headerValue.Trim(), null);
    }

    private static IEnumerable<(string Address, string? Name, string Kind)> ParseAddressList(
        List<(string, string)> headers, string headerName, string kind)
    {
        foreach (var (name, value) in headers)
        {
            if (!name.Equals(headerName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var addr in SplitAddresses(value))
            {
                var m = Regex.Match(addr, @"""([^""]*)""\s*<([^>]+)>");
                if (m.Success)
                    yield return (m.Groups[2].Value.Trim(), m.Groups[1].Value.Trim(), kind);
                else
                {
                    m = Regex.Match(addr, @"<([^>]+)>");
                    var address = m.Success ? m.Groups[1].Value.Trim() : addr.Trim().Trim(',').Trim();
                    yield return (address, null, kind);
                }
            }
        }
    }

    private static IEnumerable<string> SplitAddresses(string value)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"') depth++;
            else if (c == ',') { if (depth % 2 == 0) { yield return value[start..i]; start = i + 1; } }
        }
        yield return value[start..];
    }

    private static string[] SplitMultipart(string body, string boundary)
    {
        var parts = new List<string>();
        string open = "--" + boundary;
        string close = "--" + boundary + "--";
        int idx = 0;
        while (true)
        {
            int start = body.IndexOf(open, idx, StringComparison.Ordinal);
            if (start < 0) break;
            int contentStart = start + open.Length;
            // terminator (--boundary--): no hay contenido tras él
            if (contentStart + 2 <= body.Length && body.Substring(contentStart, 2) == "--") break;
            // avanzar idx para encontrar el siguiente borde
            int next = body.IndexOf(open, contentStart, StringComparison.Ordinal);
            if (next < 0) break;
            int contentEnd = next;
            string part = body[contentStart..contentEnd];
            // quitar línea en blanco inicial que sigue al borde
            part = part.TrimStart('\r', '\n');
            parts.Add(part);
            idx = next;
        }
        return parts.ToArray();
    }

    private static string GetBoundary(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return string.Empty;
        var m = Regex.Match(contentType, @"boundary\s*=\s*(?:\""([^\""]+)\""|([^;\s]+))");
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim() : string.Empty;
    }

    private static byte[] DecodeTransfer(string body, string cte)
    {
        var clean = body.Replace("\r\n", "\n");
        return cte switch
        {
            "base64" => DecodeBase64(clean),
            "quoted-printable" => DecodeQuotedPrintable(clean),
            _ => Encoding.UTF8.GetBytes(clean)
        };
    }

    private static byte[] DecodeBase64(string data)
    {
        try { return Convert.FromBase64String(data.Replace("\n", "").Replace("\r", "")); }
        catch { return Array.Empty<byte>(); }
    }

    private static byte[] DecodeQuotedPrintable(string data)
    {
        var outBytes = new List<byte>();
        for (int i = 0; i < data.Length; i++)
        {
            char c = data[i];
            if (c == '=' && i + 2 < data.Length)
            {
                string hex = data.Substring(i + 1, 2);
                if (byte.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var b))
                {
                    outBytes.Add(b); i += 2; continue;
                }
            }
            if (c == '\r') continue; // normalizar CRLF -> LF
            outBytes.Add(c <= 127 ? (byte)c : (byte)c);
        }
        return outBytes.ToArray();
    }

    private static DateTime? TryParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[]
        {
            "ddd, dd MMM yyyy HH:mm:ss zzz",
            "ddd, dd MMM yyyy HH:mm:ss zz",
            "dd MMM yyyy HH:mm:ss zzz",
            "dd MMM yyyy HH:mm:ss",
            "yyyy-MM-dd HH:mm:ss"
        };
        if (DateTimeOffset.TryParseExact(value.Trim(), formats,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out var dto))
            return dto.UtcDateTime;
        if (DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal |
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        return null;
    }

    private static string DecodeBytesAsText(byte[] raw)
    {
        // Intentar UTF-8, luego Latin-1 como fallback seguro.
        int boms = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
        var strict = new UTF8Encoding(false, true);
        try { return strict.GetString(raw, boms, raw.Length - boms); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(raw); }
    }

    private static int FindHeaderEnd(string text)
    {
        // fin de cabeceras = línea en blanco (CRLF CRLF o LF LF)
        int idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (idx >= 0) return idx;
        idx = text.IndexOf("\n\n", StringComparison.Ordinal);
        return idx;
    }

    private static List<(string, string)> ParseHeaders(string block)
    {
        var list = new List<(string, string)>();
        string? currentName = null;
        var currentValue = new StringBuilder();
        bool first = true;
        foreach (var line in block.Split('\n'))
        {
            string l = line.TrimEnd('\r');
            if (string.IsNullOrEmpty(l)) continue;
            if ((l[0] == ' ' || l[0] == '\t') && currentName != null) { currentValue.Append(' ').Append(l.Trim()); continue; }
            int colon = l.IndexOf(':');
            if (colon <= 0) continue;
            if (!first && currentName != null) list.Add((currentName!, currentValue.ToString().Trim()));
            currentName = l[..colon].Trim();
            currentValue.Clear().Append(l[(colon + 1)..].Trim());
            first = false;
        }
        if (!first && currentName != null) list.Add((currentName!, currentValue.ToString().Trim()));
        return list;
    }

    private static string? TrimHeader(List<(string, string)> headers, string name)
    {
        var found = headers.FirstOrDefault(h => h.Item1.Equals(name, StringComparison.OrdinalIgnoreCase));
        return found.Item1 == null ? null : found.Item2;
    }

    private static string DecodeEncodedWord(string value)
    {
        return Regex.Replace(value, @"=\?([^?]+)\?([bBqQ])\?([^?]*)\?=", mm =>
        {
            string charset = mm.Groups[1].Value;
            string encoding = mm.Groups[2].Value.ToUpperInvariant();
            string payload = mm.Groups[3].Value;
            try
            {
                byte[] bytes = encoding == "B" ? Convert.FromBase64String(payload) : DecodeQuotedPrintable(payload);
                return Encoding.GetEncoding(charset).GetString(bytes);
            }
            catch { return mm.Value; }
        });
    }

    private static string? ExtractFilename(string? disposition)
    {
        if (string.IsNullOrEmpty(disposition)) return null;
        var m = Regex.Match(disposition, @"filename\s*=\s*(?:\""([^\""]+)\""|([^;\s]+))", RegexOptions.IgnoreCase);
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
    }

    private static string? ExtractFilenameHeaders(List<(string, string)> headers)
    {
        var name = headers.FirstOrDefault(h => h.Item1.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Item2;
        if (!string.IsNullOrEmpty(name))
        {
            var m = Regex.Match(name, @"name\s*=\s*(?:\""([^\""]+)\""|([^;\s]+))", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }
        return null;
    }

    private static string SanitizeFilename(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (Array.IndexOf(invalid, c) >= 0) sb.Append('_');
            else sb.Append(c);
        }
        var clean = sb.ToString();
        if (string.IsNullOrWhiteSpace(clean)) clean = "attachment.bin";
        return clean.Length > 200 ? clean[..200] : clean;
    }

    private static string StripBrackets(string? s) =>
        string.IsNullOrEmpty(s) ? string.Empty : s.Trim('<', '>');
}

/// <summary>Excepción específica de parseo MIME.</summary>
public sealed class MimeParseException : Exception
{
    public MimeParseException(string message) : base(message) { }
}