using System.Text;
using AtlasMail.Domain.Mime;
using AtlasMail.Application.Services;

namespace AtlasMail.UnitTests;

public class MimeParserTests
{
    [Fact]
    public void Parsea_texto_plano()
    {
        var raw = "From: alice@atlas.local\r\nTo: bob@atlas.local\r\nSubject: Hola\r\n\r\nMensaje de prueba\r\n";
        var p = MimeParser.Parse(Encoding.UTF8.GetBytes(raw));
        Assert.Equal("Hola", p.Subject);
        Assert.Equal("alice@atlas.local", p.SenderAddress);
        Assert.Contains("Mensaje de prueba", p.PlainBody);
        Assert.Equal("Hola", p.Subject);
    }

    [Fact]
    public void Parsea_html_y_multipart()
    {
        var raw = "From: a@x.com\r\nTo: b@x.com\r\nSubject: HTML test\r\nContent-Type: multipart/alternative; boundary=\"xyz\"\r\n\r\n" +
                  "--xyz\r\nContent-Type: text/plain\r\n\r\nparte plano\r\n" +
                  "--xyz\r\nContent-Type: text/html\r\n\r\n<b>html</b>\r\n" +
                  "--xyz--\r\n";
        var p = MimeParser.Parse(Encoding.UTF8.GetBytes(raw));
        Assert.True(p.HasHtml);
        Assert.Equal("<b>html</b>\n", p.HtmlBody);
    }

    [Fact]
    public void Parsea_adjunto_base64_con_sha()
    {
        var payload = Encoding.UTF8.GetBytes("contenido del archivo");
        string b64 = Convert.ToBase64String(payload);
        var raw = "From: a@x.com\r\nTo: b@x.com\r\nSubject: con adjunto\r\nContent-Type: multipart/mixed; boundary=\"b2\"\r\n\r\n" +
                  "--b2\r\nContent-Type: text/plain\r\n\r\ncuerpo\r\n" +
                  "--b2\r\nContent-Type: application/pdf; name=\"doc.pdf\"\r\nContent-Disposition: attachment; filename=\"doc.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\n" + b64 + "\r\n" +
                  "--b2--\r\n";
        var p = MimeParser.Parse(Encoding.UTF8.GetBytes(raw));
        var att = Assert.Single(p.Attachments);
        Assert.Equal("doc.pdf", att.FileName);
        Assert.Equal(payload, att.Data);
    }

    [Fact]
    public void QuotedPrintable_se_decodifica()
    {
        string qp = "=C3=A9xito en =40"; // "éxito en @"
        var bytes = new byte[0];
        // verifica vía parse de un mensaje con CTE quoted-printable
        // "éxito en @" con charset utf-8
        var raw = "From: a@x.com\r\nTo: b@x.com\r\nSubject: qp\r\nContent-Transfer-Encoding: quoted-printable\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n" + qp + "\r\n";
        var p = MimeParser.Parse(Encoding.UTF8.GetBytes(raw));
        Assert.Equal("éxito en @\n", p.PlainBody);
    }

    [Fact]
    public void Subject_codificado_encoded_word()
    {
        string enc = "=?UTF-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Asunto ñ")) + "?=";
        var raw = $"From: a@x.com\r\nSubject: {enc}\r\n\r\ncuerpo\r\n";
        var p = MimeParser.Parse(Encoding.UTF8.GetBytes(raw));
        Assert.Equal("Asunto ñ", p.Subject);
    }

    [Fact]
    public void BuildPreview_limpia_espacios()
    {
        var p = new ParsedMessage { PlainBody = "Línea1\r\n   \r\nLínea2    más texto" };
        var preview = p.BuildPreview(200);
        Assert.DoesNotContain("\n", preview);
        Assert.Contains("Línea2", preview);
    }
}

public class MimeBuilderTests
{
    [Fact]
    public void Genera_mensaje_parseable()
    {
        var raw = MimeBuilder.Build(new MimeBuilder.ComposeRequest
        {
            From = "alice@atlas.local",
            To = new[] { "bob@atlas.local" },
            Subject = "Hola",
            Body = "Hola Bob"
        });
        var parsed = MimeParser.Parse(raw);
        Assert.Equal("Hola", parsed.Subject);
        Assert.Contains("Hola Bob", parsed.PlainBody);
        Assert.Contains("bob@atlas.local", parsed.Recipients.Select(r => r.Address));
    }

    [Fact]
    public void Genera_adjunto_en_base64()
    {
        var data = Encoding.UTF8.GetBytes("datos");
        var raw = MimeBuilder.Build(new MimeBuilder.ComposeRequest
        {
            From = "a@x.com", To = new[] { "b@x.com" }, Subject = "s", Body = "b",
            Attachments = new[] { new AttachmentPart { FileName = "a.txt", ContentType = "text/plain", Data = data } }
        });
        var parsed = MimeParser.Parse(raw);
        var att = Assert.Single(parsed.Attachments);
        Assert.Equal(data, att.Data);
    }
}

public class SpamScoringTests
{
    [Fact]
    public void Sin_autenticar_con_keyword_sube_score()
    {
        var score = SpamScoring.Score(hasFrom: true, emptyBody: false, size: 1000,
            text: "viagra lottery free money", authenticated: false);
        Assert.True(score >= 4.5);
    }

    [Fact]
    public void Autenticado_casi_nunca_sube()
    {
        var score = SpamScoring.Score(hasFrom: true, emptyBody: false, size: 1000,
            text: "viagra lotería", authenticated: true);
        Assert.True(score <= 0.5);
    }
}

public class RuleEngineTests
{
    [Fact]
    public async Task Keyword_de_regla_envia_a_spam()
    {
        var engine = new RuleEngine();
        var (folder, toSpam) = await engine.EvaluateAsync("news@x.com", "b@x.com", "Newsletter mensual");
        Assert.True(toSpam);
        Assert.Contains("Spam", folder ?? "");
    }

    [Fact]
    public async Task Sin_keyword_no_mueve()
    {
        var engine = new RuleEngine();
        var (folder, toSpam) = await engine.EvaluateAsync("a@x.com", "b@x.com", "Hola amigo");
        Assert.False(toSpam);
        Assert.Null(folder);
    }
}