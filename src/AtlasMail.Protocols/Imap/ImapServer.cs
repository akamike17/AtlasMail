using System.Net;
using System.Net.Sockets;
using System.Text;
using AtlasMail.Application.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Protocols.Imap;

/// <summary>
/// Servidor IMAP4rev1 (RFC 3501) incremental (spec §10). State machine multihilo por
/// conexión: NO AUTH → AUTH → SELECTED. Comandos soportados para la primera meta
/// funcional: CAPABILITY, NOOP, LOGIN, LOGOUT, LIST, LSUB, SELECT, EXAMINE, STATUS,
/// FETCH (flags, uid, tamaño, INTERNALDATE, BODY.PEEK, RFC822, ENVELOPE), STORE
/// (flags, incluido \Seen/\Flagged/\Deleted), SEARCH (básico), MOVE, EXPUNGE, CLOSE, UID.
/// Desacoplado del almacenamiento: opera contra IMailboxBackend.
/// No se declara compatibilidad IMAP completa fuera del conjunto probado.
/// </summary>
public sealed class ImapServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly Func<IMailboxBackend>? _backendFactory;
    private readonly ImapServerOptions _options;
    private readonly ILogger<ImapServer> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = new();
    private readonly object _lock = new();

    public ImapServer(Func<IMailboxBackend> backendFactory, ImapServerOptions? options = null, ILogger<ImapServer>? logger = null)
    {
        _backendFactory = backendFactory;
        _options = options ?? new ImapServerOptions();
        _logger = logger ?? NullLogger<ImapServer>.Instance;
        _listener = new TcpListener(IPAddress.Any, _options.Port);
    }

    public ImapServer(IServiceScopeFactory scopeFactory, ImapServerOptions? options = null, ILogger<ImapServer>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _options = options ?? new ImapServerOptions();
        _logger = logger ?? NullLogger<ImapServer>.Instance;
        _listener = new TcpListener(IPAddress.Any, _options.Port);
    }

    public int EffectivePort => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public bool Running { get; private set; }

    public void Start()
    {
        if (!_options.Enabled) { Running = false; return; }
        _listener.Start(_options.Backlog);
        Running = true;
        _logger.LogInformation("Servidor IMAP escuchando en puerto {Port}", _options.Port);
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var task = Task.Run(() => HandleClientAsync(client, _cts.Token));
                lock (_lock) { _connections.Add(task); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "Error aceptando conexión IMAP"); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        IServiceScope? scope = null;
        try
        {
            client.NoDelay = true;
            client.ReceiveTimeout = (int)_options.CommandTimeout.TotalMilliseconds;
            using var stream = client.GetStream();
            // UTF8 SIN BOM: un Byte-Order-Mark al inicio rompe la negociación con clientes IMAP reales.
            var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var reader = new StreamReader(stream, utf8NoBom);
            var writer = new StreamWriter(stream, utf8NoBom) { NewLine = "\r\n", AutoFlush = true };

            scope = _scopeFactory?.CreateScope();
            var backend = scope != null
                ? scope.ServiceProvider.GetRequiredService<IMailboxBackend>()
                : _backendFactory!();

            var session = new ImapSession(backend, writer, _options, _logger);

            await writer.WriteLineAsync($"* OK [{_options.Hostname}] AtlasMail IMAP4rev1 ready");

            while (!token.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(token); }
                catch (OperationCanceledException) { break; }
                catch (Exception) { break; }
                if (line == null) break;

                // Manejo literal {N}\r\n: leer N bytes extra literalmente
                string? continuation = null;
                int litIdx = line.IndexOf("{", StringComparison.Ordinal);
                if (litIdx >= 0 && line.EndsWith("}", StringComparison.Ordinal))
                {
                    var numStr = line[(litIdx + 1)..^1];
                    if (int.TryParse(numStr, out int n) && n <= _options.MaxLiteral)
                    {
                        await writer.WriteAsync("+ OK\r\n");
                        var literal = await ReadExactlyAsync(stream, n, token);
                        continuation = Encoding.UTF8.GetString(literal, 0, literal.Length);
                    }
                }

                var done = await session.ExecuteAsync(line, continuation, token);
                if (done) break;
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Conexión IMAP cerrada por {Ip}", client.Client.RemoteEndPoint); }
        finally
        {
            scope?.Dispose();
            client.Close();
        }
    }

    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(offset, count - offset), ct);
            if (n <= 0) break;
            offset += n;
        }
        return buf;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener.Stop(); } catch (SocketException) { }
        try { await Task.WhenAll(_connections); } catch { }
        _cts.Dispose();
    }
}