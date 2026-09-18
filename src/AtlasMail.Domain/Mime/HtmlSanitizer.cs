using System.Text;

namespace AtlasMail.Domain.Mime;

/// <summary>
/// Saneamiento whitelist del HTML de correos entrantes (spec §sicurezza: sanitización de
/// HTML antes de mostrarlo en el webmail). Elimina scripts, manejadores de evento (on*),
/// iframes/object/embed/forms, atributos style peligrosos y URLs javascript:/vbscript:/data:
/// para el HTML arbitrario y hostil de un tercero. El resultado NO puede ejecutar script.
/// </summary>
public static class HtmlSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "p", "div", "span", "br", "hr", "b", "strong", "i", "em", "u", "s", "strike",
        "small", "sub", "sup", "abbr", "cite", "q", "mark", "code", "pre", "tt",
        "ul", "ol", "li", "dl", "dt", "dd", "blockquote", "center",
        "h1", "h2", "h3", "h4", "h5", "h6", "table", "thead", "tbody", "tfoot", "tr", "td", "th", "caption", "colgroup", "col",
        "img", "figure", "figcaption", "details", "summary", "time", "kbd", "samp", "var", "address", "nav"
    };

    private static readonly HashSet<string> DroppedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "frame", "frameset", "object", "embed", "applet",
        "meta", "link", "base", "basefont", "form", "input", "button", "select", "textarea",
        "option", "optgroup", "label", "fieldset", "legend", "svg", "math", "canvas",
        "audio", "video", "source", "track", "noscript", "template", "title", "body", "head", "html", "marquee"
    };

    private static readonly HashSet<string> AllowedAttrs = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "title", "alt", "src", "width", "height", "align", "valign", "colspan", "rowspan",
        "cellpadding", "cellspacing", "border", "cite", "rel", "target", "lang", "dir", "datetime", "start", "type", "id"
    };

    /// <summary>Retorna el HTML saneado (o cadena vacía si la entrada es nula).</summary>
    public static string Sanitize(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var sb = new StringBuilder(html.Length + 32);
        int i = 0, n = html.Length;

        while (i < n)
        {
            char c = html[i];
            // Comentario <!-- ... -->
            if (c == '<' && n - i >= 4 && html[i + 1] == '!' && html[i + 2] == '-' && html[i + 3] == '-')
            {
                int end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }

            if (c == '<')
            {
                int close = html.IndexOf('>', i + 1);
                if (close < 0)
                {
                    // Etiqueta sin cerrar: tratar como texto literal (no inyectar).
                    AppendEscaped(sb, html, i, n);
                    break;
                }
                string raw = html[(i + 1)..close].Trim();
                i = close + 1;
                string tagName;
                bool isClosing = raw.StartsWith('/');
                if (isClosing) tagName = raw[1..].Trim();
                else
                {
                    int sp = raw.IndexOfAny(new[] { ' ', '\t', '\n', '\r', '/' });
                    tagName = sp < 0 ? raw : raw[..sp];
                }

                string lower = tagName.TrimEnd('/').ToLowerInvariant();
                if (lower.Length == 0)
                {
                    // "<>" malformado → texto
                    sb.Append("&lt;");
                    continue;
                }

                if (DroppedTags.Contains(lower))
                {
                    // Etiqueta prohibida: no la emitimos. Su contenido sigue procesado como texto
                    // por el bucle (los scripts NO se ejecutan porque jamás generamos la etiqueta).
                    continue;
                }

                if (isClosing)
                {
                    if (AllowedTags.Contains(lower)) sb.Append("</").Append(lower).Append('>');
                    continue;
                }

                if (!AllowedTags.Contains(lower))
                {
                    // Etiqueta no permitida → tratarla como texto (o saltarla). Saltamos la etiqueta.
                    continue;
                }

                sb.Append('<').Append(lower);
                sb.Append(SanitizeAttributes(raw));
                sb.Append('>');
                continue;
            }

            // Texto normal: buscar el siguiente '<'
            int next = html.IndexOf('<', i);
            int seg = next < 0 ? n : next;
            AppendEscaped(sb, html, i, seg);
            i = next < 0 ? n : next;
        }

        return sb.ToString();
    }

    /// <summary>Escribe textos escapando &amp;&lt;&gt; para que ningún resto de etiqueta se interprete.</summary>
    private static void AppendEscaped(StringBuilder sb, string html, int from, int to)
    {
        for (int k = from; k < to; k++)
        {
            char ch = html[k];
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(ch); break;
            }
        }
    }

    /// <summary>Reconstruye la lista de atributos permitidos del tag.</summary>
    private static string SanitizeAttributes(string rawTag)
    {
        var result = new StringBuilder();
        // Tokenizar atributos con regex simple: name="value" | name='value' | name=value | name
        int pos = 0, len = rawTag.Length;
        bool first = true;
        while (true)
        {
            // saltar espacios
            while (pos < len && !char.IsWhiteSpace(rawTag[pos])) pos++;
            while (pos < len && char.IsWhiteSpace(rawTag[pos])) pos++;
            if (pos >= len) break;

            // leer nombre de atributo
            int nameStart = pos;
            while (pos < len && (char.IsLetterOrDigit(rawTag[pos]) || rawTag[pos] == '-' || rawTag[pos] == '_' || rawTag[pos] == ':')) pos++;
            if (pos == nameStart) { pos++; continue; }
            string name = rawTag[nameStart..pos];

            // valor
            while (pos < len && char.IsWhiteSpace(rawTag[pos])) pos++;
            string? value = null;
            if (pos < len && rawTag[pos] == '=')
            {
                pos++;
                while (pos < len && char.IsWhiteSpace(rawTag[pos])) pos++;
                if (pos < len && (rawTag[pos] == '"' || rawTag[pos] == '\''))
                {
                    char q = rawTag[pos];
                    int vStart = ++pos;
                    while (pos < len && rawTag[pos] != q) pos++;
                    value = rawTag[vStart..pos];
                    if (pos < len) pos++; // consumir comilla cierre
                }
                else
                {
                    int vStart = pos;
                    while (pos < len && !char.IsWhiteSpace(rawTag[pos])) pos++;
                    value = rawTag[vStart..pos];
                }
            }

            if (!IsAllowedAttribute(name, value)) continue;

            string finalValue = value ?? string.Empty;
            if (string.IsNullOrEmpty(finalValue))
            {
                if (AllowedAttrs.Contains(name))
                {
                    if (!first) result.Append(' ');
                    result.Append(name);
                    first = false;
                }
            }
            else
            {
                if (!first) result.Append(' ');
                result.Append(name).Append("=\"").Append(EncodeAttrValue(finalValue)).Append('"');
                first = false;
            }
        }
        return result.ToString();
    }

    private static bool IsAllowedAttribute(string name, string? value)
    {
        // Nada de manejadores de evento.
        if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("style", StringComparison.OrdinalIgnoreCase)) return false; // CSS puede cargar URLs / exfiltrar
        if (!AllowedAttrs.Contains(name)) return false;

        // URLs peligrosas solo en href/src.
        if ((name.Equals("href", StringComparison.OrdinalIgnoreCase) || name.Equals("src", StringComparison.OrdinalIgnoreCase)))
        {
            if (value == null) return true; // atributo vacío (src sin valor) — permitido como vacío
            return IsSafeUrl(value.Trim());
        }
        return true;
    }

    private static bool IsSafeUrl(string raw)
    {
        // false si el esquema es javascript:, vbscript:, data: (salvo data:image para img es debatible; aquí lo bloqueamos)
        int colon = raw.IndexOf(':');
        if (colon >= 0)
        {
            string scheme = raw[..colon].Trim().ToLowerInvariant();
            if (scheme.Length > 0 && !IsAsciiAlpha(scheme[0])) return false;
            switch (scheme)
            {
                case "javascript":
                case "vbscript":
                case "data":
                case "file":
                case "blob":
                    return false;
                case "":
                    break;
            }
            // permitir http/https/mailto/tel/relative (sin esquema) y anclas
            string s = scheme.ToLowerInvariant();
            if (s is "http" or "https" or "mailto" or "tel") return true;
            // otros esquemas desconocidos → bloquear
            return false;
        }
        return true; // relativo o ancla
    }

    private static bool IsAsciiAlpha(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static string EncodeAttrValue(string v)
    {
        var sb = new StringBuilder(v.Length + 8);
        foreach (char ch in v)
        {
            switch (ch)
            {
                case '"': sb.Append("&quot;"); break;
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\n': sb.Append("\n"); break;
                case '\r': sb.Append("\r"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }
}