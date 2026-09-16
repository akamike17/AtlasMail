using AtlasMail.Infrastructure;
using AtlasMail.Infrastructure.Persistence;
using AtlasMail.Worker;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddAtlasMailInfrastructure(builder.Configuration);
builder.Services.AddHostedService<DeliveryWorker>();

var host = builder.Build();

// Aplicar migraciones al arranque (verificación real de fresh install / segundo startup)
await DatabaseBootstrap.InitializeAsync(host.Services, adminUsername: null, adminPassword: null);
await host.RunAsync();