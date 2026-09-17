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
    private readonly AtlasMail.Application.Abstractions.IMailIntelligenceService _intelligence;
    private readonly MailIntelligenceFacade _ai;

    public MailPersonalController(ICalendarService calendar, IContactService contacts, ICurrentUser current,
        AtlasMail.Application.Abstractions.IMailIntelligenceService intelligence, MailIntelligenceFacade ai)
    {
        _calendar = calendar; _contacts = contacts; _current = current; _intelligence = intelligence; _ai = ai;
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

    // ---------- IA asistida (FASE 8, spec §31) ----------

    [HttpPost("ai/analyze")]
    public async Task<IActionResult> AiAnalyze([FromBody] AiAnalyzeRequest req)
    {
        // Análisis local siempre disponible (no necesita buzón ni backend).
        if (!_intelligence.Enabled) return Ok(new { enabled = false });
        var sb = new AtlasMail.Application.Abstractions.SubjectBody(req.Subject ?? "", req.Body ?? "");
        return Ok(new
        {
            enabled = true,
            priority = _intelligence.Priority(sb),
            category = _intelligence.Classify(sb).ToString(),
            summary = _intelligence.Summarize(sb),
            phishing = _intelligence.AssessPhishing(sb).Score
        });
    }

    // ---------- IA avanzada con backend (FASE 8, §31) — requiere buzón + consentimiento ----------

    [HttpPost("ai/translate")]
    public async Task<IActionResult> AiTranslate([FromBody] AiTranslateRequest req)
    {
        var mb = await MailboxId();
        var r = await _ai.TranslateAsync(mb, new AtlasMail.Application.Abstractions.SubjectBody(req.Subject ?? "", req.Body ?? ""), req.ToLang ?? "es");
        return Ok(new { text = r });
    }

    [HttpPost("ai/suggest-reply")]
    public async Task<IActionResult> AiSuggestReply([FromBody] AiTranslateRequest req)
    {
        var mb = await MailboxId();
        var r = await _ai.SuggestReplyAsync(mb, new AtlasMail.Application.Abstractions.SubjectBody(req.Subject ?? "", req.Body ?? ""));
        return Ok(new { reply = r });
    }

    [HttpPost("ai/draft")]
    public async Task<IActionResult> AiDraft([FromBody] AiDraftRequest req)
    {
        var mb = await MailboxId();
        var r = await _ai.DraftAsync(mb, new AtlasMail.Application.Abstractions.SubjectBody(req.Subject ?? "", req.Body ?? ""), req.Tone);
        return Ok(new { draft = r });
    }

    [HttpPost("ai/classify")]
    public async Task<IActionResult> AiClassify([FromBody] AiTranslateRequest req)
    {
        var mb = await MailboxId();
        var r = await _ai.ClassifyAsync(mb, new AtlasMail.Application.Abstractions.SubjectBody(req.Subject ?? "", req.Body ?? ""));
        return Ok(new { category = r });
    }

    [HttpPost("ai/semantic-search")]
    public async Task<IActionResult> AiSemanticSearch([FromBody] AiSemanticSearchRequest req)
    {
        var mb = await MailboxId();
        var r = await _ai.SemanticSearchAsync(mb, req.Query ?? "", req.Docs ?? Array.Empty<string>(), req.TopK);
        return Ok(new { indices = r.Indices, reason = r.Reason });
    }

    [HttpGet("ai/consent")]
    public async Task<IActionResult> AiConsentGet()
    {
        var mb = await MailboxId();
        var b = await _ai.HasConsentAsync(mb);
        return Ok(new { consent = b, backend = _ai.BackendEnabled });
    }

    [HttpPost("ai/consent")]
    public async Task<IActionResult> AiConsentSet([FromBody] AiConsentRequest req)
    {
        var mb = await MailboxId();
        await _ai.SetConsentAsync(mb, req.Grant, CancellationToken.None);
        return Ok(new { consent = req.Grant });
    }
}

public sealed record AiAnalyzeRequest(string? Subject, string? Body);
public sealed record AiTranslateRequest(string? Subject, string? Body, string? ToLang);
public sealed record AiDraftRequest(string? Subject, string? Body, string? Tone);
public sealed record AiSemanticSearchRequest(string? Query, string[]? Docs, int TopK = 5);
public sealed record AiConsentRequest(bool Grant);