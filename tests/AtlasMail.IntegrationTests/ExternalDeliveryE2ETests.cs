using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Services;
using AtlasMail.Protocols.Smtp;

namespace AtlasMail.IntegrationTests;

/// <summary>
/// FASE 2 — Entrega externa E2E real (secciones 7, 8): levanta un servidor SMTP local
/// que actúa como el "MX remoto" de un dominio ficticio, y usa el SmtpClient de Protocols
/// (a través de SmtpExternalMailSender) + un FakeMxResolver que apunta al localhost en el
/// puerto del aceptador. Así se prueba el pipeline real completo (EHLO/MAIL/RCPT/DATA)
/// sin falsificar red ni declarar entrega a internet real (la resolución MX real se cubre
/// por unidad + health probe).
/// </summary>
public class ExternalDeliveryE2ETests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Entrega_externa_real_por_smtp_lllega_al_servidor_remoto()
    {
        const string remoteDomain = "remoto.example";
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 1) "MX remoto" local: aceptador SMTP mínimo real por socket.
        var (port, stop) = await StartTestSmtpAcceptorAsync(received);

        try
        {
            // 2) Fake MX resolver -> localhost:<aceptador> y cliente SMTP REAL de Protocols.
            var mx = new FakeMxResolver();
            mx.Add(remoteDomain, new MailExchange("127.0.0.1", 0, port));
            var sender = new SmtpExternalMailSender();
            var svc = new ExternalDeliveryService(mx, sender, new StubPolicy(true),
                new ExternalDeliverySettings(StartTlsRequiredForExternal: false, HeloName: "atlasmail.local"));

            var mime = Encoding.UTF8.GetBytes("Subject: FASE2 real\r\nDate: " + DateTime.UtcNow.ToString("R") + "\r\n\r\nMensaje de prueba.\r\n");
            var outcome = await svc.DeliverAsync("alice@atlas.local", "bob@" + remoteDomain, mime);

            var completed = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

            outcome.Delivered.Should().BeTrue($"outcome resp: {outcome.RemoteResponse}");
            completed.Should().BeTrue("el servidor remoto debe haber recibido el mensaje");
        }
        finally
        {
            await stop();
        }
    }

    private static async Task<(int Port, Func<Task> Stop)> StartTestSmtpAcceptorAsync(TaskCompletionSource<bool> received)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var task = Task.Run(async () =>
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                client.ReceiveTimeout = 10000; client.SendTimeout = 10000;
                using var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 mx.remoto.example ESMTP test");
                bool inData = false;
                while (client.Connected)
                {
                    string? line = await reader.ReadLineAsync();
                    if (line == null) break;
                    string upper = line.TrimEnd('\r').ToUpperInvariant();
                    if (inData)
                    {
                        if (line.StartsWith(".")) { await writer.WriteLineAsync("250 2.0.0 Ok: queued"); inData = false; }
                        continue;
                    }
                    if (upper == "QUIT") { await writer.WriteLineAsync("221 Bye"); break; }
                    else if (upper.StartsWith("EHLO") || upper.StartsWith("HELO"))
                    {
                        await writer.WriteLineAsync("250-mx.remoto.example");
                        await writer.WriteLineAsync("250 8BITMIME");
                    }
                    else if (upper.StartsWith("MAIL FROM") || upper.StartsWith("RCPT TO")) await writer.WriteLineAsync("250 2.1.0 Ok");
                    else if (upper == "DATA") { await writer.WriteLineAsync("354 End data"); inData = true; }
                    else await writer.WriteLineAsync("500 Error");
                }
                received.TrySetResult(true);
            }
            catch { received.TrySetResult(false); }
            finally { listener.Stop(); }
        });
        return (port, async () => { try { await task; } catch { /* ya cerrado */ } });
    }
}

/// <summary>Fake MX resolver simple para tests.</summary>
public sealed class FakeMxResolver : IMxResolver
{
    private readonly Dictionary<string, IReadOnlyList<MailExchange>> _map = new();
    public void Add(string domain, params MailExchange[] mxs) => _map[domain.ToLowerInvariant()] = mxs;
    public Task<IReadOnlyList<MailExchange>> ResolveAsync(string domainName, CancellationToken ct = default)
        => Task.FromResult(_map.TryGetValue(domainName.ToLowerInvariant(), out var m) ? m : Array.Empty<MailExchange>());
}

public sealed class StubPolicy : IExternalDeliveryPolicy
{
    private readonly bool _allow;
    public StubPolicy(bool allow) => _allow = allow;
    public Task<bool> AllowDomainExternalSendAsync(string domainName, CancellationToken ct = default) => Task.FromResult(_allow);
}