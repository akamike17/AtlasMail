using System.Net.Http.Headers;
using AtlasMail.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AtlasMail.IntegrationTests;

/// <summary>
/// Banco de pruebas de integración con MySQL TEMPORAL dedicado (spec §35: "No tocar DB real").
/// Crea una BD atlasmail_ci_<guid>, aplica migraciones, expone el cliente HTTP autenticado.
/// </summary>
public class AtlasMailFactory : WebApplicationFactory<AtlasMail.Web.Program>
{
    public string DbName { get; }
    public string ConnectionString { get; }
    public string MailStoreDir { get; }

    public AtlasMailFactory()
    {
        DbName = "atlasmail_ci_" + Guid.NewGuid().ToString("N")[..8];
        // Conexión a MySQL TEMPORAL dedicada por prueba. Las credenciales del servidor
        // vienen de la variable de entorno ATLASMAIL_CONNECTION_STRING (no se hardcodea
        // ningún secreto en el repo; spec §53). El factory crea una BD efímera atlasmail_ci_*.
        var serverParams = ParseDbParams(Environment.GetEnvironmentVariable("ATLASMAIL_CONNECTION_STRING")
                            ?? "server=localhost;port=3306;user=root;password=;");
        ConnectionString = $"server={serverParams.Host};port={serverParams.Port};database={DbName};" +
                           $"user={serverParams.User};password={serverParams.Password};";
        MailStoreDir = Path.Combine(Path.GetTempPath(), "atlasmail_x_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(MailStoreDir);

        // La config por env var tiene precedencia y se lee al inicio de Program.cs (el
        // ConfigureAppConfiguration del factory corre después). Establecer por entorno.
        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", ConnectionString);
        Environment.SetEnvironmentVariable("Storage__Path", MailStoreDir);
        Environment.SetEnvironmentVariable("Storage__BackupPath",
            Path.Combine(Path.GetTempPath(), "atlasmail_backup_" + Guid.NewGuid().ToString("N")));
        Environment.SetEnvironmentVariable("Smtp__Enabled", "0");
        Environment.SetEnvironmentVariable("Admin__Username", "admin");
        Environment.SetEnvironmentVariable("Admin__Password", "Atl4smail1!");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
    }

    /// <summary>Extrae host/puerto/usuario/password de una cadena de conexión MySQL estándar.</summary>
    private static (string Host, int Port, string User, string Password) ParseDbParams(string conn)
    {
        string host = "localhost", user = "root", password = "";
        int port = 3306;
        foreach (var part in conn.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0].Trim().ToLowerInvariant())
            {
                case "server": host = kv[1].Trim(); break;
                case "port": int.TryParse(kv[1].Trim(), out port); break;
                case "user": case "uid": user = kv[1].Trim(); break;
                case "password": case "pwd": password = kv[1].Trim(); break;
            }
        }
        return (host, port, user, password);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        // Limpiar BD temporal tras la prueba
        try
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AtlasMailDbContext>();
            db.Database.ExecuteSqlRaw($"DROP DATABASE IF EXISTS `{DbName}`");
        }
        catch { /* best effort */ }
        try { if (Directory.Exists(MailStoreDir)) Directory.Delete(MailStoreDir, true); } catch { }
    }

    public async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "Atl4smail1!" });
        res.EnsureSuccessStatusCode();
        return client;
    }
}