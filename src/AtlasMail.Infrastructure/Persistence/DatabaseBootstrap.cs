using AtlasMail.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AtlasMail.Application;

namespace AtlasMail.Infrastructure.Persistence;

/// <summary>
/// Migraciones + seed idempotente:
///  - aplica migraciones pendientes en el arranque,
///  - crea el SuperAdmin inicial a partir de configuración (nunca password hardcoded),
///  - crea configuraciones por defecto.
/// NO crea demo data ni passwords por defecto.
/// </summary>
public static class DatabaseBootstrap
{
    public static async Task InitializeAsync(IServiceProvider services, string? adminUsername, string? adminPassword, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AtlasMailDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseBootstrap");

        await db.Database.MigrateAsync(ct);
        logger.LogInformation("Migraciones aplicadas.");

        if (!string.IsNullOrWhiteSpace(adminUsername) && !string.IsNullOrWhiteSpace(adminPassword))
        {
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var exists = await db.Users.AnyAsync(u => u.Username == adminUsername, ct);
            if (!exists)
            {
                db.Users.Add(new Domain.Entities.User
                {
                    Username = adminUsername.Trim().ToLowerInvariant(),
                    DisplayName = "AtlasMail Administrator",
                    PasswordHash = hasher.Hash(adminPassword),
                    Role = UserRole.SuperAdmin
                });
                await db.SaveChangesAsync(ct);
                await db.AuditEvents.AddAsync(new Domain.Entities.AuditEvent
                {
                    Action = "Seed.AdminCreated", Actor = adminUsername, Target = "user", TargetId = adminUsername, Result = "OK"
                }, ct);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("SuperAdmin bootstrap creado: {User}", adminUsername);
            }
        }

        // Configuraciones por defecto
        var defaults = new[] { ("smtp.maxMessageSizeBytes", "52428800"), ("smtp.maxRecipientsPerMessage", "100"),
            ("auth.maxLoginFailures", "5"), ("security.allowAuthenticatedExternalSend", "true") };
        foreach (var (key, value) in defaults)
        {
            if (!await db.ConfigurationEntries.AnyAsync(c => c.Key == key, ct))
            {
                db.ConfigurationEntries.Add(new Domain.Entities.ConfigurationEntry { Key = key, Value = value });
            }
        }
        await db.SaveChangesAsync(ct);
    }
}