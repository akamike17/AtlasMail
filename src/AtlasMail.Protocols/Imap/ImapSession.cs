using System.Text;
using AtlasMail.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace AtlasMail.Protocols.Imap;

/// <summary>
/// Estado y ejecución de una sesión IMAP (RFC 3501). Retiene el estado de autenticación
/// y la carpeta seleccionada. Cada conexión tiene su propio ImapSession.
/// Nota: este archivo define la clase ImapSession con sus utilidades de parsing; la clase
/// se reparte en archivos parciales (ImapSession.Commands.cs) por claridad.
/// </summary>
public sealed partial class ImapSession
{
    private readonly IMailboxBackend _backend;
    private StreamWriter _writer;
    private readonly ImapServerOptions _options;
    private readonly ILogger _logger;

    private long? _mailboxId;
    private string? _email;
    private ImapFolderSnapshot? _selected;
    private string _selectedName = string.Empty;

    /// <summary>True cuando la sesión va cifrada (tras STARTTLS). Lo usa la puerta TLS de LOGIN.</summary>
    public bool IsTls { get; set; }

    public ImapSession(IMailboxBackend backend, StreamWriter writer, ImapServerOptions options, ILogger logger)
    {
        _backend = backend; _writer = writer; _options = options; _logger = logger;
    }

    /// <summary>Reemplaza el transporte tras STARTTLS (el nuevo StreamWriter va sobre el SslStream).</summary>
    public void SetWriter(StreamWriter writer) => _writer = writer;

    private void LogWarning(string message) =>
        _logger.Log(LogLevel.Warning, 0, message, null, (s, _) => (string)s!);
    private void LogInformation(string message) =>
        _logger.Log(LogLevel.Information, 0, message, null, (s, _) => (string)s!);

    /// <summary>Ejecuta un comando IMAP. Devuelve true si la conexión debe cerrarse (LOGOUT).</summary>
    public async Task<bool> ExecuteAsync(string rawLine, string? literalContinuation, CancellationToken ct)
    {
        string line = rawLine.TrimEnd('\r');
        if (line.Length == 0) return false;

        int sp = line.IndexOf(' ');
        string tag, rest;
        if (sp < 0) { tag = line; rest = string.Empty; }
        else { tag = line[..sp]; rest = line[(sp + 1)..].Trim(); }

        string upper = rest.ToUpperInvariant();

        try
        {
            if (upper == "CAPABILITY") await CmdCapabilityAsync(tag, ct);
            else if (upper == "NOOP") await CmdNoopAsync(tag, ct);
            else if (upper == "LOGOUT") { await CmdLogoutAsync(tag); return true; }
            else if (upper == "LOGIN" || upper.StartsWith("LOGIN ")) await CmdLoginAsync(tag, rest, ct);
            else if (upper == "LIST" || upper.StartsWith("LIST ")) await CmdListAsync(tag, rest, ct);
            else if (upper == "LSUB" || upper.StartsWith("LSUB ")) await CmdListAsync(tag, rest, ct);
            else if (upper == "SELECT" || upper.StartsWith("SELECT ")) await CmdSelectAsync(tag, rest, false, ct);
            else if (upper == "EXAMINE" || upper.StartsWith("EXAMINE ")) await CmdSelectAsync(tag, rest, true, ct);
            else if (upper == "STATUS" || upper.StartsWith("STATUS ")) await CmdStatusAsync(tag, rest, ct);
            else if (upper == "CLOSE") await CmdCloseAsync(tag, ct);
            else if (upper == "EXPUNGE") await CmdExpungeAsync(tag, ct);
            else if (upper == "UID" || upper.StartsWith("UID ")) await CmdUidAsync(tag, rest, ct);
            else if (upper == "FETCH" || upper.StartsWith("FETCH ")) await CmdFetchAsync(tag, rest, useUid: false, ct);
            else if (upper == "STORE" || upper.StartsWith("STORE ")) await CmdStoreAsync(tag, rest, false, ct);
            else if (upper == "SEARCH" || upper.StartsWith("SEARCH ")) await CmdSearchAsync(tag, rest, false, ct);
            else if (upper == "MOVE" || upper.StartsWith("MOVE ")) await CmdMoveAsync(tag, rest, false, ct);
            else if (upper == "APPEND" || upper.StartsWith("APPEND ")) await CmdAppendAsync(tag, rest, literalContinuation, ct);
            else if (upper == "COPY" || upper.StartsWith("COPY ")) await CmdCopyAsync(tag, rest, ct);
            else await WriteBadAsync(tag, "Unknown command");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LogWarning("Error IMAP procesando '" + tag + " " + rest + "': " + ex.Message);
            await WriteRespAsync(tag, "NO", "Server error: " + Sanitize(ex.Message));
        }
        return false;
    }

    private static string Sanitize(string s) => s.Replace("\r", " ").Replace("\n", " ");

    private async Task WriteAsync(string text)
    {
        // Todo el output va directo al BaseStream (sin buffer intermedio) para que
        // los literales crudos y el texto etiquetado nunca se interleaven.
        var bytes = Encoding.UTF8.GetBytes(text.EndsWith("\r\n") ? text : text + "\r\n");
        await _writer.BaseStream.WriteAsync(bytes);
    }

    private Task WriteUntaggedAsync(string text) => WriteAsync("* " + text);
    private Task WriteRespAsync(string tag, string code, string text) => WriteAsync($"{tag} {code} {text}");
    private Task WriteOkAsync(string tag, string text) => WriteRespAsync(tag, "OK", text);
    private Task WriteNoAsync(string tag, string text) => WriteRespAsync(tag, "NO", text);
    private Task WriteBadAsync(string tag, string text) => WriteRespAsync(tag, "BAD", text);
    private Task WriteTaggedOkAsync(string tag, string? extra = null) =>
        WriteAsync($"{tag} OK {extra ?? "Completed"}");

    // ---------- comandos ----------

    private async Task CmdCapabilityAsync(string tag, CancellationToken ct)
    {
        string caps = "IMAP4rev1";
        if (_options.TlsCertificate != null && !IsTls) caps += " STARTTLS";
        await WriteUntaggedAsync("CAPABILITY " + caps);
        await WriteTaggedOkAsync(tag, "CAPABILITY completed");
    }

    private async Task CmdNoopAsync(string tag, CancellationToken ct) => await WriteTaggedOkAsync(tag, "NOOP completed");

    private async Task CmdLogoutAsync(string tag)
    {
        await WriteUntaggedAsync("BYE AtlasMail logging out");
        await WriteOkAsync(tag, "LOGOUT completed");
    }

    private async Task CmdLoginAsync(string tag, string rest, CancellationToken ct)
    {
        // §seguridad: si TLS es obligatorio, rechazar LOGIN en claro (los clientes deben negociar STARTTLS).
        if (_options.RequireTlsForLogin && !IsTls)
        {
            await WriteNoAsync(tag, "LOGIN must be issued after STARTTLS (TLS required)");
            return;
        }
        if (_mailboxId.HasValue) { await WriteNoAsync(tag, "Already authenticated"); return; }
        var args = SplitArgs(rest, max: 3); // LOGIN user pass
        if (args.Count < 3)
        {
            await WriteBadAsync(tag, "LOGIN requires username and password");
            return;
        }
        string user = Unquote(args[1]);
        string pass = Unquote(args[2]);

        var result = await _backend.AuthenticateAsync(user, pass, ct);
        if (result == null)
        {
            LogInformation("IMAP LOGIN falló para " + user);
            await WriteNoAsync(tag, "LOGIN failed");
            return;
        }
        _mailboxId = result.MailboxId; _email = result.EmailAddress;
        LogInformation("IMAP LOGIN OK " + _email);
        await WriteOkAsync(tag, "LOGIN completed");
    }

    private async Task CmdListAsync(string tag, string rest, CancellationToken ct)
    {
        RequireAuth(tag, out bool ok); if (!ok) return;
        // LIST ref name
        var args = SplitArgs(rest, max: 3);
        if (args.Count < 3) { await WriteBadAsync(tag, "LIST requires reference and mailbox name"); return; }
        string listName = Unquote(args[2]);
        var folders = await _backend.ListFoldersAsync(_mailboxId!.Value, ct);
        bool empty = listName == "" || listName == "*" || listName.Contains("%");
        // Devolver carpetas que coinciden; raíz ""
        if (empty)
        {
            foreach (var f in folders)
            {
                string flags = f.SystemName is null ? "\\HasNoChildren" : SpecialUseFlag(f.SystemName.Value);
                await WriteUntaggedAsync($"LIST ({flags}) \"/\" {Quote(f.Name)}");
            }
        }
        else
        {
            var match = folders.FirstOrDefault(f => f.Name.Equals(listName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                await WriteUntaggedAsync($"LIST () \"/\" {Quote(match.Name)}");
        }
        await WriteTaggedOkAsync(tag, "LIST completed");
    }

    private static string SpecialUseFlag(Domain.Enums.SystemFolder f) => f switch
    {
        Domain.Enums.SystemFolder.Sent => "\\Sent",
        Domain.Enums.SystemFolder.Drafts => "\\Drafts",
        Domain.Enums.SystemFolder.Trash => "\\Trash",
        Domain.Enums.SystemFolder.Spam => "\\Junk",
        Domain.Enums.SystemFolder.Archive => "\\Archive",
        _ => "\\HasNoChildren"
    };

    private async Task CmdSelectAsync(string tag, string rest, bool examine, CancellationToken ct)
    {
        RequireAuth(tag, out bool ok); if (!ok) return;
        var args = SplitArgs(rest, max: 3);
        if (args.Count < 2) { await WriteBadAsync(tag, "SELECT requires a mailbox"); return; }
        string name = Unquote(args[1]);
        // name tras SELECT/EXAMINE
        if (args.Count >= 3) name = Unquote(args[2]);

        var snap = await _backend.SelectFolderAsync(_mailboxId!.Value, name, ct);
        if (snap == null) { await WriteNoAsync(tag, "Mailbox not found: " + name); return; }

        _selected = snap; _selectedName = snap.Name;
        await WriteUntaggedAsync(snap.Exists + " EXISTS");
        await WriteUntaggedAsync(snap.Recent + " RECENT");
        if (snap.Unseen > 0) await WriteUntaggedAsync($"OK [UNSEEN {FirstUnseen(snap)}] Message {FirstUnseen(snap)} is first unseen");
        await WriteUntaggedAsync($"OK [UIDVALIDITY {snap.UidValidity}] UIDs valid");
        await WriteUntaggedAsync($"OK [UIDNEXT {NextUid(snap)}] Predicted next UID");
        await WriteUntaggedAsync($"FLAGS (\\Seen \\Flagged \\Deleted)");
        await WriteTaggedOkAsync(tag, (examine ? "EXAMINE" : "SELECT") + " completed");
    }

    private static long FirstUnseen(ImapFolderSnapshot s) => s.Messages.FirstOrDefault(m => !m.Seen)?.Seq ?? s.Exists + 1;
    private static long NextUid(ImapFolderSnapshot s) => s.Messages.Count == 0 ? 1 : s.Messages.Max(m => m.Uid) + 1;

    private async Task CmdStatusAsync(string tag, string rest, CancellationToken ct)
    {
        RequireAuth(tag, out bool ok); if (!ok) return;
        var args = SplitArgs(rest, max: 5);
        if (args.Count < 2) { await WriteBadAsync(tag, "STATUS requires mailbox"); return; }
        string name = Unquote(args[1]);
        var snap = await _backend.SelectFolderAsync(_mailboxId!.Value, name, ct);
        if (snap == null) { await WriteNoAsync(tag, "Mailbox not found"); return; }
        await WriteUntaggedAsync($"STATUS {Quote(name)} (MESSAGES {snap.Exists} UIDNEXT {NextUid(snap)} UIDVALIDITY {snap.UidValidity} UNSEEN {snap.Unseen})");
        await WriteTaggedOkAsync(tag, "STATUS completed");
    }

    private async Task CmdCloseAsync(string tag, CancellationToken ct)
    {
        if (_selected != null)
        {
            await _backend.ExpungeAsync(_mailboxId!.Value, _selected.FolderId, ct);
            _selected = null; _selectedName = string.Empty;
        }
        await WriteTaggedOkAsync(tag, "CLOSE completed");
    }

    private async Task CmdExpungeAsync(string tag, CancellationToken ct)
    {
        if (_selected == null) { await WriteNoAsync(tag, "No mailbox selected"); return; }
        var gone = _selected.Messages.Where(m => m.Deleted).Select(m => m.Seq).ToList();
        await _backend.ExpungeAsync(_mailboxId!.Value, _selected.FolderId, ct);
        foreach (var seq in gone) await WriteUntaggedAsync(seq + " EXPUNGE");
        // refrescar snapshot
        _selected = await _backend.SelectFolderAsync(_mailboxId!.Value, _selectedName, ct);
        await WriteTaggedOkAsync(tag, "EXPUNGE completed");
    }

    private void RequireAuth(string tag, out bool ok)
    {
        ok = _mailboxId.HasValue;
        if (!ok) WriteNoAsync(tag, "Not authenticated").GetAwaiter().GetResult();
    }
}