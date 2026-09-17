using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace AtlasMail.IntegrationTests;

/// <summary>
/// FASE 5 — Cuarentena y blocklist (spec §22) vía API admin (SecurityAdmin/SuperAdmin).
/// </summary>
public class QuarantineApiTests : IClassFixture<AtlasMailFactory>
{
    private readonly AtlasMailFactory _factory;
    public QuarantineApiTests(AtlasMailFactory factory) => _factory = factory;
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Cuarentena_blocklist_endpoints_funcionan()
    {
        var admin = await _factory.CreateAdminClientAsync();

        // lista inicial de cuarentena (vacía en limpio)
        var list = await (await admin.GetAsync("/api/admin/quarantine")).Content.ReadAsStringAsync();
        Body(list).ValueKind.Should().Be(JsonValueKind.Array);

        // bloquear un remitente exacto
        var blockRes = await admin.PostAsJsonAsync("/api/admin/quarantine/block", new { value = "spammer@baddie.net", kind = "exact", reason = "verificado spam" });
        blockRes.EnsureSuccessStatusCode();
        var blocked = await (await admin.GetAsync("/api/admin/quarantine/blocked")).Content.ReadAsStringAsync();
        var arr = Body(blocked);
        arr.GetArrayLength().Should().BeGreaterThan(0);
        arr[0].GetProperty("value").GetString().Should().Contain("baddie.net");

        // la blocklist coincide en la ingesta (via servicio, no hay SMTP en factory)
        long blockId = arr[0].GetProperty("id").GetInt64();
        var unblock = await admin.DeleteAsync($"/api/admin/quarantine/blocked/{blockId}");
        unblock.EnsureSuccessStatusCode();
        var blocked2 = await (await admin.GetAsync("/api/admin/quarantine/blocked")).Content.ReadAsStringAsync();
        Body(blocked2).GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Metrics_endpoint_expone_snapshot_con_auth()
    {
        var admin = await _factory.CreateAdminClientAsync();
        // login inválido explícito: debe registrar auth_login_failures
        var anon = _factory.CreateClient();
        await anon.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "wrong-password" });

        var json = await (await admin.GetAsync("/api/metrics")).Content.ReadAsStringAsync();
        var snap = Body(json).GetProperty("snapshot");
        snap.TryGetProperty("auth.login_failures", out _).Should().BeTrue();
        snap.GetProperty("auth.login_failures").GetDouble().Should().BeGreaterThan(0);
        snap.TryGetProperty("storage.bytes", out _).Should().BeTrue();
        snap.TryGetProperty("queue.pending_total", out _).Should().BeTrue();

        // formato texto Prometheus sanitizado
        var text = await admin.GetStringAsync("/api/metrics/text");
        text.Should().Contain("auth_login_failures ");
        text.Should().NotContainAny("password", "secret");
    }

    [Fact]
    public async Task Ia_analyze_esta_deshabilitada_por_defecto()
    {
        var client = _factory.CreateClient();
        // construir un cliente webmail: la IA requiere sesión; con IA deshabilitada devuelve enabled=false
        // (login admin y pedir a /api/personal/ai/analyze con csrf)
        var admin = await _factory.CreateAdminClientAsync();
        var res = await admin.PostAsJsonAsync("/api/personal/ai/analyze", new { subject = "urgente", body = "verifica tu cuenta ahora" });
        var json = await res.Content.ReadAsStringAsync();
        // por defecto Ai:Enabled=false en el factory, por lo que el endpoint responde enabled=false
        var root = Body(json);
        root.GetProperty("enabled").GetBoolean().Should().BeFalse();
    }
}