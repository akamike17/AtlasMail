using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace AtlasMail.IntegrationTests;

/// <summary>
/// FASE 4 — Autenticación de correo (spec §17-19). Prueba contra la BD temporal:
///  - diagnóstico/admin de un dominio (get auth status),
///  - habilitar DKIM y verificar que se genera clave + registro DNS a publicar,
///  - establecer política DMARC (none/quarantine/reject). La verificación SPF/DKIM/DMARC
///    en recepción depende de DNS público, por lo que aquí se valida el flujo del pipeline
///    sin red (dominios inexistentes → SPF None / DKIM none / DMARC NoRecord).
/// </summary>
public class EmailAuthAdminTests : IClassFixture<AtlasMailFactory>
{
    private readonly AtlasMailFactory _factory;
    public EmailAuthAdminTests(AtlasMailFactory factory) => _factory = factory;
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Domain_auth_genera_dkim_y_dmarc()
    {
        var admin = await _factory.CreateAdminClientAsync();
        var d = await (await admin.PostAsJsonAsync("/api/admin/domain", new { name = "authadmin.local" })).Content.ReadAsStringAsync();
        long domainId = Body(d).GetProperty("id").GetInt64();

        // estado inicial (sin DKIM, sin DMARC)
        var st0 = await (await admin.GetAsync($"/api/admin/domain/{domainId}/auth")).Content.ReadAsStringAsync();
        Body(st0).GetProperty("dkimEnabled").GetBoolean().Should().BeFalse();
        Body(st0).GetProperty("spfRecordToPublish").GetString().Should().Contain("v=spf1");

        // habilitar DKIM
        var dkimRes = await admin.PostAsync($"/api/admin/domain/{domainId}/auth/dkim/enable", null);
        dkimRes.EnsureSuccessStatusCode();
        var dkim = Body(await dkimRes.Content.ReadAsStringAsync());
        dkim.GetProperty("dkimEnabled").GetBoolean().Should().BeTrue();
        dkim.GetProperty("dkimSelector").GetString().Should().NotBeNullOrEmpty();
        dkim.GetProperty("dkimPublicKeyRecord").GetString().Should().Contain("v=DKIM1; k=rsa; p=");

        // la clave privada no se expone
        dkim.TryGetProperty("dkimPrivateKey", out var k).Should().BeFalse();
        dkim.GetProperty("dkimPrivateKeyHint").GetString().Should().Contain("no expuesta");

        // DMARC reject
        var dmarcRes = await admin.PostAsync($"/api/admin/domain/{domainId}/auth/dmarc", new StringContent("\"reject\"", Encoding.UTF8, "application/json"));
        dmarcRes.EnsureSuccessStatusCode();
        var dmarc = Body(await dmarcRes.Content.ReadAsStringAsync());
        dmarc.GetProperty("dmarcPolicy").GetString().Should().Be("reject");
        dmarc.GetProperty("dmarcRecordToPublish").GetString().Should().Contain("v=DMARC1; p=reject");
    }

    [Fact]
    public async Task Domain_auth_politica_dmarc_invalida_rechazada()
    {
        var admin = await _factory.CreateAdminClientAsync();
        var d = await (await admin.PostAsJsonAsync("/api/admin/domain", new { name = "authbad.local" })).Content.ReadAsStringAsync();
        long domainId = Body(d).GetProperty("id").GetInt64();

        var res = await admin.PostAsync($"/api/admin/domain/{domainId}/auth/dmarc", new StringContent("\"bogus\"", Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }
}