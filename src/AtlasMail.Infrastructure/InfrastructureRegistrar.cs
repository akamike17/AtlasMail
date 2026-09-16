using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Services;
using AtlasMail.Infrastructure.Dns;
using AtlasMail.Infrastructure.EmailAuth;
using AtlasMail.Infrastructure.Imap;
using AtlasMail.Infrastructure.Persistence;
using AtlasMail.Infrastructure.Storage;
using DnsClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

        // FASE 2: entrega externa (DNS MX + política + rate limit)
        services.AddSingleton<IMxResolver>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AtlasMail.Infrastructure.Dns.DnsMxResolver");
            var dns = new LookupClientOptions
            {
                UseCache = true,
                Timeout = TimeSpan.FromSeconds(5),
                Retries = 1,
                ThrowDnsErrors = false,
                UseTcpOnly = false,
                UseTcpFallback = true,
            };
            return new DnsMxResolver(dns, logger);
        });
        services.AddScoped<IExternalDeliveryPolicy, ExternalDeliveryPolicy>();
        services.AddSingleton(new ExternalDeliverySettings(
            MaxRetries: config.GetValue("Delivery:MaxRetries", 6),
            ConnectTimeout: TimeSpan.FromSeconds(config.GetValue("Delivery:ConnectTimeoutSeconds", 60)),
            StartTlsRequiredForExternal: config.GetValue("Delivery:StartTlsRequired", false),
            RateLimitPerMinute: config.GetValue("Delivery:RateLimitPerMinute", 0),
            RateLimitPerDomainPerMinute: config.GetValue("Delivery:RateLimitPerDomainPerMinute", 0),
            HeloName: config["Delivery:HeloName"]));
        services.AddSingleton<AtlasMail.Domain.Rules.SlidingWindowRateLimiter>(_ => new AtlasMail.Domain.Rules.SlidingWindowRateLimiter(60));
        // IExternalMailSender se registra donde se disponen el SmtpClient (AtlasMail.Web / Worker).

        // FASE 3: backend IMAP sobre MySQL/metadata + IMessageStore (protocolo desacoplado)
        services.AddScoped<IMailboxBackend, MySqlMailboxBackend>();

        // FASE 4: autenticación de correo (SPF/DKIM/DMARC) en recepción
        services.AddSingleton<IDnsRecordResolver>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AtlasMail.Infrastructure.Dns.DnsRecordResolver");
            return new DnsRecordResolver(timeout: TimeSpan.FromSeconds(5), logger: logger);
        });
        services.AddScoped<IEmailAuthenticationService>(sp =>
            new EmailAuthenticationService(
                sp.GetRequiredService<IDnsRecordResolver>(),
                dmarcEnforce: config.GetValue("Delivery:DmarcEnforce", true),
                logger: sp.GetRequiredService<ILoggerFactory>().CreateLogger<EmailAuthenticationService>()));
        services.AddScoped<IDomainMailAuthService, DomainMailAuthService>();
        services.AddScoped<DkimOutboundSigner>();
        services.AddScoped<IBackupService>(sp => new BackupService(
            sp.GetRequiredService<AtlasMailDbContext>(),
            sp.GetRequiredService<IMessageStore>(),
            sp.GetRequiredService<IAuditService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BackupService>>(),
            config["Storage:BackupPath"] ?? Path.Combine(AppContext.BaseDirectory, "backups")));

        return services;
    }
}