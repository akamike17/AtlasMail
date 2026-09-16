using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Protocols.Imap;

/// <summary>
/// Opciones del servidor IMAP (spec §10). Configurable por entorno.
/// </summary>
public sealed class ImapServerOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 143;
    public string Hostname { get; set; } = "atlasmail.local";
    public int MaxMessageBytes { get; set; } = 50 * 1024 * 1024;
    public int MaxConnectionsPerIp { get; set; } = 20;
    public int Backlog { get; set; } = 100;
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(10);
    /// <summary>Capacidad literal {N} max de un comando (para buffers grandes de FETCH/APPEND).</summary>
    public int MaxLiteral { get; set; } = 50 * 1024 * 1024;
}