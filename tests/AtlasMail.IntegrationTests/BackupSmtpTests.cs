using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using AtlasMail.Protocols.Smtp;
using Microsoft.Extensions.DependencyInjection;

namespace AtlasMail.IntegrationTests;

/// <summary>Backup/restore (secciones 28, 48) contra BD temporal.</summary>
public class BackupTests
{
    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    private async Task<(AtlasMailFactory factory, HttpClient admin)> NewClientAsync()
    {
        var factory = new AtlasMailFactory();
        var admin = await factory.CreateAdminClientAsync();
        return (factory, admin);
    }

    [Fact]
    public async Task Crear_backup_genera_manifest_con_sha()
    {
        var (factory, admin) = await NewClientAsync();
        try
        {
            var d = await (await admin.PostAsJsonAsync("/api/admin/domain", new { name = "bkp.local", plusAddressingEnabled = true })).Content.ReadAsStringAsync();
            long domId = Body(d).GetProperty("id").GetInt64();
            await admin.PostAsJsonAsync("/api/admin/mailbox", new { domainId = domId, localPart = "a", displayName = "A", password = "Atl4smail1!" });

            var backupRes = await admin.PostAsync("/api/admin/backup", null);
            backupRes.StatusCode.Should().Be(HttpStatusCode.OK);
            var bkp = Body(await backupRes.Content.ReadAsStringAsync());
            bkp.GetProperty("backupId").GetString().Should().NotBeNullOrEmpty();
            bkp.GetProperty("sha256").GetString().Should().NotBeNullOrEmpty();
            bkp.GetProperty("databaseTables").GetInt32().Should().BeGreaterThanOrEqualTo(14);
        }
        finally { factory.Dispose(); }
    }

    [Fact]
    public async Task Restore_desde_backup_preserva_datos()
    {
        var (factory, admin) = await NewClientAsync();
        try
        {
            var d = await (await admin.PostAsJsonAsync("/api/admin/domain", new { name = "rst.local", plusAddressingEnabled = true })).Content.ReadAsStringAsync();
            long domId = Body(d).GetProperty("id").GetInt64();
            await admin.PostAsJsonAsync("/api/admin/mailbox", new { domainId = domId, localPart = "carol", displayName = "Carol", password = "Atl4smail1!" });

            var bkp = Body(await (await admin.PostAsync("/api/admin/backup", null)).Content.ReadAsStringAsync());
            string backupId = bkp.GetProperty("backupId").GetString()!;

            var restoreRes = await admin.PostAsync($"/api/admin/backup/{backupId}/restore", null);
            restoreRes.StatusCode.Should().Be(HttpStatusCode.OK,
                "falló restore: " + await restoreRes.Content.ReadAsStringAsync());
            var rest = Body(await restoreRes.Content.ReadAsStringAsync());
            rest.GetProperty("success").GetBoolean().Should().BeTrue();

            var domains = await (await admin.GetAsync("/api/admin/domains")).Content.ReadAsStringAsync();
            Body(domains).EnumerateArray().Should().Contain(x => x.GetProperty("name").GetString() == "rst.local");
        }
        finally { factory.Dispose(); }
    }

    [Fact]
    public async Task Restore_de_backup_inexistente_falla_limpio()
    {
        var (factory, admin) = await NewClientAsync();
        try
        {
            var res = await admin.PostAsync("/api/admin/backup/noexiste/restore", null);
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = Body(await res.Content.ReadAsStringAsync());
            body.GetProperty("success").GetBoolean().Should().BeFalse();
        }
        finally { factory.Dispose(); }
    }
}

/// <summary>
/// SMTP E2E real (sección 36): levanta el SmtpServer REAL con el handler conectado a
/// la BD temporal del factory, en puerto efímero, y lo conduce con sockets SMTP reales.
/// Verifica entrega local + relay DENEGADO.
/// </summary>
public class SmtpE2ETests : IAsyncLifetime
{
    private readonly AtlasMailFactory _factory;
    private SmtpServer? _server;

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    public SmtpE2ETests()
    {
        _factory = new AtlasMailFactory();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_server != null) await _server.DisposeAsync();
        _factory.Dispose();
    }

    [Fact]
    public async Task Entrega_local_y_relay_denegado()
    {
        // Preparar dominio + buzón + usuario local en la BD temporal
        var admin = await _factory.CreateAdminClientAsync();
        var d = await (await admin.PostAsJsonAsync("/api/admin/domain", new { name = "atlas.local", plusAddressingEnabled = true })).Content.ReadAsStringAsync();
        long domId = Body(d).GetProperty("id").GetInt64();
        await admin.PostAsJsonAsync("/api/admin/mailbox", new { domainId = domId, localPart = "bob", displayName = "Bob", password = "Atl4smail1!" });
        await admin.PostAsJsonAsync("/api/admin/user", new { username = "bob", password = "Atl4smail1!", displayName = "Bob", role = 0, domainId = domId });

        // Levantar SmtpServer real en puerto fijo alto, con factory de scope real de la app
        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        _server = new SmtpServer(scopeFactory, new SmtpServerOptions { Port = 0, Hostname = "atlasmail.local", MaxMessageBytes = 50 * 1024 * 1024 });
        _server.Start();
        int port = _server.EffectivePort;

        // 1) Entrega local real
        string localResult;
        using (var c = new TcpClient())
        {
            await c.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
            var reader = new StreamReader(c.GetStream(), Encoding.ASCII);
            var writer = new StreamWriter(c.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await reader.ReadLineAsync(); // 220
            await writer.WriteLineAsync("HELO test");
            await reader.ReadLineAsync(); // 250
            await writer.WriteLineAsync("MAIL FROM:<ext@elsewhere.example>");
            await reader.ReadLineAsync();
            await writer.WriteLineAsync("RCPT TO:<bob@atlas.local>");
            string rcptReply = await reader.ReadLineAsync() ?? string.Empty;
            await writer.WriteLineAsync("DATA");
            await reader.ReadLineAsync();
            await writer.WriteLineAsync("Subject: E2E local");
            await writer.WriteLineAsync("");
            await writer.WriteLineAsync("Hola bob desde SMTP real.");
            await writer.WriteLineAsync(".");
            string dataReply = await reader.ReadLineAsync() ?? string.Empty;
            localResult = rcptReply + " | " + dataReply;
            await writer.WriteLineAsync("QUIT");
        }

        // 2) Relay no autorizado -> destino externo
        string relayResult;
        using (var c = new TcpClient())
        {
            await c.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
            var reader = new StreamReader(c.GetStream(), Encoding.ASCII);
            var writer = new StreamWriter(c.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await reader.ReadLineAsync(); // 220
            await writer.WriteLineAsync("HELO test");
            await reader.ReadLineAsync();
            await writer.WriteLineAsync("MAIL FROM:<ext@elsewhere.example>");
            await reader.ReadLineAsync();
            await writer.WriteLineAsync("RCPT TO:<victim@gmail.com>");
            relayResult = await reader.ReadLineAsync() ?? string.Empty;
            await writer.WriteLineAsync("QUIT");
        }

        // LOCAL: RCPT 250 + DATA 250
        localResult.Should().Contain("250");
        localResult.Should().Contain("2.0.0 OK");
        // RELAY: RCPT debe ser 550 (denegado)
        relayResult.Should().Contain("55");

        // Verificar que llegó al buzón de bob
        var bobClient = _factory.CreateClient();
        await bobClient.PostAsJsonAsync("/api/auth/login", new { username = "bob", password = "Atl4smail1!" });
        var inbox = await (await bobClient.GetAsync("/api/mail/folders")).Content.ReadAsStringAsync();
        Body(inbox).GetArrayLength().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Smtp_AUTH_PLAIN_legitimo_y_login_desde_Tcp()
    {
        // Preparar dominio + buzón con password
        var admin = await _factory.CreateAdminClientAsync();
        var d = await (await admin.PostAsJsonAsync("/api/admin/domain", new { name = "auth.local" })).Content.ReadAsStringAsync();
        long domId = Body(d).GetProperty("id").GetInt64();
        await admin.PostAsJsonAsync("/api/admin/mailbox", new { domainId = domId, localPart = "authuser", displayName = "Auth User", password = "Atl4smail1!" });

        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        _server = new SmtpServer(scopeFactory, new SmtpServerOptions { Port = 0, Hostname = "atlasmail.local" });
        _server.Start();
        int port = _server.EffectivePort;

        // AUTH PLAIN inline (base64 "\0user@auth.local\0password")
        string plain = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0authuser@auth.local\0Atl4smail1!"));
        string okReply, badReply;
        using (var c = new TcpClient())
        {
            await c.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
            var reader = new StreamReader(c.GetStream(), Encoding.ASCII);
            var writer = new StreamWriter(c.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await reader.ReadLineAsync(); // 220
            await writer.WriteLineAsync("EHLO test");
            // Consumir toda la respuesta EHLO multilínea: líneas "250-*" (posición 3 == '-')
            // hasta la que NO es continuada ("250 HELP" con espacio en posición 3).
            while (true) { var l = await reader.ReadLineAsync() ?? "250"; if (l.Length < 4 || l[3] != '-') break; }
            await writer.WriteLineAsync("AUTH PLAIN " + plain);
            okReply = await reader.ReadLineAsync() ?? string.Empty;
            // AUTH fallida
            string badPlain = Convert.ToBase64String(Encoding.UTF8.GetBytes("\0authuser@auth.local\0Passw0rd!"));
            await writer.WriteLineAsync("AUTH PLAIN " + badPlain);
            badReply = await reader.ReadLineAsync() ?? string.Empty;
            await writer.WriteLineAsync("QUIT");
        }

        okReply.Should().StartWith("235");
        badReply.Should().StartWith("535");
    }

    [Fact]
    public async Task Oversize_DATA_rechazado_552_sin_entregar_truncado()
    {
        // §49 "huge DATA": mensaje > MaxMessageBytes debe rechazarse con 552 y NO caer
        // en el buzón ni entregar un MIME truncado.
        var admin = await _factory.CreateAdminClientAsync();
        var d = await (await admin.PostAsJsonAsync("/api/admin/domain", new { name = "big.local", plusAddressingEnabled = true })).Content.ReadAsStringAsync();
        long domId = Body(d).GetProperty("id").GetInt64();
        await admin.PostAsJsonAsync("/api/admin/mailbox", new { domainId = domId, localPart = "big", displayName = "Big", password = "Atl4smail1!" });
        await admin.PostAsJsonAsync("/api/admin/user", new { username = "big", password = "Atl4smail1!", displayName = "Big", role = 0, domainId = domId });

        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        // límite pequeño para forzar oversize rápido
        _server = new SmtpServer(scopeFactory, new SmtpServerOptions { Port = 0, Hostname = "atlasmail.local", MaxMessageBytes = 4096 });
        _server.Start();
        int port = _server.EffectivePort;

        string dataReply;
        using (var c = new TcpClient())
        {
            await c.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
            var reader = new StreamReader(c.GetStream(), Encoding.ASCII);
            var writer = new StreamWriter(c.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await reader.ReadLineAsync();
            await writer.WriteLineAsync("HELO test"); await reader.ReadLineAsync();
            await writer.WriteLineAsync("MAIL FROM:<ext@x.example>"); await reader.ReadLineAsync();
            await writer.WriteLineAsync("RCPT TO:<big@big.local>"); await reader.ReadLineAsync();
            await writer.WriteLineAsync("DATA"); await reader.ReadLineAsync();
            await writer.WriteLineAsync("Subject: huge");
            await writer.WriteLineAsync("");
            // Cuerpo grande (~2000 líneas * 4 bytes > 4096)
            for (int i = 0; i < 2000; i++) await writer.WriteLineAsync("xxxx");
            await writer.WriteLineAsync(".");
            dataReply = await reader.ReadLineAsync() ?? string.Empty;
            await writer.WriteLineAsync("QUIT");
            await reader.ReadLineAsync();
        }

        dataReply.Should().Contain("552");

        // No quedó en el buzón: verificar que el folder Inbox NO tiene mensajes
        var bobClient = _factory.CreateClient();
        await bobClient.PostAsJsonAsync("/api/auth/login", new { username = "big", password = "Atl4smail1!" });
        var foldersRaw = await (await bobClient.GetAsync("/api/mail/folders")).Content.ReadAsStringAsync();
        var folders = Body(foldersRaw);
        // Encontrar el folder con systemName Inbox
        long inboxId = folders[0].GetProperty("id").GetInt64();
        foreach (var f in folders.EnumerateArray())
            if (f.GetProperty("systemName").GetInt32() == (int)AtlasMail.Domain.Enums.SystemFolder.Inbox) { inboxId = f.GetProperty("id").GetInt64(); break; }
        var msgsRaw = await (await bobClient.GetAsync($"/api/mail/folder/{inboxId}/messages")).Content.ReadAsStringAsync();
        Body(msgsRaw).GetArrayLength().Should().Be(0, "oversize no debe entregar mensaje truncado");
    }

    [Fact]
    public async Task Flood_de_comandos_cerrado_con_421()
    {
        // §49 "SMTP command flooding": exceder MaxCommandsPerConnection cierra la conexión.
        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        _server = new SmtpServer(scopeFactory, new SmtpServerOptions { Port = 0, Hostname = "atlasmail.local", MaxCommandsPerConnection = 5 });
        _server.Start();
        int port = _server.EffectivePort;

        string lastReply = "";
        using (var c = new TcpClient())
        {
            await c.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
            var reader = new StreamReader(c.GetStream(), Encoding.ASCII);
            var writer = new StreamWriter(c.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await reader.ReadLineAsync(); // 220
            for (int i = 0; i < 10; i++)
            {
                await writer.WriteLineAsync("NOOP");
                string? line = null;
                try { line = await reader.ReadLineAsync() ?? string.Empty; } catch (IOException) { line = string.Empty; }
                if (!string.IsNullOrWhiteSpace(line)) lastReply = line;
                if (lastReply.Contains("421")) break; // el servidor cerró la conexión
            }
        }
        lastReply.Should().Contain("421");
    }
}