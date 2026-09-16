using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using AtlasMail.Protocols.Imap;
using Microsoft.Extensions.DependencyInjection;

namespace AtlasMail.IntegrationTests;

/// <summary>
/// FASE 3 — IMAP E2E real (spec §10, §44): levanta el servidor IMAP real sobre la BD
/// temporal del factory, y conduce una sesión con un cliente IMAP real por socket:
/// LOGIN → LIST → SELECT → FETCH → STORE (\Seen) → MOVE → EXPUNGE/verificación.
/// La compatibilidad con Thunderbird/etc. SOLO se declarará PROVEN tras prueba real
/// (§44); esto prueba el protocolo contra nuestra implementación real.
/// </summary>
public class ImapE2ETests : IAsyncLifetime
{
    private readonly AtlasMailFactory _factory;
    private ImapServer? _server;

    public ImapE2ETests() => _factory = new AtlasMailFactory();
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        if (_server != null) await _server.DisposeAsync();
        _factory.Dispose();
    }

    private static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    private async Task<(int Port, long DomainId)> SeedAsync()
    {
        var admin = await _factory.CreateAdminClientAsync();
        var d = await (await admin.PostAsJsonAsync("/api/admin/domain", new { name = "imap.local" })).Content.ReadAsStringAsync();
        long domainId = Body(d).GetProperty("id").GetInt64();
        await admin.PostAsJsonAsync("/api/admin/mailbox", new { domainId, localPart = "imapuser", displayName = "Imap", password = "Atl4smail1!" });
        await admin.PostAsJsonAsync("/api/admin/user", new { username = "imapuser", password = "Atl4smail1!", displayName = "Imap", role = 0, domainId });

        // sembrar un mensaje en el buzón vía compose (imapuser -> imapuser)
        var user = _factory.CreateClient();
        var login = await user.PostAsJsonAsync("/api/auth/login", new { username = "imapuser", password = "Atl4smail1!" });
        login.EnsureSuccessStatusCode();
        await user.PostAsJsonAsync("/api/mail/compose", new
        {
            fromUsername = "imapuser",
            to = new[] { "imapuser@imap.local" },
            cc = Array.Empty<string>(),
            subject = "Mensaje IMAP E2E",
            body = "Hola IMAP desde el cliente real.",
            attachments = Array.Empty<object>()
        });

        // levantar servidor IMAP real en puerto efímero
        var scopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();
        _server = new ImapServer(scopeFactory, new ImapServerOptions { Port = 0, Hostname = "atlasmail.local" });
        _server.Start();
        return (_server.EffectivePort, domainId);
    }

    [Fact]
    public async Task Sesion_imap_completa_login_select_fetch_flags_move()
    {
        var (port, domainId) = await SeedAsync();

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
        client.ReceiveTimeout = 5000;
        using var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, Encoding.UTF8) { NewLine = "\r\n", AutoFlush = true };

        string? greeting = await ImapReadLineAsync(reader);
        greeting.Should().Contain("OK");

        // LOGIN
        await writer.WriteLineAsync("A1 LOGIN imapuser@imap.local Atl4smail1!");
        string loginResp = await ImapReadLineAsync(reader);
        loginResp.Should().StartWith("A1 OK");

        // LIST
        await writer.WriteLineAsync("A2 LIST \"\" \"*\"");
        var listResp = await ImapReadUntilTaggedAsync(reader, "A2");
        listResp.Should().Contain(x => x.Contains("Inbox"));
        listResp.Should().Contain(x => x.Contains("Sent"));
        listResp.Should().Contain(x => x.StartsWith("A2 OK"));

        // SELECT Inbox
        await writer.WriteLineAsync("A3 SELECT Inbox");
        var selResp = await ImapReadUntilTaggedAsync(reader, "A3");
        selResp.Should().Contain(x => x.Contains("EXISTS"));
        selResp.Should().Contain(x => x.Contains("UIDVALIDITY"));
        selResp.Should().Contain(x => x.StartsWith("A3 OK"));

        // FETCH 1 (FLAGS UID RFC822.SIZE)
        await writer.WriteLineAsync("A4 FETCH 1 (FLAGS UID RFC822.SIZE INTERNALDATE)");
        string f1 = await ImapReadLineAsync(reader);
        string fDone = await ImapReadLineAsync(reader);
        f1.Should().Contain("FETCH");
        f1.Should().Contain("UID 1");
        fDone.Should().StartWith("A4 OK");

        // FETCH BODY[] — obtener el mensaje completo (literal por líneas del reader)
        await writer.WriteLineAsync("A5 FETCH 1 (BODY[])");
        var bodyLines = new List<string>();
        while (true)
        {
            string? raw = await reader.ReadLineAsync();
            if (raw == null) break; // EOF real
            bodyLines.Add(raw);
            if (raw.StartsWith("A5 ")) break;
        }
        bodyLines.Should().Contain(x => x.Contains("BODY[]"));
        bodyLines.Should().Contain(x => x.Contains("Subject: Mensaje IMAP E2E"));
        bodyLines.Should().Contain(x => x.StartsWith("A5 OK"));

        // STORE 1 +FLAGS.SILENT \Seen
        await writer.WriteLineAsync("A6 STORE 1 +FLAGS.SILENT \\Seen");
        string storeDone = await ImapReadLineAsync(reader);
        storeDone.Should().StartWith("A6 OK");

        // MOVE 1 Trash
        await writer.WriteLineAsync("A7 MOVE 1 Trash");
        string m1 = await ImapReadLineAsync(reader); // EXPUNGE
        string mDone = await ImapReadLineAsync(reader);
        mDone.Should().StartWith("A7 OK");

        // LOGOUT
        await writer.WriteLineAsync("A8 LOGOUT");
        string bye = await ImapReadLineAsync(reader);
        string outResp = await ImapReadLineAsync(reader);
        bye.Should().StartWith("* BYE");
        outResp.Should().StartWith("A8 OK");
    }

    [Fact]
    public async Task Login_invalido_rechazado()
    {
        var (port, _) = await SeedAsync();
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(5));
        client.ReceiveTimeout = 5000;
        using var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8);
        var writer = new StreamWriter(stream, Encoding.UTF8) { NewLine = "\r\n", AutoFlush = true };

        await ImapReadLineAsync(reader); // greeting
        await writer.WriteLineAsync("B1 LOGIN imapuser@imap.local WrongPass1!");
        string resp = await ImapReadLineAsync(reader);
        resp.Should().StartWith("B1 NO");
    }

    private static async Task<string> ImapReadLineAsync(StreamReader reader)
    {
        string? line = await reader.ReadLineAsync();
        return line ?? string.Empty;
    }

    /// <summary>Lee respuestas hasta encontrar la etiquetada con el tag dado.</summary>
    private static async Task<List<string>> ImapReadUntilTaggedAsync(StreamReader reader, string tag)
    {
        var lines = new List<string>();
        while (true)
        {
            string line = await ImapReadLineAsync(reader);
            if (string.IsNullOrEmpty(line)) break;
            lines.Add(line);
            if (line.StartsWith(tag + " ")) break;
        }
        return lines;
    }
}