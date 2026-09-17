using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Services;
using AtlasMail.Infrastructure;
using AtlasMail.Infrastructure.Persistence;
using AtlasMail.Protocols.Smtp;
using AtlasMail.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAtlasMailInfrastructure(builder.Configuration);
// Entrega externa (FASE 2): conector SMTP + servicio orquestador (scoped, depende de la BD)
builder.Services.AddScoped<IExternalMailSender>(sp =>
    new SmtpExternalMailSender(
        sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger<AtlasMail.Protocols.Smtp.SmtpClient>(),
        TimeSpan.FromSeconds(builder.Configuration.GetValue("Delivery:ConnectTimeoutSeconds", 60))));
builder.Services.AddScoped<ExternalDeliveryService>();
// FASE 7: worker concurrente (HA); concurrency configurable
builder.Services.AddHostedService(sp => new DeliveryWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DeliveryWorker>>(),
    builder.Configuration.GetValue("Delivery:Hostname", "atlasmail.local"),
    builder.Configuration.GetValue("Delivery:LoopIntervalMs", 2000),
    builder.Configuration.GetValue("Delivery:Concurrency", 4)));

var host = builder.Build();

// Aplicar migraciones al arranque (verificación real de fresh install / segundo startup)
await DatabaseBootstrap.InitializeAsync(host.Services, adminUsername: null, adminPassword: null);
await host.RunAsync();