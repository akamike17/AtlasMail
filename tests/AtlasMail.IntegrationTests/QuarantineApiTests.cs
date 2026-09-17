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
}