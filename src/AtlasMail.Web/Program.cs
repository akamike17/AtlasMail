using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Services;
using AtlasMail.Infrastructure;
using AtlasMail.Infrastructure.Persistence;
using AtlasMail.Protocols.Imap;
using AtlasMail.Protocols.Smtp;
using AtlasMail.Security;
using AtlasMail.Web.Imap;
using AtlasMail.Web.Smtp;
using AtlasMail.Worker;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(o =>
{
    // deny-by-default: toda request debe estar autenticada salvo [AllowAnonymous]
    var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    o.Filters.Add(new AuthorizeFilter(policy));
});

// SMSec
builder.Services.AddAtlasMailInfrastructure(builder.Configuration);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AtlasMail.Web.Services.CurrentUserService>();
builder.Services.AddScoped<AtlasMail.Web.Services.ICurrentUser, AtlasMail.Web.Services.HttpCurrentUser>();

// Password (Security layer)
builder.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
builder.Services.AddSingleton<IPasswordPolicy>(_ => new DefaultPasswordPolicy());

// Auth con cookies (secure, HttpOnly). Se prepara TOTP/MFA en capa Security preparado.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/AccessDenied";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = 401;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/api"))
                {
                    ctx.Response.StatusCode = 403;
                    return Task.CompletedTask;
                }
                ctx.Response.Redirect(ctx.RedirectUri);
                return Task.CompletedTask;
            }
        };
    });

// Antiforgery para fetch(): header X-CSRF-TOKEN (sección 33)
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

builder.Services.AddAuthorization();

// Health checks (sección 32)
builder.Services.AddHealthChecks();

// SMTP + worker de cola (hosted)
builder.Services.AddSingleton<IHealthCheckSubscriber, HealthCheckSubscriber>();
// Rate-limit SMTP AUTH por IP (spec §49): singleton compartido, ventana/límite configurables.
builder.Services.AddSingleton(new AtlasMail.Domain.Rules.AuthRateLimiter(
    builder.Configuration.GetValue("Smtp:AuthFailuresPerIpMax", 10),
    TimeSpan.FromMinutes(builder.Configuration.GetValue("Smtp:AuthFailureWindowMinutes", 15))));
builder.Services.AddScoped<SmtpInboundHandler>();
builder.Services.AddScoped<ISmtpMessageHandler>(sp => sp.GetRequiredService<SmtpInboundHandler>());
// SmtpServer SIEMPRE registrado (para que SmtpHostedService resuelva); el flag Enabled
// decide si el listener se abrepórtá (0 en pruebas/CI para no ocupar puerto).
builder.Services.AddSingleton<SmtpServer>(sp =>
{
    var scopeFactory = sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
    return new SmtpServer(scopeFactory,
            new SmtpServerOptions
                        {
                            Port = builder.Configuration.GetValue<int>("Smtp:Port", 2525),
                            Hostname = builder.Configuration["Smtp:Hostname"] ?? "atlasmail.local",
                            MaxMessageBytes = (int)builder.Configuration.GetValue<long>("Smtp:MaxMessageBytes", 50 * 1024 * 1024),
                            Enabled = builder.Configuration.GetValue<int>("Smtp:Enabled") == 1,
                            DataTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Smtp:DataTimeoutSeconds", 600)),
                            TlsCertificate = LoadTlsCert(builder.Configuration, "Smtp"),
                            RequireTlsForAuth = builder.Configuration.GetValue("Smtp:RequireTls", false)
                        });
            });
builder.Services.AddHostedService<SmtpHostedService>();
// FASE 7: worker concurrente (HA); concurrency configurable. Desactivable vía Delivery:WorkerEnabled
// (por defecto activo en producción; los tests de integración lo apagan con 0 para no dejar un worker
// de fondo reintentando contra una BD temporal que se dropea — impide el cierre limpio del testhost).
if (builder.Configuration.GetValue<int>("Delivery:WorkerEnabled", 1) == 1)
{
    builder.Services.AddHostedService(sp => new DeliveryWorker(
        sp.GetRequiredService<IServiceScopeFactory>(),
        sp.GetRequiredService<ILogger<DeliveryWorker>>(),
        builder.Configuration.GetValue("Delivery:Hostname", "atlasmail.local"),
        builder.Configuration.GetValue("Delivery:LoopIntervalMs", 2000),
        builder.Configuration.GetValue("Delivery:Concurrency", 4)));
}

// FASE 3: servidor IMAP4rev1 (desacoplado del almacenamiento vía IMailboxBackend)
// Siempre registrado; el flag Enabled decide si abre el listener (0 en CI).
builder.Services.AddSingleton<ImapServer>(sp =>
{
    var scopeFactory = sp.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
    return new ImapServer(scopeFactory,
            new ImapServerOptions
            {
                Port = builder.Configuration.GetValue<int>("Imap:Port", 143),
                Hostname = builder.Configuration["Imap:Hostname"] ?? "atlasmail.local",
                MaxMessageBytes = (int)builder.Configuration.GetValue<long>("Imap:MaxMessageBytes", 50 * 1024 * 1024),
                Enabled = builder.Configuration.GetValue<int>("Imap:Enabled") == 1,
                TlsCertificate = LoadTlsCert(builder.Configuration, "Imap"),
                RequireTlsForLogin = builder.Configuration.GetValue("Imap:RequireTls", false)
            },
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger<ImapServer>());
});
builder.Services.AddHostedService<ImapHostedService>();

// FASE 2: entrega externa (SMTP outbound) + DNS health.
// ExternalDeliveryService es scoped porque depende de IExternalDeliveryPolicy (BD);
// el DeliveryWorker resuelve dentro de un scope por elemento de cola.
builder.Services.AddScoped<IExternalMailSender>(sp =>
    new SmtpExternalMailSender(
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger<SmtpClient>(),
        TimeSpan.FromSeconds(builder.Configuration.GetValue("Delivery:ConnectTimeoutSeconds", 60))));
builder.Services.AddScoped<ExternalDeliveryService>();

// DNS health probe (FASE 2) — opcional para no depender de red real en CI
if (builder.Configuration.GetValue("Delivery:DnsProbeEnabled", true))
{
    builder.Services.AddHostedService(sp => new DnsHealthProbe(
        sp.GetRequiredService<AtlasMail.Application.Abstractions.IMxResolver>(),
        sp.GetRequiredService<AtlasMail.Web.Smtp.IHealthCheckSubscriber>(),
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger<DnsHealthProbe>(),
        builder.Configuration["Delivery:DnsProbeDomain"] ?? "gmail.com"));
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// Secure headers básicos (sección 33)
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self';";
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Antiforgery token endpoint para fetch(): se regenera tras login (skill)
app.MapGet("/api/antiforgery/token", (Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, HttpContext http) =>
{
    var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!,
            new CookieOptions { HttpOnly = false, SameSite = SameSiteMode.Lax });
        return Results.Ok(new { token = tokens.RequestToken });
}).AllowAnonymous();

app.MapGet("/api/health", (AtlasMail.Web.Smtp.IHealthCheckSubscriber health, AtlasMail.Application.IOutboundQueueService queue) =>
    {
        // FASE 2: métricas de cola en el health endpoint
        var list = queue.ListAsync(take: 1000).GetAwaiter().GetResult();
        var counts = list
            .GroupBy(q => q.State)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        return Results.Ok(new
        {
            status = "ok",
            components = health.Snapshot(),
            queue = new
            {
                pending = counts.GetValueOrDefault("Pending", 0),
                processing = counts.GetValueOrDefault("Processing", 0),
                deferred = counts.GetValueOrDefault("Deferred", 0),
                delivered = counts.GetValueOrDefault("Delivered", 0),
                failed = counts.GetValueOrDefault("Failed", 0),
                deadLetter = counts.GetValueOrDefault("DeadLetter", 0)
            }
        });
    }).AllowAnonymous();

// FASE 7: métricas de observabilidad (spec §32). Backfill de gauges de storage desde el store,
// sin exponer datos sensibles.
app.MapGet("/api/metrics", async (AtlasMail.Application.Abstractions.IMetricsRegistry metrics,
        AtlasMail.Application.Abstractions.IMessageStore store,
        AtlasMail.Web.Smtp.IHealthCheckSubscriber health,
        AtlasMail.Application.IOutboundQueueService queue,
        CancellationToken ct) =>
    {
        try
        {
            metrics.SetGauge("storage.bytes", await store.TotalSizeAsync(ct));
        }
        catch { /* gauge opcional */ }
        var counts = (await queue.ListAsync(take: 1000, ct: ct))
            .GroupBy(q => q.State)
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        metrics.SetGauge("queue.pending_total", counts.GetValueOrDefault("Pending", 0));
        metrics.SetGauge("queue.deferred_total", counts.GetValueOrDefault("Deferred", 0));
        metrics.SetGauge("queue.failed_total", counts.GetValueOrDefault("Failed", 0));
        return Results.Json(new { snapshot = metrics.Snapshot() });
    }).AllowAnonymous();

// Formato texto Prometheus (nombres sanitizados), sin datos sensibles.
app.MapGet("/api/metrics/text", (AtlasMail.Application.Abstractions.IMetricsRegistry metrics,
        AtlasMail.Application.Abstractions.IMessageStore store, CancellationToken ct) =>
    {
        try { metrics.SetGauge("storage.bytes", store.TotalSizeAsync(ct).GetAwaiter().GetResult()); } catch { }
        return Results.Text(AtlasMail.Application.Abstractions.MetricsFormat.ToText(metrics.Snapshot()), "text/plain");
    }).AllowAnonymous();

// Bootstrap: migraciones + admin inicial desde config (nunca password hardcoded en repo)
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AtlasMailDbContext>().Database.Migrate();
    var config = app.Configuration;
    await DatabaseBootstrap.InitializeAsync(app.Services,
        adminUsername: config["Admin:Username"],
        adminPassword: config["Admin:Password"]);
}

app.Run();

// Carga opcional del certificado TLS del servidor (SMTP/IMAP STARTTLS). Si la ruta no está
// configurada → null (sin STARTTLS). El password debe venir de entorno/secrets, nunca hardcoded.
static System.Security.Cryptography.X509Certificates.X509Certificate2? LoadTlsCert(Microsoft.Extensions.Configuration.IConfiguration cfg, string section)
{
    string? path = cfg[$"{section}:TlsCertificatePath"];
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
    string? password = cfg[$"{section}:TlsCertificatePassword"];
    return new System.Security.Cryptography.X509Certificates.X509Certificate2(path, password ?? string.Empty);
}

// NOTA: el marcador `public partial class Program` vive en ProgramMarker.cs (namespace AtlasMail.Web)