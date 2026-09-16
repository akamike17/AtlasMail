using AtlasMail.Application.Abstractions;
using AtlasMail.Security.EmailAuth;

namespace AtlasMail.UnitTests;

/// <summary>Resolver DNS falso con registros TXT/A configurables para tests de auth de correo.</summary>
public sealed class FakeDnsResolver : IDnsRecordResolver
{
    private readonly Dictionary<string, string[]> _txt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string[]> _a = new(StringComparer.OrdinalIgnoreCase);
    public FakeDnsResolver WithTxt(string name, params string[] records) { _txt[name] = records; return this; }
    public FakeDnsResolver WithA(string name, params string[] ips) { _a[name] = ips; return this; }
    public Task<IReadOnlyList<string>> GetTxtAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_txt.TryGetValue(name, out var v) ? v : Array.Empty<string>());
    public Task<IReadOnlyList<string>> GetAddressesAsync(string hostname, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(_a.TryGetValue(hostname, out var v) ? v : Array.Empty<string>());
}

public class SpfEvaluatorTests
{
    [Fact]
    public async Task Pass_cuando_ip4_coincide()
    {
        var dns = new FakeDnsResolver().WithTxt("atlas.local", "v=spf1 ip4:203.0.113.5 -all");
        var ev = new SpfEvaluator(dns);
        var r = await ev.EvaluateAsync("203.0.113.5", null, "user@atlas.local");
        Assert.Equal(SpfResult.Pass, r.Result);
    }

    [Fact]
    public async Task Fail_cuando_ip_no_autorizada()
    {
        var dns = new FakeDnsResolver().WithTxt("atlas.local", "v=spf1 ip4:203.0.113.5 -all");
        var ev = new SpfEvaluator(dns);
        var r = await ev.EvaluateAsync("198.51.100.7", null, "user@atlas.local");
        Assert.Equal(SpfResult.Fail, r.Result);
        Assert.Equal("-all", r.Rule);
    }

    [Fact]
    public async Task SoftFail_con_tilde_all()
    {
        var dns = new FakeDnsResolver().WithTxt("ejemplo.mx", "v=spf1 ip4:203.0.113.5 ~all");
        var ev = new SpfEvaluator(dns);
        var r = await ev.EvaluateAsync("198.51.100.7", null, "u@ejemplo.mx");
        Assert.Equal(SpfResult.SoftFail, r.Result);
    }

    [Fact]
    public async Task Neutral_con_pregunta_all()
    {
        var dns = new FakeDnsResolver().WithTxt("neutro.net", "v=spf1 ip4:203.0.113.5 ?all");
        var ev = new SpfEvaluator(dns);
        var r = await ev.EvaluateAsync("198.51.100.7", null, "u@neutro.net");
        Assert.Equal(SpfResult.Neutral, r.Result);
    }

    [Fact]
    public async Task None_cuando_no_hay_registro()
    {
        var dns = new FakeDnsResolver();
        var ev = new SpfEvaluator(dns);
        var r = await ev.EvaluateAsync("203.0.113.5", null, "u@nodominio.xyz");
        Assert.Equal(SpfResult.None, r.Result);
    }

    [Fact]
    public async Task Pass_con_mecanismo_a_resolviendo_dominio()
    {
        var dns = new FakeDnsResolver()
            .WithTxt("servidor.net", "v=spf1 a -all")
            .WithA("servidor.net", "203.0.113.50");
        var ev = new SpfEvaluator(dns);
        var r = await ev.EvaluateAsync("203.0.113.50", null, "u@servidor.net");
        Assert.Equal(SpfResult.Pass, r.Result);
    }

    [Fact]
    public async Task Include_pasa_when_subdominio_permite()
    {
        var dns = new FakeDnsResolver()
            .WithTxt("padre.com", "v=spf1 include:hijo.com -all")
            .WithTxt("hijo.com", "v=spf1 ip4:203.0.113.9 -all");
        var ev = new SpfEvaluator(dns);
        var r = await ev.EvaluateAsync("203.0.113.9", null, "u@padre.com");
        Assert.Equal(SpfResult.Pass, r.Result);
    }

    [Fact]
    public async Task Redirect_aplica_la_politica_del_objetivo()
    {
        var dns = new FakeDnsResolver()
            .WithTxt("raiz.com", "v=spf1 redirect=entradas.com")
            .WithTxt("entradas.com", "v=spf1 ip4:203.0.113.77 -all");
        var ev = new SpfEvaluator(dns);
        var r = await ev.EvaluateAsync("203.0.113.77", null, "u@raiz.com");
        Assert.Equal(SpfResult.Pass, r.Result);
    }

    [Fact]
    public async Task Macro_d_expande_dominio()
    {
        var dns = new FakeDnsResolver().WithTxt("macro.net", "v=spf1 ip4:%{i} -all");
        var ev = new SpfEvaluator(dns);
        // ip4:%{i} debe expandir a la IP del cliente; usamos ip que coincide
        var r = await ev.EvaluateAsync("10.1.2.3", null, "u@macro.net");
        // ip4 con IP exacta
        var ev2 = new SpfEvaluator(new FakeDnsResolver().WithTxt("macro.net", "v=spf1 ip4:%{i} -all"));
        var r2 = await ev2.EvaluateAsync("10.1.2.3", null, "u@macro.net");
        // %{i} expandido a 10.1.2.3 → match
        Assert.Equal(SpfResult.Pass, r2.Result);
    }

    [Fact]
    public async Task PermError_registro_sin_all()
    {
        var dns = new FakeDnsResolver().WithTxt("incompleto.net", "v=spf1 ip4:203.0.113.5");
        var ev = new SpfEvaluator(dns);
        var r = await ev.EvaluateAsync("198.51.100.7", null, "u@incompleto.net");
        Assert.Equal(SpfResult.PermError, r.Result);
    }
}

public class DkimTests
{
    [Fact]
    public void Roundtrip_firma_y_verificacion_pasa()
    {
        var (priv, pub) = Dkim.GenerateKeyPair(1024);
        var mime = System.Text.Encoding.UTF8.GetBytes(
            "From: alice@atlas.local\r\nTo: bob@otro.local\r\nSubject: Prueba DKIM\r\nDate: Wed, 01 Jan 2025 00:00:00 +0000\r\n\r\nHola mundo.\r\n");

        string sig = Dkim.Sign(mime, "atlas.local", priv, signedHeaders: new[] { "From", "To", "Subject", "Date" });
        var ver = Dkim.Verify(mime, sig, ExtractPublicKeyText(pub));

        Assert.Equal(DkimResult.Pass, ver.Result);
        Assert.Equal("atlas.local", ver.Domain);
    }

    [Fact]
    public void Roundtrip_con_relaxed_simple()
    {
        var (priv, pub) = Dkim.GenerateKeyPair(1024);
        var mime = System.Text.Encoding.UTF8.GetBytes(
            "From: carol@atlas.local\r\nTo: dave@otro.local\r\nSubject: test\r\nDate: Wed, 01 Jan 2025 00:00:00 +0000\r\n\r\nBody relaxed.\r\n");
        string sig = Dkim.Sign(mime, "atlas.local", priv,
            signedHeaders: new[] { "From", "To", "Subject", "Date" },
            headerC: DkimCanonicalization.Relaxed, bodyC: DkimCanonicalization.Simple);
        // firmar con canonicalización: el verifier debe usar misma canonicalización → el registro incluye c=relaxed/simple
        string pubPem = pub;
        var ver = Dkim.Verify(mime, sig, ExtractPublicKeyText(pubPem));
        Assert.Equal(DkimResult.Pass, ver.Result);
    }

    private static string ExtractPublicKeyText(string pubPem)
    {
        // registrar valor p= en una sola línea
        return pubPem.Replace("\n", "").Replace("-----BEGIN PUBLIC KEY-----", "")
            .Replace("-----END PUBLIC KEY-----", "").Trim();
    }

    [Fact]
    public void Firma_se_invalida_al_modificar_cuerpo()
    {
        var (priv, pub) = Dkim.GenerateKeyPair(1024);
        var mime = System.Text.Encoding.UTF8.GetBytes(
            "From: eve@atlas.local\r\nSubject: original\r\nDate: Wed, 01 Jan 2025 00:00:00 +0000\r\n\r\nContenido original.\r\n");
        string sig = Dkim.Sign(mime, "atlas.local", priv, signedHeaders: new[] { "From", "Subject", "Date" });

        // alterar el cuerpo
        var tampered = System.Text.Encoding.UTF8.GetBytes(
            "From: eve@atlas.local\r\nSubject: original\r\nDate: Wed, 01 Jan 2025 00:00:00 +0000\r\n\r\nContenido MODIFICADO.\r\n");
        var ver = Dkim.Verify(tampered, sig, ExtractPublicKeyText(pub));
        // El body hash ya no coincide
        Assert.NotEqual(DkimResult.Pass, ver.Result);
    }
}

public class DmarcEvaluatorTests
{
    [Fact]
    public void Alineacion_relaxed_strict()
    {
        Assert.True(DmarcEvaluator.Align("sub.atlas.local", "atlas.local", "r"));
        Assert.False(DmarcEvaluator.Align("sub.atlas.local", "atlas.local", "s"));
        Assert.True(DmarcEvaluator.Align("atlas.local", "atlas.local", "s"));
    }

    [Fact]
    public void Registrable_domain_detecta_tld_compuesto()
    {
        Assert.Equal("ejemplo.co.uk", DmarcEvaluator.RegistrableDomain("a.ejemplo.co.uk"));
        Assert.Equal("empresa.com.mx", DmarcEvaluator.RegistrableDomain("b.empresa.com.mx"));
    }

    [Fact]
    public async Task Pass_cuando_spf_alineado_y_politica_none()
    {
        var dns = new FakeDnsResolver().WithTxt("_dmarc.atlas.local", "v=DMARC1; p=none");
        var ev = new DmarcEvaluator(dns);
        var spf = new SpfEvaluation(SpfResult.Pass, "ip4", null);
        var dkim = new DkimVerification(DkimResult.NoSignature, null, null, "none");
        var r = await ev.EvaluateAsync("atlas.local", "atlas.local", spf, dkim, ct: CancellationToken.None);
        Assert.Equal(DmarcResult.Pass, r.Result);
        Assert.Equal("none", r.Policy);
        Assert.False(r.ShouldReject);
        Assert.False(r.ShouldQuarantine);
    }

    [Fact]
    public async Task Fail_con_politica_reject_debe_rechazar()
    {
        var dns = new FakeDnsResolver().WithTxt("_dmarc.atlas.local", "v=DMARC1; p=reject");
        var ev = new DmarcEvaluator(dns);
        // SPF no autorizado (fail) + sin DKIM → sin alineación → DMARC fail
        var spf = new SpfEvaluation(SpfResult.Fail, "-all", null);
        var dkim = new DkimVerification(DkimResult.NoSignature, null, null, "none");
        var r = await ev.EvaluateAsync("mail.other.net", "atlas.local", spf, dkim, ct: CancellationToken.None);
        Assert.Equal(DmarcResult.Fail, r.Result);
        Assert.Equal("reject", r.Policy);
        Assert.True(r.ShouldReject);
    }

    [Fact]
    public async Task None_cuando_no_hay_registro()
    {
        var dns = new FakeDnsResolver();
        var ev = new DmarcEvaluator(dns);
        var spf = new SpfEvaluation(SpfResult.Pass, "ip4", null);
        var dkim = new DkimVerification(DkimResult.NoSignature, null, null, "none");
        var r = await ev.EvaluateAsync("atlas.local", "atlas.local", spf, dkim, ct: CancellationToken.None);
        Assert.Equal(DmarcResult.NoRecord, r.Result);
    }
}

public class MailAuthServiceTests
{
    [Fact]
    public async Task Habilitar_dkim_genera_claves_y_registro_dns()
    {
        using var ctx = new ImapSeededContext("auth_svc_" + Guid.NewGuid().ToString("N"));
        var domain = new AtlasMail.Domain.Entities.Domain
        {
            Name = "firma.local",
            Enabled = true,
            MaxMailboxQuotaBytes = 1024 * 1024 * 1024,
            MaxMessageSizeBytes = 50 * 1024 * 1024,
            MaxRecipientsPerMessage = 100
        };
        ctx.Db.Domains.Add(domain);
        await ctx.Db.SaveChangesAsync();

        var svc = new AtlasMail.Infrastructure.EmailAuth.DomainMailAuthService(ctx.Db);
        var status = await svc.EnableDkimAsync(domain.Id);

        Assert.True(status.DkimEnabled);
        Assert.False(string.IsNullOrEmpty(status.DkimSelector));
        Assert.Contains("v=DKIM1; k=rsa; p=", status.DkimPublicKeyRecord ?? "");
        Assert.Equal("clave privada almacenada (no expuesta)", status.DkimPrivateKeyHint);
    }

    [Fact]
    public async Task Establecer_dmarc_politica_valida_y_rechaza_invalida()
    {
        using var ctx = new ImapSeededContext("auth_dmarc_" + Guid.NewGuid().ToString("N"));
        var domain = new AtlasMail.Domain.Entities.Domain
        {
            Name = "dmarc.local",
            Enabled = true,
            MaxMailboxQuotaBytes = 1024 * 1024 * 1024,
            MaxMessageSizeBytes = 50 * 1024 * 1024,
            MaxRecipientsPerMessage = 100
        };
        ctx.Db.Domains.Add(domain);
        await ctx.Db.SaveChangesAsync();

        var svc = new AtlasMail.Infrastructure.EmailAuth.DomainMailAuthService(ctx.Db);
        var ok = await svc.SetDmarcPolicyAsync(domain.Id, "reject");
        Assert.Equal("reject", ok.DmarcPolicy);
        Assert.Contains("v=DMARC1; p=reject", ok.DmarcRecordToPublish ?? "");

        await Assert.ThrowsAsync<ArgumentException>(() => svc.SetDmarcPolicyAsync(domain.Id, "bogus"));
    }

    [Fact]
    public async Task Dkim_outbound_firma_solo_si_dominio_habilitado()
    {
        using var ctx = new ImapSeededContext("auth_sign_" + Guid.NewGuid().ToString("N"));
        var adminAuth = new AtlasMail.Infrastructure.EmailAuth.DomainMailAuthService(ctx.Db);
        var domain = new AtlasMail.Domain.Entities.Domain
        {
            Name = "salida.local",
            Enabled = true,
            MaxMailboxQuotaBytes = 1024 * 1024 * 1024,
            MaxMessageSizeBytes = 50 * 1024 * 1024,
            MaxRecipientsPerMessage = 100
        };
        ctx.Db.Domains.Add(domain);
        await ctx.Db.SaveChangesAsync();
        await adminAuth.EnableDkimAsync(domain.Id);

        var signer = new AtlasMail.Infrastructure.EmailAuth.DkimOutboundSigner(ctx.Db);
        var mime = System.Text.Encoding.UTF8.GetBytes(
            "From: salida@salida.local\r\nTo: ext@remote.com\r\nSubject: firma\r\nDate: Wed, 01 Jan 2025 00:00:00 +0000\r\n\r\nhola.\r\n");

        var signed = await signer.SignIfEnabledAsync("salida@salida.local", mime);
        var signedText = System.Text.Encoding.UTF8.GetString(signed);
        Assert.Contains("DKIM-Signature: v=1; a=rsa-sha256", signedText);
        Assert.Contains("d=salida.local", signedText);

        // dominio sin DKIM → sin alterar
        var plain = await signer.SignIfEnabledAsync("otro@otro.local", mime);
        Assert.DoesNotContain("DKIM-Signature", System.Text.Encoding.UTF8.GetString(plain));
    }
}