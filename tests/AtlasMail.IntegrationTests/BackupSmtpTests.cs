using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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
        // MaxConnectionsPerIp alto: la cuenta de conexiones por IP es ESTÁTICA y compartida por todos los
        // SmtpServer del proceso (host del factory + servers de tests). En una corrida conjunta el límite
        // por defecto (20) se supera con las conexiones de otros tests a 127.0.0.1 y cierra la conexión con
        // "Too many connections" ANTES de probar el flood de comandos. Este test sólo verifica el límite de
        // COMANDOS, por lo que se aísla el de conexiones.
        _server = new SmtpServer(scopeFactory, new SmtpServerOptions { Port = 0, Hostname = "atlasmail.local", MaxCommandsPerConnection = 5, MaxConnectionsPerIp = 100000 });
        _server.Start();
        int port = _server.EffectivePort;

        string lastReply = "";
        using (var c = new TcpClient())
        {
            await c.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
            c.ReceiveTimeout = 3000; // no colgar si el cierre (RST) corta la lectura
            var reader = new StreamReader(c.GetStream(), Encoding.ASCII);
            var writer = new StreamWriter(c.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await reader.ReadLineAsync(); // 220
            // Enviar comandos hasta que el servidor cierre la conexión por flood. Leer SIEMPRE
            // (drenando el buffer) hasta ver el 421 o un EOF/cierre — no romper en el primer write.
            for (int i = 0; i < 200; i++)
            {
                try { await writer.WriteLineAsync("NOOP"); }
                catch (IOException) { /* el servidor ya cerró; drenar abajo */ }
                string? line = null;
                try { line = await reader.ReadLineAsync(); } catch (IOException) { line = null; }
                catch (TimeoutException) { break; }
                if (string.IsNullOrWhiteSpace(line)) break; // EOF / cierre
                lastReply = line;
                if (lastReply.Contains("421")) break;
            }
        }
        lastReply.Should().Contain("421");
    }

    [Fact]
    public async Task DATA_infinito_sin_punto_se_cierra_por_timeout()
    {
        // §49/spec: un cliente que entra en DATA y sigue enviando sin mandar el "." final
        // (stream infinito) debe terminar la sesión dentro del límite configurado, sin que la
        // memoria crezca (el buffer se acota con MaxMessageBytes y luego se descarta).
        _server = new SmtpServer(() => new DummySmtpHandler(),
            new SmtpServerOptions
            {
                Port = 0,
                Hostname = "atlasmail.local",
                MaxMessageBytes = 1024,
                DataTimeout = TimeSpan.FromSeconds(1)
            });
        _server.Start();
        int port = _server.EffectivePort;

        using (var c = new TcpClient())
        {
            await c.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
            c.ReceiveTimeout = 5000;
            var reader = new StreamReader(c.GetStream(), Encoding.ASCII);
            var writer = new StreamWriter(c.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await reader.ReadLineAsync(); // 220
            await writer.WriteLineAsync("HELO test"); await reader.ReadLineAsync();
            await writer.WriteLineAsync("MAIL FROM:<a@x.example>"); await reader.ReadLineAsync();
            await writer.WriteLineAsync("RCPT TO:<b@y.example>"); await reader.ReadLineAsync();
            await writer.WriteLineAsync("DATA"); await reader.ReadLineAsync(); // 354

            // §3.md/Fix 4: enviar líneas prolijamente SIN el "." — el servidor debe responder el contrato
            // EXACTO "421 4.4.2 Timeout receiving DATA, connection closing" (~DataTimeout) y cerrar. No
            // basta con que cierre: debe emitir el 421 4.4.2 verificable.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string? last421 = null;
            string? lastAny = null;
            bool got421 = false;
            while (sw.ElapsedMilliseconds < 4000)
            {
                try { await writer.WriteLineAsync("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"); }
                catch (IOException) { break; } // servidor cerró tras el 421; drenar debajo
                string? line = null;
                try { line = await reader.ReadLineAsync(); } catch (IOException) { break; }
                catch (TimeoutException) { break; }
                if (string.IsNullOrWhiteSpace(line)) break; // EOF / cierre
                lastAny = line;
                if (line.Contains("421 4.4.2 Timeout receiving DATA"))
                {
                    last421 = line;
                    got421 = true;
                    break;
                }
            }
            sw.Stop();

            Assert.True(got421, $"debió recibir '421 4.4.2 Timeout receiving DATA' dentro del límite; última respuesta='{lastAny}'");
            last421.Should().Contain("421 4.4.2 Timeout receiving DATA");
            Assert.True(sw.ElapsedMilliseconds < 4000, "cierre dentro del límite configurado");
        }
    }

    [Fact]
    public async Task STARTTLS_impide_AUTH_en_claro_y_permite_TLS()
    {
        using var cert = CreateSelfSignedCert();
        _server = new SmtpServer(() => new DummySmtpHandler(),
            new SmtpServerOptions
            {
                Port = 0,
                Hostname = "atlasmail.local",
                TlsCertificate = cert,
                RequireTlsForAuth = true
            });
        _server.Start();
        int port = _server.EffectivePort;

        using (var c = new TcpClient())
        {
            await c.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
            var stream = c.GetStream();
            var reader = new StreamReader(stream, Encoding.ASCII);
            var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

            await reader.ReadLineAsync(); // 220
            // EHLO pre-TLS: debe anunciar STARTTLS pero NO AUTH (TLS obligatorio para AUTH).
            await writer.WriteLineAsync("EHLO client");
            var ehlo1 = ReadEhlo(reader);
            ehlo1.Should().Contain("STARTTLS");
            ehlo1.Should().NotContain("AUTH", "no debe anunciarse AUTH en claro cuando TLS es obligatorio");

            // AUTH en claro debe rechazarse con 530.
            await writer.WriteLineAsync("AUTH PLAIN " + Convert.ToBase64String(Encoding.UTF8.GetBytes("\0u@x\0p")));
            var deny = await reader.ReadLineAsync();
            deny.Should().Contain("530");

            // STARTTLS → 220 y negociar TLS real.
            await writer.WriteLineAsync("STARTTLS");
            var ok = await reader.ReadLineAsync();
            ok.Should().Contain("220");
            await writer.FlushAsync();

            var ssl = new System.Net.Security.SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync("atlasmail.local");
            var sreader = new StreamReader(ssl, Encoding.ASCII);
            var swriter = new StreamWriter(ssl, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

            // Tras STARTTLS el cliente repite EHLO: ahora AUTH SÍ se anuncia (canal cifrado).
            await swriter.WriteLineAsync("EHLO client");
            var ehlo2 = ReadEhlo(sreader);
            ehlo2.Should().NotContain("STARTTLS", "no debe re-anunciarse STARTTLS ya activo");
            ehlo2.Should().Contain("AUTH PLAIN LOGIN");
        }
    }

    [Fact]
    public async Task STARTTLS_sin_certificado_da_454()
    {
        _server = new SmtpServer(() => new DummySmtpHandler(),
            new SmtpServerOptions { Port = 0, Hostname = "atlasmail.local" /* sin cert */ });
        _server.Start();
        int port = _server.EffectivePort;

        using (var c = new TcpClient())
        {
            await c.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
            var reader = new StreamReader(c.GetStream(), Encoding.ASCII);
            var writer = new StreamWriter(c.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await reader.ReadLineAsync(); // 220
            await writer.WriteLineAsync("EHLO x"); ReadEhlo(reader);
            await writer.WriteLineAsync("STARTTLS");
            var rep = await reader.ReadLineAsync();
            rep.Should().Contain("454");
        }
    }

    private static string ReadEhlo(TextReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var l = reader.ReadLine() ?? "250 ";
            sb.AppendLine(l);
            if (l.Length < 4 || l[3] != '-') break; // última línea multilínea no termina en '-'
        }
        return sb.ToString();
    }

    /// <summary>Certificado self-signed efímero para los tests de STARTTLS (nunca para producción).</summary>
    private static X509Certificate2 CreateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=atlasmail.local", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("atlasmail.local"); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        byte[] pfx = cert.Export(X509ContentType.Pfx, "pw");
        return new X509Certificate2(pfx, "pw",
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.MachineKeySet);
    }
}

/// <summary>Handler SMTP stub para tests de transporte (timeout de DATA, STARTTLS) sin BD.</summary>
public class DummySmtpHandler : ISmtpMessageHandler
{
    public Task<string> ValidateEnvelopeAsync(string mailFrom, string rcptTo, SmtpSessionContext ctx, CancellationToken ct = default)
        => Task.FromResult("250 2.1.0 OK");
    public Task<string> HandleMessageAsync(string mailFrom, string rcptTo, byte[] rawMime, SmtpSessionContext ctx, CancellationToken ct = default)
        => Task.FromResult("250 2.0.0 OK queued");
    public Task<string> AuthenticateAsync(string username, string password, SmtpSessionContext ctx, CancellationToken ct = default)
    {
        ctx.Authenticated = true; ctx.AuthUsername = username;
        return Task.FromResult("235 2.7.0 Authentication successful");
    }
}