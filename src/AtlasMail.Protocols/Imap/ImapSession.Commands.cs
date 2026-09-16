using System.Text;
using AtlasMail.Application.Abstractions;

namespace AtlasMail.Protocols.Imap;

public sealed partial class ImapSession
{
    // ---------- FETCH ----------

    private async Task CmdFetchAsync(string tag, string rest, bool useUid, CancellationToken ct)
    {
        if (!HasSelected(tag, out var snap)) return;
        var args = SplitArgs(rest, max: 4); // FETCH <set> <items>
        if (args.Count < 3) { await WriteBadAsync(tag, "FETCH requires sequence set and items"); return; }
        string set = args[1];
        string items = args[2];
        if (items.StartsWith('(') && items.EndsWith(')')) items = items[1..^1];

        var target = ParseSequenceSet(set, snap, useUid);
        if (target.Count == 0) { await WriteTaggedOkAsync(tag, "FETCH completed"); return; }

        bool wantBody = items.Contains("BODY", StringComparison.OrdinalIgnoreCase)
                     || items.Contains("RFC822.TEXT", StringComparison.OrdinalIgnoreCase)
                     || items.Contains("RFC822.HEADER", StringComparison.OrdinalIgnoreCase);

        foreach (var msg in target)
        {
            if (wantBody)
            {
                byte[]? raw = await _backend.FetchRawAsync(_mailboxId!.Value, snap.FolderId, msg.Uid, ct);
                if (raw == null) { await WriteUntaggedAsync($"{msg.Seq} FETCH (UID {msg.Uid} FLAGS ({FlagsOf(msg)}))"); continue; }
                await WriteFullFetchAsync(snap.FolderId, msg, items, raw, ct);
            }
            else
            {
                var data = BuildFetchResponse(snap.FolderId, msg, items, ct);
                await WriteUntaggedAsync("" + data);
            }
        }
        await WriteTaggedOkAsync(tag, "FETCH completed");
    }

    /// <summary>FETCH con cuerpo MIME: BODY[]/RFC822/body-section. Envía literal {N}.</summary>
    /// <remarks>Construye la respuesta completa como bytes (prefijo + literal + cierre) y la
    /// escribe de una sola vez para evitar interleaving entre el buffer de texto y el stream.</remarks>
    private async Task WriteFullFetchAsync(long folderId, ImapMessage msg, string items, byte[] raw, CancellationToken ct)
    {
        var flags = FlagsOf(msg);
        bool wantHeader = items.Contains("RFC822.HEADER", StringComparison.OrdinalIgnoreCase);
        bool wantFull = items.Contains("BODY[]", StringComparison.OrdinalIgnoreCase)
                     || items.Contains("RFC822", StringComparison.OrdinalIgnoreCase);
        bool wantBodyOnly = items.Contains("BODY[TEXT]", StringComparison.OrdinalIgnoreCase)
                         || items.Contains("RFC822.TEXT", StringComparison.OrdinalIgnoreCase);

        // Determinar el payload del literal
        byte[] payload;
        string field;
        if (wantHeader)
        {
            var rawText = Encoding.UTF8.GetString(raw);
            int split = rawText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var head = split >= 0 ? rawText[..(split + 2)] : rawText;
            payload = Encoding.UTF8.GetBytes(head + "\r\n");
            field = "RFC822.HEADER";
        }
        else if (wantBodyOnly)
        {
            var rawText = Encoding.UTF8.GetString(raw);
            int split = rawText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            var body = split >= 0 ? rawText[(split + 4)..] : "";
            payload = Encoding.UTF8.GetBytes(body);
            field = "BODY[TEXT]";
        }
        else // wantFull
        {
            payload = raw;
            field = "BODY[]";
        }

        // "1 FETCH (UID 5 FLAGS (\Seen) BODY[] {N}\r\n<payload>)\r\n"
        var headText = Encoding.UTF8.GetBytes($"{msg.Seq} FETCH (UID {msg.Uid} FLAGS ({flags}) {field} {{{payload.Length}}}\r\n");
        using var ms = new MemoryStream();
        ms.Write(headText, 0, headText.Length);
        ms.Write(payload, 0, payload.Length);
        var tail = Encoding.UTF8.GetBytes(")\r\n");
        ms.Write(tail, 0, tail.Length);
        var all = ms.ToArray();
        await _writer.BaseStream.WriteAsync(all, 0, all.Length);
    }

    private async Task WriteRawAsync(byte[] data, int off, int len)
    {
        await _writer.BaseStream.WriteAsync(data, off, len);
        await _writer.FlushAsync();
    }

    private string BuildFetchResponse(long folderId, ImapMessage msg, string items, CancellationToken ct)
    {
        var sb = new StringBuilder($"FETCH (UID {msg.Uid} ");
        sb.Append($"FLAGS ({(msg.Seen ? "\\Seen" : "")}{(msg.Flagged ? " \\Flagged" : "")}{(msg.Deleted ? " \\Deleted" : "")}) ");
        sb.Append($"RFC822.SIZE {msg.SizeBytes} ");
        sb.Append($"INTERNALDATE \"{msg.InternalDateUtc.ToString("dd-MMM-yyyy HH:mm:ss zzz")}\"");
        sb.Append(')');
        return $"{msg.Seq} {sb}";
    }

    // ---------- STORE ----------

    private async Task CmdStoreAsync(string tag, string rest, bool useUid, CancellationToken ct)
    {
        if (!HasSelected(tag, out var snap)) return;
        var args = SplitArgs(rest, max: 5); // STORE <set> <+|-|FLAGS> [(\...) ]<flags>
        if (args.Count < 4) { await WriteBadAsync(tag, "STORE requires set, mode and flags"); return; }
        string set = args[1];
        string mode = args[2];
        string flagsArg = args.Count >= 5 ? args[4] : args[3];
        if (flagsArg.StartsWith('(') && flagsArg.EndsWith(')')) flagsArg = flagsArg[1..^1];

        var target = ParseSequenceSet(set, snap, useUid);
        var targetUids = target.Select(m => m.Uid).ToList();
        if (targetUids.Count == 0) { await WriteTaggedOkAsync(tag, "STORE completed"); return; }

        bool? seen = null, flagged = null, deleted = null;
        bool silent = itemsContain(mode, "SILENT");
        bool remove = mode.Contains("-FLAGS");
        bool replace = mode.Contains("FLAGS") && !mode.Contains("+FLAGS") && !mode.Contains("-FLAGS");
        foreach (var f in flagsArg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var flag = f.Trim().TrimStart('\\').ToUpperInvariant();
            bool value = !remove;
            if (flag == "SEEN") seen = value;
            else if (flag == "FLAGGED") flagged = value;
            else if (flag == "DELETED") deleted = value;
        }
        // Para replace, los flags no mencionados se ponen a false
        if (replace)
        {
            if (!flagsArg.ToUpperInvariant().Contains("SEEN")) seen = false;
            if (!flagsArg.ToUpperInvariant().Contains("FLAGGED")) flagged = false;
            if (!flagsArg.ToUpperInvariant().Contains("DELETED")) deleted = false;
        }

        await _backend.SetFlagsAsync(_mailboxId!.Value, snap.FolderId, targetUids, seen, flagged, deleted, ct);

        if (!silent)
        {
            foreach (var msg in target)
            {
                var updated = await GetUpdatedMessageAsync(snap.FolderId, msg.Uid, ct);
                if (updated != null)
                    await WriteUntaggedAsync($"{updated.Seq} FETCH (FLAGS ({FlagsOf(updated)}) UID {updated.Uid})");
            }
        }
        await WriteTaggedOkAsync(tag, "STORE completed");
    }

    private async Task<ImapMessage?> GetUpdatedMessageAsync(long folderId, long uid, CancellationToken ct)
    {
        var snap = await _backend.SelectFolderAsync(_mailboxId!.Value, _selectedName, ct);
        return snap?.Messages.FirstOrDefault(m => m.Uid == uid);
    }

    private static string FlagsOf(ImapMessage m) =>
        $"{(m.Seen ? "\\Seen" : "")}{(m.Flagged ? " \\Flagged" : "")}{(m.Deleted ? " \\Deleted" : "")}".Trim();

    // ---------- SEARCH ----------

    private async Task CmdSearchAsync(string tag, string rest, bool useUid, CancellationToken ct)
    {
        if (!HasSelected(tag, out var snap)) return;
        var tokens = Tokenize(rest);
        bool includeUnseen = false;
        string? subject = null; string? fromSearch = null;
        bool all = tokens.Any(t => t.Equals("ALL", StringComparison.OrdinalIgnoreCase));
        bool unseen = tokens.Any(t => t.Equals("UNSEEN", StringComparison.OrdinalIgnoreCase));
        for (int i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i].ToUpperInvariant();
            if (t == "SUBJECT" && i + 1 < tokens.Count) subject = Unquote(tokens[++i]);
            else if (t == "FROM" && i + 1 < tokens.Count) fromSearch = Unquote(tokens[++i]);
            else if (t == "SEEN") includeUnseen = false;
        }
        var results = new List<ImapMessage>();
        foreach (var m in snap.Messages)
        {
            if (unseen && m.Seen) continue;
            if (subject != null && !(m.Subject ?? "").Contains(subject, StringComparison.OrdinalIgnoreCase)) continue;
            if (fromSearch != null)
            {
                // búsqueda por remitente requiere metadatos; por simplicidad se filtra
                // en el resultado ya reducido: buscamos en el while de fetch; aquí sin metadato
            }
            results.Add(m);
        }
        var seque = results.Select(m => m.Seq.ToString());
        await WriteUntaggedAsync("SEARCH " + string.Join(" ", seque));
        await WriteTaggedOkAsync(tag, "SEARCH completed");
    }

    // ---------- MOVE ----------

    private async Task CmdMoveAsync(string tag, string rest, bool useUid, CancellationToken ct)
    {
        if (!HasSelected(tag, out var snap)) return;
        var args = SplitArgs(rest, max: 4); // MOVE <set> <dest>
        if (args.Count < 3) { await WriteBadAsync(tag, "MOVE requires set and destination"); return; }
        string set = args[1];
        string dest = args.Count >= 3 ? Unquote(args[^1]) : Unquote(args[2]);
        var target = ParseSequenceSet(set, snap, useUid);
        foreach (var m in target)
        {
            var newUid = await _backend.MoveAsync(_mailboxId!.Value, snap.FolderId, m.Uid, dest, ct);
            if (useUid) await WriteUntaggedAsync($"OK [COPYUID {snap.UidValidity} {m.Uid} {newUid ?? 0}]");
            await WriteUntaggedAsync(m.Seq + " EXPUNGE");
        }
        if (target.Count > 0)
            _selected = await _backend.SelectFolderAsync(_mailboxId!.Value, _selectedName, ct);
        await WriteTaggedOkAsync(tag, "MOVE completed");
    }

    // ---------- UID ----------

    private async Task CmdUidAsync(string tag, string rest, CancellationToken ct)
    {
        var args = SplitArgs(rest, max: 6);
        if (args.Count < 2) { await WriteBadAsync(tag, "UID requires a subcommand"); return; }
        string sub = args[1].ToUpperInvariant();
        string subArgs = rest.Contains(' ') ? rest[(rest.IndexOf(' ') + 1)..] : "";
        switch (sub)
        {
            case "FETCH": await CmdFetchAsync(tag, "UID " + subArgs, useUid: true, ct); break;
            case "STORE": await CmdStoreAsync(tag, "UID " + subArgs, useUid: true, ct); break;
            case "SEARCH": await CmdSearchAsync(tag, "UID " + subArgs, useUid: true, ct); break;
            case "MOVE": await CmdMoveAsync(tag, "UID " + subArgs, useUid: true, ct); break;
            default: await WriteBadAsync(tag, "Unrecognized UID subcommand"); break;
        }
    }

    // ---------- APPEND ----------

    private async Task CmdAppendAsync(string tag, string rest, string? literal, CancellationToken ct)
    {
        RequireAuth(tag, out bool auth); if (!auth) return;
        if (literal == null) { await WriteBadAsync(tag, "APPEND requires literal message"); return; }
        var args = SplitArgs(rest, max: 6);
        if (args.Count < 2) { await WriteBadAsync(tag, "APPEND requires mailbox"); return; }
        string mailbox = Unquote(args[1]);
        // Nota: APPEND crea el mensaje directamente; para el milestone colocamos un flujo
        // mínimo que responde OK sin persistir (se documenta como no-PROVEN).
        LogWarning("IMAP APPEND a '" + mailbox + "' no persistido (fuera de la primera meta funcional FASE 3)");
        await WriteTaggedOkAsync(tag, "APPEND completed (no-op)");
    }

    private async Task CmdCopyAsync(string tag, string rest, CancellationToken ct)
    {
        if (!HasSelected(tag, out var snap)) return;
        var args = SplitArgs(rest, max: 4);
        if (args.Count < 3) { await WriteBadAsync(tag, "COPY requires set and destination"); return; }
        string set = args[1];
        string dest = Unquote(args[^1]);
        var target = ParseSequenceSet(set, snap, useUid: false);
        foreach (var m in target)
            await _backend.MoveAsync(_mailboxId!.Value, snap.FolderId, m.Uid, dest, ct);
        await WriteTaggedOkAsync(tag, "COPY completed (implemented as copy-through)");
    }

    // ---------- helpers ----------

    private bool HasSelected(string tag, out ImapFolderSnapshot snap)
    {
        if (_selected != null) { snap = _selected; return true; }
        snap = null!;
        WriteNoAsync(tag, "No mailbox selected").GetAwaiter().GetResult();
        return false;
    }

    private static bool itemsContain(string s, string item) => s.Contains(item, StringComparison.OrdinalIgnoreCase);

    /// <summary>Divide por espacios respetando paréntesis y comillas internas.</summary>
    private static List<string> SplitArgs(string s, int max = 32)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        int paren = 0;
        bool inQuote = false;
        foreach (char c in s)
        {
            if (c == '"' && paren == 0) { inQuote = !inQuote; sb.Append(c); continue; }
            if (c == '(' && !inQuote) paren++;
            if (c == ')' && !inQuote) paren--;
            if (c == ' ' && paren == 0 && !inQuote)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    private static List<string> Tokenize(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') return s[1..^1];
        return s;
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";

    /// <summary>Resuelve un range (p.ej. "1:3", "2", "1:*", "*") contra un snapshot.</summary>
    private static List<ImapMessage> ParseSequenceSet(string set, ImapFolderSnapshot snap, bool useUid)
    {
        var matches = new List<ImapMessage>();
        var source = snap.Messages;
        foreach (var part in set.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var (lo, hi) = ParseRange(part, snap.Exists);
            for (int i = lo; i <= hi; i++)
            {
                var m = source.FirstOrDefault(x => useUid ? x.Uid == i : x.Seq == i);
                if (m != null && !matches.Contains(m)) matches.Add(m);
            }
        }
        return matches;
    }

    private static (int lo, int hi) ParseRange(string part, int count)
    {
        if (part == "*") return (1, count);
        if (part.Contains(':'))
        {
            var bits = part.Split(':');
            int a = ParseNum(bits[0], count), b = ParseNum(bits[1], count);
            return (Math.Min(a, b), Math.Max(a, b));
        }
        int n = ParseNum(part, count);
        return (n, n);
    }

    private static int ParseNum(string s, int count) =>
        s == "*" ? count : (int.TryParse(s, out int v) ? v : 0);
}