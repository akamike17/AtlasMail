using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AtlasMail.Infrastructure.Persistence;

/// <summary>
/// Factory design-time para migraciones EF. Lee la conexión de la variable de entorno
/// ATLASMAIL_CONNECTION_STRING (sin credencial hardcoded en el repo).
/// </summary>
public class AtlasMailDbContextFactory : IDesignTimeDbContextFactory<AtlasMailDbContext>
{
    public AtlasMailDbContext CreateDbContext(string[] args)
    {
        var conn = Environment.GetEnvironmentVariable("ATLASMAIL_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(conn))
            throw new InvalidOperationException(
                "Define ATLASMAIL_CONNECTION_STRING (server=...;port=3306;database=atlasmail;user=...;password=...) antes de usar dotnet ef.");

        var options = new DbContextOptionsBuilder<AtlasMailDbContext>()
            .UseMySql(conn, ServerVersion.AutoDetect(conn), o => o.EnableRetryOnFailure())
            .Options;
        return new AtlasMailDbContext(options);
    }
}