using System.Text;

namespace AtlasMail.Domain.Mime;

/// <summary>Construye un mensaje MIME mínimo pero válido (usado por compose/envío).</summary>
public static class MimeBuilder
{
    public sealed class ComposeRequest
    {
        public string From { get; set; } = string.Empty;
        public IEnumerable<string> To { get; set; } = Array.Empty<string>();
        public IEnumerable<string> Cc { get; set; } = Array.Empty<string>();
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public IEnumerable<AttachmentPart> Attachments { get; set; } = Array.Empty<AttachmentPart>();
        public string? InReplyTo { get; set; }
    }

    /// <summary>Genera el mensaje MIME crudo (bytes) a partir de la solicitud.</summary>
    public static byte[] Build(ComposeRequest request)
    {
        string messageId = $"<{Guid.NewGuid():N}@atlasmail>";
        var sb = new StringBuilder();
        sb.AppendLine("MIME-Version: 1.0");
        sb.Append("From: ").AppendLine(request.From);
        if (request.To.Any()) sb.Append("To: ").AppendLine(string.Join(", ", request.To));
        if (request.Cc.Any()) sb.Append("Cc: ").AppendLine(string.Join(", ", request.Cc));
        sb.Append("Subject: ").AppendLine(EncodeSubject(request.Subject));
        sb.AppendLine("Date: " + DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss '+0000'"));
        sb.Append("Message-ID: ").AppendLine(messageId);
        if (!string.IsNullOrEmpty(request.InReplyTo)) sb.Append("In-Reply-To: ").AppendLine(request.InReplyTo);

        bool hasAttachments = request.Attachments.Any();
        string boundary = $"=_atlas_{Guid.NewGuid():N}";

        if (hasAttachments)
        {
            sb.Append("Content-Type: multipart/mixed; boundary=\"").Append(boundary).AppendLine("\"");
            sb.AppendLine();
            // parte texto
            sb.Append("--").AppendLine(boundary);
            sb.AppendLine("Content-Type: text/plain; charset=utf-8");
            sb.AppendLine("Content-Transfer-Encoding: 8bit");
            sb.AppendLine();
            sb.AppendLine(request.Body);
            sb.AppendLine();
            foreach (var att in request.Attachments)
            {
                sb.Append("--").AppendLine(boundary);
                sb.Append("Content-Type: ").Append(att.ContentType).AppendLine();
                sb.Append("Content-Disposition: attachment; filename=\"").Append(EscapeHeader(att.FileName)).AppendLine("\"");
                sb.AppendLine("Content-Transfer-Encoding: base64");
                sb.AppendLine();
                sb.AppendLine(Convert.ToBase64String(att.Data));
                sb.AppendLine();
            }
            sb.Append("--").Append(boundary).AppendLine("--");
        }
        else
        {
            sb.AppendLine("Content-Type: text/plain; charset=utf-8");
            sb.AppendLine("Content-Transfer-Encoding: 8bit");
            sb.AppendLine();
            sb.AppendLine(request.Body);
        }

        // Normalizar a LF + techo. Convertir a UTF-8 bytes.
        var final = sb.ToString().Replace("\r\n", "\n");
        return Encoding.UTF8.GetBytes(final);
    }

    private static string EncodeSubject(string s)
    {
        // Codificar como UTF-8 encoded-word si hay no-ASCII o caracteres especiales.
        bool needsEncoding = s.Any(c => c > 127 || c == '=' || c == '?');
        return needsEncoding
            ? "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?="
            : s;
    }

    private static string EscapeHeader(string s) => s.Replace("\"", "'").Replace("\r", "").Replace("\n", "");
}