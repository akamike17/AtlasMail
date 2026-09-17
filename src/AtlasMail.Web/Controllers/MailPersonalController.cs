using AtlasMail.Application.Services;
using AtlasMail.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AtlasMail.Web.Controllers;

/// <summary>
/// API de colaboración del webmail (FASE 6, specs §14-15): calendario y contactos
/// personales del usuario autenticado. Todo se limita al mailboxId del usuario (anti-IDOR).
/// </summary>
[ApiController]
[Authorize]
[Route("api/personal")]
public class MailPersonalController : ControllerBase
{
    private readonly ICalendarService _calendar;
    private readonly IContactService _contacts;
    private readonly ICurrentUser _current;

    public MailPersonalController(ICalendarService calendar, IContactService contacts, ICurrentUser current)
    {
        _calendar = calendar; _contacts = contacts; _current = current;
    }

    private async Task<long> MailboxId() => (await _current.GetMailboxIdAsync(CancellationToken.None))!.Value;

    // ---------- Calendario ----------

    [HttpGet("calendar")]
    public async Task<IActionResult> CalendarList(DateTime? from = null, DateTime? to = null)
    {
        var mb = await MailboxId();
        var f = from ?? DateTime.UtcNow.AddMonths(-1);
        var t = to ?? DateTime.UtcNow.AddMonths(1);
        return Ok(await _calendar.ListByRangeAsync(mb, f, t));
    }

    [HttpGet("calendar/{id:long}")]
    public async Task<IActionResult> CalendarGet(long id)
        => Ok(await _calendar.GetAsync(await MailboxId(), id));

    [HttpPost("calendar")]
    public async Task<IActionResult> CalendarCreate([FromBody] NewEventInput input)
        => Ok(await _calendar.CreateAsync(await MailboxId(), input));

    [HttpPut("calendar/{id:long}")]
    public async Task<IActionResult> CalendarUpdate(long id, [FromBody] NewEventInput input)
    {
        try { return Ok(await _calendar.UpdateAsync(await MailboxId(), id, input)); }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpDelete("calendar/{id:long}")]
    public async Task<IActionResult> CalendarDelete(long id)
        => (await _calendar.DeleteAsync(await MailboxId(), id)) ? Ok(new { ok = true }) : NotFound(new { error = "No existe" });

    [HttpGet("calendar/export.ics")]
    public async Task<IActionResult> CalendarExport()
    {
        var ics = await _calendar.ExportIcsAsync(await MailboxId());
        return Content(ics, "text/calendar", System.Text.Encoding.UTF8);
    }

    [HttpPost("calendar/import.ics")]
    [Consumes("text/plain")]
    public async Task<IActionResult> CalendarImport()
    {
        using var reader = new System.IO.StreamReader(Request.Body);
        var content = await reader.ReadToEndAsync();
        return Ok(await _calendar.ImportIcsAsync(await MailboxId(), content));
    }

    // ---------- Contactos ----------

    [HttpGet("contacts")]
    public async Task<IActionResult> ContactsList()
        => Ok(await _contacts.ListPersonalAsync(await MailboxId()));

    [HttpPost("contacts")]
    public async Task<IActionResult> ContactCreate([FromBody] ContactInput input)
        => Ok(await _contacts.CreatePersonalAsync(await MailboxId(), input));

    [HttpPut("contacts/{id:long}")]
    public async Task<IActionResult> ContactUpdate(long id, [FromBody] ContactInput input)
    {
        var c = await _contacts.UpdateAsync(await MailboxId(), id, input);
        return c == null ? NotFound(new { error = "No existe" }) : Ok(c);
    }

    [HttpDelete("contacts/{id:long}")]
    public async Task<IActionResult> ContactDelete(long id)
        => (await _contacts.DeleteAsync(await MailboxId(), id)) ? Ok(new { ok = true }) : NotFound(new { error = "No existe" });

    [HttpGet("contacts/export.vcf")]
    public async Task<IActionResult> ContactsExportVcf()
        => Content(await _contacts.ExportVcfAsync(await MailboxId()), "text/vcard", System.Text.Encoding.UTF8);

    [HttpGet("contacts/export.csv")]
    public async Task<IActionResult> ContactsExportCsv()
        => Content(await _contacts.ExportCsvAsync(await MailboxId()), "text/csv", System.Text.Encoding.UTF8);

    [HttpPost("contacts/import.vcf")]
    [Consumes("text/plain")]
    public async Task<IActionResult> ContactImportVcf()
    {
        string content = await new System.IO.StreamReader(Request.Body).ReadToEndAsync();
        return Ok(await _contacts.ImportVcfAsync(await MailboxId(), content));
    }

    [HttpPost("contacts/import.csv")]
    [Consumes("text/plain")]
    public async Task<IActionResult> ContactImportCsv()
    {
        string content = await new System.IO.StreamReader(Request.Body).ReadToEndAsync();
        return Ok(await _contacts.ImportCsvAsync(await MailboxId(), content));
    }
}