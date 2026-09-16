using AtlasMail.Application;
using AtlasMail.Infrastructure;
using AtlasMail.Infrastructure.Persistence;
using AtlasMail.Protocols.Smtp;
using AtlasMail.Security;
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
            Enabled = builder.Configuration.GetValue<int>("Smtp:Enabled") == 1
        });
});
builder.Services.AddHostedService<SmtpHostedService>();
builder.Services.AddHostedService<DeliveryWorker>();

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

app.MapGet("/api/health", (AtlasMail.Web.Smtp.IHealthCheckSubscriber health) =>
    Results.Ok(new { status = "ok", components = health.Snapshot() })).AllowAnonymous();

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

// NOTA: el marcador `public partial class Program` vive en ProgramMarker.cs (namespace AtlasMail.Web)