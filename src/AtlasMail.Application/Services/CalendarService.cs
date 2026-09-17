using System.Text;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Application.Services;

/// <summary>Evento como DTO plano para API/webmail.</summary>
public sealed record CalendarEventDto(
    long Id, long CalendarId, string Title, string? Description, string? Location,
    DateTime StartUtc, DateTime EndUtc, string TimeZoneId, string? OrganizerEmail,
    string? RecurrenceRule, string Status, bool IsAllDay, string? ExternalUid,
    IReadOnlyList<AttendeeDto> Attendees);

public sealed record AttendeeDto(string Email, string? DisplayName, string ParticipationStatus);

/// <summary>Resultado de importar un .ics.</summary>
public sealed record IcsImportResult(int Imported, int Skipped);

public interface ICalendarService
{
    Task<IReadOnlyList<CalendarEventDto>> ListByRangeAsync(long mailboxId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
    Task<IReadOnlyList<CalendarEventDto>> ListAllAsync(long mailboxId, CancellationToken ct = default);
    Task<CalendarEventDto?> GetAsync(long mailboxId, long eventId, CancellationToken ct = default);
    Task<CalendarEventDto> CreateAsync(long mailboxId, NewEventInput input, CancellationToken ct = default);
    Task<CalendarEventDto> UpdateAsync(long mailboxId, long eventId, NewEventInput input, CancellationToken ct = default);
    Task<bool> DeleteAsync(long mailboxId, long eventId, CancellationToken ct = default);
    /// <summary>Exporta todos los eventos del buzón a un .ics (RFC 5545).</summary>
    Task<string> ExportIcsAsync(long mailboxId, CancellationToken ct = default);
    /// <summary>Importa eventos de un .ics. Idempotente por ExternalUid.</summary>
    Task<IcsImportResult> ImportIcsAsync(long mailboxId, string icsContent, CancellationToken ct = default);
}

/// <summary>Input de crea/edición de evento.</summary>
public sealed record NewEventInput(string Title, DateTime? StartUtc, DateTime? EndUtc,
    string? Description, string? Location, string? TimeZoneId, string? OrganizerEmail,
    string? RecurrenceRule, string? Status, bool IsAllDay, IReadOnlyList<string>? AttendeeEmails);

/// <summary>
/// Calendario y eventos por buzón, con export/import `.ics` (RFC 5545). No afirma
/// compatibilidad Exchange/Outlook completa (§15): se valida interoperabilidad básica.
/// </summary>
public class CalendarService : ICalendarService
{
    private readonly IApplicationDbContext _db;

    public CalendarService(IApplicationDbContext db) => _db = db;

    private async Task<Calendar> EnsureCalendarAsync(long mailboxId, CancellationToken ct)
    {
        var cal = await _db.Calendars.AsNoTracking().FirstOrDefaultAsync(c => c.MailboxId == mailboxId, ct);
        if (cal != null) return cal;
        cal = new Calendar { MailboxId = mailboxId, Name = "Mi calendario" };
        _db.Calendars.Add(cal);
        await _db.SaveChangesAsync(ct);
        return cal;
    }

    public async Task<IReadOnlyList<CalendarEventDto>> ListByRangeAsync(long mailboxId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        var cal = await _db.Calendars.AsNoTracking().FirstOrDefaultAsync(c => c.MailboxId == mailboxId, ct);
        if (cal == null) return Array.Empty<CalendarEventDto>();
        var events = await _db.CalendarEvents.AsNoTracking()
            .Where(e => e.CalendarId == cal.Id && e.StartUtc < toUtc && e.EndUtc > fromUtc)
            .Include(e => e.Attendees)
            .ToListAsync(ct);
        return events.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<CalendarEventDto>> ListAllAsync(long mailboxId, CancellationToken ct = default)
    {
        var cal = await _db.Calendars.AsNoTracking().FirstOrDefaultAsync(c => c.MailboxId == mailboxId, ct);
        if (cal == null) return Array.Empty<CalendarEventDto>();
        var events = await _db.CalendarEvents.AsNoTracking()
            .Where(e => e.CalendarId == cal.Id).Include(e => e.Attendees).ToListAsync(ct);
        return events.Select(ToDto).ToList();
    }

    public async Task<CalendarEventDto?> GetAsync(long mailboxId, long eventId, CancellationToken ct = default)
    {
        var ev = await GetOwnedAsync(mailboxId, eventId, ct);
        return ev == null ? null : ToDto(ev);
    }

    private async Task<CalendarEvent?> GetOwnedAsync(long mailboxId, long eventId, CancellationToken ct)
    {
        return await _db.CalendarEvents.AsNoTracking()
            .Include(e => e.Attendees)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Calendar!.MailboxId == mailboxId, ct);
    }

    public async Task<CalendarEventDto> CreateAsync(long mailboxId, NewEventInput input, CancellationToken ct = default)
    {
        var cal = await EnsureCalendarAsync(mailboxId, ct);
        var ev = new CalendarEvent
        {
            CalendarId = cal.Id,
            Title = input.Title,
            Description = input.Description,
            Location = input.Location,
            StartUtc = input.StartUtc ?? DateTime.UtcNow,
            EndUtc = input.EndUtc ?? (input.StartUtc ?? DateTime.UtcNow).AddHours(1),
            TimeZoneId = string.IsNullOrWhiteSpace(input.TimeZoneId) ? "UTC" : input.TimeZoneId,
            OrganizerEmail = input.OrganizerEmail,
            RecurrenceRule = string.IsNullOrWhiteSpace(input.RecurrenceRule) ? null : input.RecurrenceRule,
            Status = ParseStatus(input.Status),
            IsAllDay = input.IsAllDay,
        };
        _db.CalendarEvents.Add(ev);
        AddAttendees(ev, input.AttendeeEmails);
        await _db.SaveChangesAsync(ct);
        return ToDto(await _db.CalendarEvents.AsNoTracking().Include(e => e.Attendees).FirstAsync(e => e.Id == ev.Id, ct));
    }

    public async Task<CalendarEventDto> UpdateAsync(long mailboxId, long eventId, NewEventInput input, CancellationToken ct = default)
    {
        var ev = await _db.CalendarEvents.Include(e => e.Attendees)
            .FirstOrDefaultAsync(e => e.Id == eventId && e.Calendar!.MailboxId == mailboxId, ct)
            ?? throw new InvalidOperationException("Evento no encontrado o no es de este buzón");
        ev.Title = input.Title;
        ev.Description = input.Description;
        ev.Location = input.Location;
        ev.StartUtc = input.StartUtc ?? ev.StartUtc;
        ev.EndUtc = input.EndUtc ?? ev.EndUtc;
        ev.TimeZoneId = string.IsNullOrWhiteSpace(input.TimeZoneId) ? ev.TimeZoneId : input.TimeZoneId;
        ev.OrganizerEmail = input.OrganizerEmail;
        ev.RecurrenceRule = string.IsNullOrWhiteSpace(input.RecurrenceRule) ? null : input.RecurrenceRule;
        ev.Status = ParseStatus(input.Status);
        ev.IsAllDay = input.IsAllDay;
        ev.UpdatedAtUtc = DateTime.UtcNow;
        if (input.AttendeeEmails is { Count: > 0 })
        {
            _db.CalendarEventAttendees.RemoveRange(ev.Attendees);
            AddAttendees(ev, input.AttendeeEmails);
        }
        await _db.SaveChangesAsync(ct);
        return ToDto(await _db.CalendarEvents.AsNoTracking().Include(e => e.Attendees).FirstAsync(e => e.Id == eventId, ct));
    }

    public async Task<bool> DeleteAsync(long mailboxId, long eventId, CancellationToken ct = default)
    {
        var ev = await _db.CalendarEvents.FirstOrDefaultAsync(e => e.Id == eventId && e.Calendar!.MailboxId == mailboxId, ct);
        if (ev == null) return false;
        _db.CalendarEvents.Remove(ev);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static void AddAttendees(CalendarEvent ev, IReadOnlyList<string>? emails)
    {
        if (emails == null) return;
        foreach (var e in emails)
        {
            if (string.IsNullOrWhiteSpace(e)) continue;
            ev.Attendees.Add(new CalendarEventAttendee { Email = e.Trim() });
        }
    }

    private static CalendarEventDisplayStatus ParseStatus(string? s) => s?.ToLowerInvariant() switch
    {
        "tentative" => CalendarEventDisplayStatus.Tentative,
        "cancelled" => CalendarEventDisplayStatus.Cancelled,
        _ => CalendarEventDisplayStatus.Confirmed
    };

    private static CalendarEventDto ToDto(CalendarEvent e)
    {
        return new CalendarEventDto(
            e.Id, e.CalendarId, e.Title, e.Description, e.Location,
            e.StartUtc, e.EndUtc, e.TimeZoneId, e.OrganizerEmail, e.RecurrenceRule,
            e.Status.ToString(), e.IsAllDay, e.ExternalUid,
            e.Attendees?.Select(a => new AttendeeDto(a.Email, a.DisplayName, a.ParticipationStatus.ToString())).ToList() ?? new List<AttendeeDto>());
    }

    // ---------- iCalendar (RFC 5545) ----------

    public async Task<string> ExportIcsAsync(long mailboxId, CancellationToken ct = default)
    {
        var events = await ListAllAsync(mailboxId, ct);
        var sb = new StringBuilder();
        sb.AppendLine("BEGIN:VCALENDAR");
        sb.AppendLine("VERSION:2.0");
        sb.AppendLine("PRODID:-//AtlasMail//Calendar//EN");
        foreach (var e in events)
        {
            sb.AppendLine("BEGIN:VEVENT");
            sb.AppendLine("UID:" + (string.IsNullOrWhiteSpace(e.ExternalUid) ? $"atlas-{e.Id}@atlasmail.local" : e.ExternalUid));
            sb.AppendLine("DTSTAMP:" + ToIcsDate(DateTime.UtcNow));
            sb.AppendLine("DTSTART:" + ToIcsDate(e.StartUtc));
            sb.AppendLine("DTEND:" + ToIcsDate(e.EndUtc));
            sb.AppendLine("SUMMARY:" + Fold(e.Title));
            if (!string.IsNullOrWhiteSpace(e.Description)) sb.AppendLine("DESCRIPTION:" + Fold(e.Description));
            if (!string.IsNullOrWhiteSpace(e.Location)) sb.AppendLine("LOCATION:" + Fold(e.Location));
            if (e.RecurrenceRule != null) sb.AppendLine("RRULE:" + e.RecurrenceRule);
            sb.AppendLine("STATUS:" + IcsStatus(e.Status));
            foreach (var a in e.Attendees) sb.AppendLine("ATTENDEE:mailto:" + a.Email);
            sb.AppendLine("END:VEVENT");
        }
        sb.AppendLine("END:VCALENDAR");
        return sb.ToString();
    }

    public async Task<IcsImportResult> ImportIcsAsync(long mailboxId, string icsContent, CancellationToken ct = default)
    {
        var cal = await EnsureCalendarAsync(mailboxId, ct);
        int imported = 0, skipped = 0;
        foreach (var block in SplitVEventBlocks(icsContent))
        {
            var props = ParseProps(block);
            if (!props.TryGetValue("SUMMARY", out var title) || string.IsNullOrWhiteSpace(title))
            { skipped++; continue; }
            DateTime? start = ParseIcsDate(props.GetValueOrDefault("DTSTART"));
            DateTime? end = ParseIcsDate(props.GetValueOrDefault("DTEND"));
            if (start == null && end == null) { skipped++; continue; }
            start ??= end;
            end ??= start.Value.AddHours(1);
            var uid = props.GetValueOrDefault("UID");

            var existing = !string.IsNullOrWhiteSpace(uid)
                ? await _db.CalendarEvents.FirstOrDefaultAsync(e => e.CalendarId == cal.Id && e.ExternalUid == uid, ct)
                : null;
            if (existing != null) { skipped++; continue; }

            var ev = new CalendarEvent
            {
                CalendarId = cal.Id,
                Title = title.Trim(),
                Description = props.GetValueOrDefault("DESCRIPTION"),
                Location = props.GetValueOrDefault("LOCATION"),
                StartUtc = start.Value, EndUtc = end.Value,
                TimeZoneId = "UTC",
                RecurrenceRule = props.GetValueOrDefault("RRULE"),
                Status = props.GetValueOrDefault("STATUS")?.ToLowerInvariant() switch
                {
                    "tentative" => CalendarEventDisplayStatus.Tentative,
                    "cancelled" => CalendarEventDisplayStatus.Cancelled,
                    _ => CalendarEventDisplayStatus.Confirmed
                },
                ExternalUid = string.IsNullOrWhiteSpace(uid) ? null : uid
            };
            _db.CalendarEvents.Add(ev);
            imported++;
        }
        await _db.SaveChangesAsync(ct);
        return new IcsImportResult(imported, skipped);
    }

    private static string IcsStatus(string s) => s.ToUpperInvariant() switch
    {
        "TENTATIVE" => "TENTATIVE",
        "CANCELLED" => "CANCELLED",
        _ => "CONFIRMED"
    };

    private static string ToIcsDate(DateTime dt)
        => dt.ToString("yyyyMMddTHHmmssZ"); // Western & UTC (Z)

    private static DateTime? ParseIcsDate(string? val)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;
        // "20260916T120000Z" or "20260916T120000"
        var v = val.Trim();
        if (v.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase))
        {
            int colon = v.IndexOf(':');
            if (colon > 0) v = v[(colon + 1)..];
        }
        var s = v.Replace("Z", "").Replace("z", "");
        if (s.Length == 8) s = s + "T000000"; // date only
        if (DateTime.TryParseExact(s, "yyyyMMddTHHmmss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            return dt;
        return null;
    }

    private static IEnumerable<string> SplitVEventBlocks(string ics)
    {
        var lines = ics.Replace("\r\n", "\n").Split('\n');
        var current = new StringBuilder();
        bool inEvent = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { current = new StringBuilder(); inEvent = true; }
            else if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase)) { if (inEvent) { current.AppendLine(line); yield return current.ToString(); } inEvent = false; }
            else if (inEvent) current.AppendLine(line);
        }
    }

    private static Dictionary<string, string?> ParseProps(string block)
    {
        // unfolded lines
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var logical = new List<string>();
        foreach (var raw in block.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if ((line[0] == ' ' || line[0] == '\t') && logical.Count > 0) logical[^1] += line[1..];
            else logical.Add(line);
        }
        foreach (var line in logical)
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var name = line[..colon];
            var value = line[(colon + 1)..];
            if (name.Contains(';')) name = name[..name.IndexOf(';')];
            dict[name.Trim().ToUpperInvariant()] = value;
        }
        return dict;
    }

    private static string Fold(string value)
    {
        var v = value.Replace("\n", " ").Replace(";", "\\;").Replace(",", "\\,").Replace(":", "\\:");
        var sb = new StringBuilder();
        int i = 0;
        while (i < v.Length)
        {
            int take = Math.Min(73, v.Length - i);
            sb.Append(i == 0 ? "" : "\r\n ");
            sb.Append(v.Substring(i, take));
            i += take;
        }
        return sb.ToString();
    }
}