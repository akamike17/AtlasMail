using System.Text;
using AtlasMail.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Application.Services;

public sealed record ContactDto(long Id, string Name, string Email, string? Phone, string? Company, string? Notes);

public sealed record ContactImportResult(int Imported, int Skipped);

public interface IContactService
{
    Task<IReadOnlyList<ContactDto>> ListPersonalAsync(long mailboxId, CancellationToken ct = default);
    Task<IReadOnlyList<ContactDto>> ListDomainAsync(long domainId, CancellationToken ct = default);
    Task<ContactDto> CreatePersonalAsync(long mailboxId, ContactInput c, CancellationToken ct = default);
    Task<ContactDto?> UpdateAsync(long mailboxId, long contactId, ContactInput c, CancellationToken ct = default);
    Task<bool> DeleteAsync(long mailboxId, long contactId, CancellationToken ct = default);
    Task<string> ExportVcfAsync(long mailboxId, CancellationToken ct = default);
    Task<string> ExportCsvAsync(long mailboxId, CancellationToken ct = default);
    Task<ContactImportResult> ImportVcfAsync(long mailboxId, string vcf, CancellationToken ct = default);
    Task<ContactImportResult> ImportCsvAsync(long mailboxId, string csv, CancellationToken ct = default);
}

public sealed record ContactInput(string Name, string Email, string? Phone, string? Company, string? Notes);

/// <summary>Contactos personales de un buzón, con import/export VCARD (RFC 6350) y CSV.</summary>
public class ContactService : IContactService
{
    private readonly IApplicationDbContext _db;
    public ContactService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<ContactDto>> ListPersonalAsync(long mailboxId, CancellationToken ct = default)
        => await _db.Contacts.AsNoTracking()
            .Where(c => c.OwnerMailboxId == mailboxId)
            .OrderBy(c => c.Name).ThenBy(c => c.Email)
            .Select(c => new ContactDto(c.Id, c.Name, c.Email, c.Phone, c.Company, c.Notes))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ContactDto>> ListDomainAsync(long domainId, CancellationToken ct = default)
        => await _db.Contacts.AsNoTracking()
            .Where(c => c.DomainId == domainId)
            .OrderBy(c => c.Name).ThenBy(c => c.Email)
            .Select(c => new ContactDto(c.Id, c.Name, c.Email, c.Phone, c.Company, c.Notes))
            .ToListAsync(ct);

    public async Task<ContactDto> CreatePersonalAsync(long mailboxId, ContactInput c, CancellationToken ct = default)
    {
        var contact = new Domain.Entities.Contact
        {
            OwnerMailboxId = mailboxId,
            Name = c.Name, Email = c.Email, Phone = c.Phone, Company = c.Company, Notes = c.Notes
        };
        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync(ct);
        return ToDto(contact);
    }

    public async Task<ContactDto?> UpdateAsync(long mailboxId, long contactId, ContactInput c, CancellationToken ct = default)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId && x.OwnerMailboxId == mailboxId, ct);
        if (contact == null) return null;
        contact.Name = c.Name; contact.Email = c.Email; contact.Phone = c.Phone; contact.Company = c.Company; contact.Notes = c.Notes;
        await _db.SaveChangesAsync(ct);
        return ToDto(contact);
    }

    public async Task<bool> DeleteAsync(long mailboxId, long contactId, CancellationToken ct = default)
    {
        var contact = await _db.Contacts.FirstOrDefaultAsync(x => x.Id == contactId && x.OwnerMailboxId == mailboxId, ct);
        if (contact == null) return false;
        _db.Contacts.Remove(contact);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<string> ExportVcfAsync(long mailboxId, CancellationToken ct = default)
    {
        var contacts = await ListPersonalAsync(mailboxId, ct);
        var sb = new StringBuilder();
        foreach (var c in contacts)
        {
            sb.AppendLine("BEGIN:VCARD");
            sb.AppendLine("VERSION:3.0");
            sb.AppendLine("FN:" + Fold(c.Name));
            sb.AppendLine("EMAIL:" + c.Email);
            if (!string.IsNullOrWhiteSpace(c.Phone)) sb.AppendLine("TEL:" + c.Phone);
            if (!string.IsNullOrWhiteSpace(c.Company)) sb.AppendLine("ORG:" + Fold(c.Company));
            if (!string.IsNullOrWhiteSpace(c.Notes)) sb.AppendLine("NOTE:" + Fold(c.Notes));
            sb.AppendLine("END:VCARD");
        }
        return sb.ToString();
    }

    public async Task<string> ExportCsvAsync(long mailboxId, CancellationToken ct = default)
    {
        var contacts = await ListPersonalAsync(mailboxId, ct);
        var sb = new StringBuilder();
        sb.AppendLine("Name,Email,Phone,Company,Notes");
        foreach (var c in contacts)
            sb.AppendLine(string.Join(",", new[] { c.Name, c.Email, c.Phone ?? "", c.Company ?? "", c.Notes ?? "" }.Select(CsvEscape)));
        return sb.ToString();
    }

    public async Task<ContactImportResult> ImportVcfAsync(long mailboxId, string vcf, CancellationToken ct = default)
    {
        int imported = 0, skipped = 0;
        foreach (var block in SplitBlocks(vcf, "VCARD"))
        {
            var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in block.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.TrimEnd(); if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var keyName = line[..colon]; var value = line[(colon + 1)..];
                if (keyName.Contains(';')) keyName = keyName[..keyName.IndexOf(';')];
                props[keyName.Trim().ToUpperInvariant()] = value.Trim();
            }
            var email = props.GetValueOrDefault("EMAIL");
            var name = props.GetValueOrDefault("FN") ?? email ?? "(sin nombre)";
            if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(props.GetValueOrDefault("FN"))) { skipped++; continue; }
            _db.Contacts.Add(new Domain.Entities.Contact
            {
                OwnerMailboxId = mailboxId,
                Name = name, Email = email ?? "",
                Phone = props.GetValueOrDefault("TEL"),
                Company = props.GetValueOrDefault("ORG"),
                Notes = props.GetValueOrDefault("NOTE")
            });
            imported++;
        }
        await _db.SaveChangesAsync(ct);
        return new ContactImportResult(imported, skipped);
    }

    public async Task<ContactImportResult> ImportCsvAsync(long mailboxId, string csv, CancellationToken ct = default)
    {
        int imported = 0, skipped = 0;
        var rows = ParseCsv(csv);
        if (rows.Count == 0) return new ContactImportResult(0, 0);
        var header = rows[0]; var hi = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Count; i++) hi[header[i].Trim()] = i;
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 0) continue;
            string? At(int? i) => i != null && i.Value < row.Count ? row[i.Value].Trim() : null;
            var email = At(hi.TryGetValue("Email", out var e) ? e : null);
            var fullName = At(hi.TryGetValue("Name", out var n) ? n : null) ?? email ?? "(sin nombre)";
            if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(fullName)) { skipped++; continue; }
            _db.Contacts.Add(new Domain.Entities.Contact
            {
                OwnerMailboxId = mailboxId,
                Name = fullName, Email = email ?? "",
                Phone = At(hi.TryGetValue("Phone", out var p) ? p : null),
                Company = At(hi.TryGetValue("Company", out var c) ? c : null),
                Notes = At(hi.TryGetValue("Notes", out var nn) ? nn : null)
            });
            imported++;
        }
        await _db.SaveChangesAsync(ct);
        return new ContactImportResult(imported, skipped);
    }

    private static ContactDto ToDto(Domain.Entities.Contact c) => new(c.Id, c.Name, c.Email, c.Phone, c.Company, c.Notes);

    private static IEnumerable<string> SplitBlocks(string text, string blockName)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var cur = new StringBuilder(); bool inB = false;
        foreach (var raw in lines)
        {
            var l = raw.TrimEnd();
            if (l.StartsWith("BEGIN:" + blockName, StringComparison.OrdinalIgnoreCase)) { cur = new StringBuilder(); inB = true; }
            else if (l.StartsWith("END:" + blockName, StringComparison.OrdinalIgnoreCase)) { if (inB) { cur.AppendLine(l); yield return cur.ToString(); } inB = false; }
            else if (inB) cur.AppendLine(l);
        }
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Trim().Length == 0) continue;
            var row = new List<string>();
            var field = new StringBuilder();
            bool inQuote = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (inQuote)
                {
                    if (ch == '"' && i + 1 < raw.Length && raw[i + 1] == '"') { field.Append('"'); i++; }
                    else if (ch == '"') inQuote = false;
                    else field.Append(ch);
                }
                else
                {
                    if (ch == '"') inQuote = true;
                    else if (ch == ',') { row.Add(field.ToString().Trim()); field = new StringBuilder(); }
                    else field.Append(ch);
                }
            }
            row.Add(field.ToString().Trim());
            rows.Add(row);
        }
        return rows;
    }

    private static string CsvEscape(string v) => v.Contains(',') || v.Contains('"') || v.Contains('\n') ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    private static string Fold(string v) => v.Length <= 70 ? v : v;
}