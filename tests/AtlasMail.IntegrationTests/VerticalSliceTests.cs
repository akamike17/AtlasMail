using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace AtlasMail.IntegrationTests;

/// <summary>Pruebas de integración con MySQL temporal dedicado (spec §35).</summary>
public class VerticalSliceTests : IClassFixture<AtlasMailFactory>
{
    private readonly AtlasMailFactory _factory;

    public VerticalSliceTests(AtlasMailFactory factory) => _factory = factory;

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Deny_by_default_sin_sesion()
    {
        var client = _factory.CreateClient();
        (await client.GetAsync("/api/mail/folders")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/admin/dashboard")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_login_y_dashboard()
    {
        var client = await _factory.CreateAdminClientAsync();
        var me = await (await client.GetAsync("/api/auth/me")).Content.ReadAsStringAsync();
        Body(me).GetProperty("role").GetString().Should().Be("SuperAdmin");

        var dash = await (await client.GetAsync("/api/admin/dashboard")).Content.ReadAsStringAsync();
        Body(dash).GetProperty("domainCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Crear_dominio_buzones_alias_y_usuarios()
    {
        var admin = await _factory.CreateAdminClientAsync();
        var domain = await (await admin.PostAsJsonAsync("/api/admin/domain", new
        {
            name = "iver.local", maxMailboxQuotaBytes = 1073741824, maxRecipientsPerMessage = 100,
            maxMessageSizeBytes = 52428800, plusAddressingEnabled = true
        })).Content.ReadAsStringAsync();
        long domainId = Body(domain).GetProperty("id").GetInt64();

        var aliceMailbox = await (await admin.PostAsJsonAsync("/api/admin/mailbox", new { domainId, localPart = "aliceX", displayName = "Alice", password = "Atl4smail1!" })).Content.ReadAsStringAsync();
        var bobMailbox = await (await admin.PostAsJsonAsync("/api/admin/mailbox", new { domainId, localPart = "bobX", displayName = "Bob", password = "Atl4smail1!" })).Content.ReadAsStringAsync();
        long aliceId = Body(aliceMailbox).GetProperty("id").GetInt64();
        long bobId = Body(bobMailbox).GetProperty("id").GetInt64();

        var aliceUser = await (await admin.PostAsJsonAsync("/api/admin/user", new { username = "aliceX", password = "Atl4smail1!", displayName = "Alice", role = 0, domainId })).Content.ReadAsStringAsync();
        var bobUser = await (await admin.PostAsJsonAsync("/api/admin/user", new { username = "bobX", password = "Atl4smail1!", displayName = "Bob", role = 0, domainId })).Content.ReadAsStringAsync();
        Body(aliceUser).GetProperty("id").GetInt64().Should().BePositive();
        Body(bobUser).GetProperty("id").GetInt64().Should().BePositive();
    }

    [Fact]
    public async Task Alias_entrega_al_buzon_destino()
    {
        var admin = await _factory.CreateAdminClientAsync();
        var domains = await (await admin.GetAsync("/api/admin/domains")).Content.ReadAsStringAsync();
        // usar o crear dominio
        var domainInfo = Body(domains).EnumerateArray().FirstOrDefault();
        long domainId;
        if (domainInfo.ValueKind == JsonValueKind.Undefined)
        {
            var domain = await (await admin.PostAsJsonAsync("/api/admin/domain", new { name = Guid.NewGuid().ToString("N") + ".local" })).Content.ReadAsStringAsync();
            domainId = Body(domain).GetProperty("id").GetInt64();
        }
        else domainId = domainInfo.GetProperty("id").GetInt64();

        var mb = await (await admin.PostAsJsonAsync("/api/admin/mailbox", new { domainId, localPart = "tgt", displayName = "T", password = "Atl4smail1!" })).Content.ReadAsStringAsync();
        long mbId = Body(mb).GetProperty("id").GetInt64();
        var aliasRes = await (await admin.PostAsJsonAsync("/api/admin/alias", new { domainId, localPart = "ventasX", targetMailboxId = mbId })).Content.ReadAsStringAsync();
        Body(aliasRes).GetProperty("id").GetInt64().Should().BePositive();
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("Sololetras1")]
    public async Task Password_debil_rechazada(string weak)
    {
        var admin = await _factory.CreateAdminClientAsync();
        var res = await admin.PostAsJsonAsync("/api/admin/mailbox", new { domainId = 1, localPart = "x", displayName = "x", password = weak });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sin_rol_no_crea_dominio()
    {
        // crear dominio + usuario normal
        var admin = await _factory.CreateAdminClientAsync();
        var d = await (await admin.PostAsJsonAsync("/api/admin/domain", new { name = "rbac.local" })).Content.ReadAsStringAsync();
        long domainId = Body(d).GetProperty("id").GetInt64();
        await admin.PostAsJsonAsync("/api/admin/user", new { username = "ruser", password = "Atl4smail1!", displayName = "R", role = 0, domainId });

        // login como usuario rol 0
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "ruser", password = "Atl4smail1!" });
        login.EnsureSuccessStatusCode();
        var res = await client.PostAsJsonAsync("/api/admin/domain", new { name = "prohibido.local" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}