using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Services;
using AtlasMail.Infrastructure.Persistence;
using AtlasMail.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AtlasMail.Infrastructure;

public static class InfrastructureRegistrar
{
    public static IServiceCollection AddAtlasMailInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        string conn = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection no configurada");
        string storeRoot = config["Storage:Path"]
            ?? Path.Combine(AppContext.BaseDirectory, "mailstore");

        services.AddDbContext<AtlasMailDbContext>(o =>
            o.UseMySql(conn, ServerVersion.AutoDetect(conn), mb => mb.EnableRetryOnFailure(3)));

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<AtlasMailDbContext>());
        services.AddSingleton<IMessageStore>(_ => new FileSystemMessageStore(storeRoot));

        // Services
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAdminService, AdminService>();
        services.AddScoped<IAddressResolutionService, AddressResolutionService>();
        services.AddScoped<IInboundDeliveryService, InboundDeliveryService>();
        services.AddScoped<IOutboundQueueService, OutboundQueueService>();
        services.AddScoped<ISubmissionService, SubmissionService>();
        services.AddScoped<IMailboxService, MailboxService>();
        services.AddScoped<IMailSearchService, MailSearchService>();
        services.AddScoped<IMessageTraceService, MessageTraceService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IAdminDashboardService, AdminDashboardService>();
        services.AddScoped<IRuleEngine, RuleEngine>();
        services.AddScoped<IAttachmentScanner, NoOpAttachmentScanner>();
        services.AddSingleton<IMailIntelligenceService, DisabledMailIntelligenceService>();
        services.AddScoped<IBackupService>(sp => new BackupService(
            sp.GetRequiredService<AtlasMailDbContext>(),
            sp.GetRequiredService<IMessageStore>(),
            sp.GetRequiredService<IAuditService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackupService>>(),
            config["Storage:BackupPath"] ?? Path.Combine(AppContext.BaseDirectory, "backups")));

        return services;
    }
}