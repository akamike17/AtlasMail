using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AtlasMail.Application;
using AtlasMail.Application.Services;
using AtlasMail.Domain.Entities;
using AtlasMail.Infrastructure.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.UnitTests;

/// <summary>Tests del backend de IA (fake HttpMessageHandler, sin red) y del gate de consentimiento.</summary>
public class AiBackendTests
{
    private static HttpClient FakeClient(string json)
    {
        var handler = new FakeHandler(json);
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
    }

    [Fact]
    public async Task Backend_parsea_respuesta_openai_compatible()
    {
        var json = "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Hola\"}}]}";
        var backend = new OpenAiCompatibleMailIntelligenceBackend(FakeClient(json), "http://x/v1", null, "test", NullLogger.Instance, enabled: true);
        Assert.True(backend.Enabled);
        Assert.Contains("test", backend.Provider);
        var r = await backend.SummarizeAsync(new("Asunto", "cuerpo"), 200);
        Assert.Equal("Hola", r);
    }

    [Fact]
    public async Task Backend_falla_usa_fallback_local()
    {
        var backend = new OpenAiCompatibleMailIntelligenceBackend(FakeClient("not-json"), "http://x/v1", null, "test", NullLogger.Instance, enabled: true);
        var r = await backend.SummarizeAsync(new("Asunto", "Contenido largo de prueba que excede el limite para verificar el fallback del resumen local"), 20);
        Assert.False(string.IsNullOrWhiteSpace(r)); // fallback devolvió algo
    }

    [Fact]
    public async Task Semantica_usa_coseno_local_sin_backend()
    {
        var backend = new OpenAiCompatibleMailIntelligenceBackend(FakeClient(""), "http://x/v1", null, "test", NullLogger.Instance, enabled: true);
        var r = await backend.SemanticSearchAsync("urgente pago", new[] { "factura vencida pago", "reunion lunes", "reclamo urgente pago" }, topK: 2);
        Assert.NotEmpty(r.Indices);
        Assert.Equal("local-cosine", r.Reason);
    }

    [Fact]
    public void Disabled_backend_no_activo()
    {
        var b = new DisabledMailIntelligenceBackend();
        Assert.False(b.Enabled);
        Assert.Equal("none", b.Provider);
    }

    private sealed class FakeHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }
}

public class AiConsentTests
{
    [Fact]
    public async Task Consentimiento_por_defecto_falso_y_revocable()
    {
        using var ctx = new FakeAppDbContext("ai_" + Guid.NewGuid().ToString("N"));
        var domain = new MailDomain { Name = "ai.local", Enabled = true, MaxMailboxQuotaBytes = 1024, MaxMessageSizeBytes = 1024, MaxRecipientsPerMessage = 10 };
        var mb = new Mailbox { Domain = domain, LocalPart = "user", PasswordHash = "h", QuotaBytes = 1024 };
        ctx.Domains.Add(domain); ctx.Mailboxes.Add(mb);
        await ctx.SaveChangesAsync();

        var facade = new MailIntelligenceFacade(ctx, new AtlasMail.Security.MailIntelligence.LocalMailIntelligenceService(), null, Microsoft.Extensions.Logging.Abstractions.NullLogger<MailIntelligenceFacade>.Instance);
        Assert.False(await facade.HasConsentAsync(mb.Id));

        await facade.SetConsentAsync(mb.Id, true);
        Assert.True(await facade.HasConsentAsync(mb.Id));
        Assert.True((await ctx.Mailboxes.FindAsync(mb.Id)).AiConsent);

        await facade.SetConsentAsync(mb.Id, false);
        Assert.False(await facade.HasConsentAsync(mb.Id));
    }
}