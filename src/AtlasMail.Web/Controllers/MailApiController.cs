using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using AtlasMail.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AtlasMail.Web.Controllers;

/// <summary>
/// API del webmail autenticado (secciones 11). Cada operación se aísla por el
/// mailboxId del usuario logueado — un usuario NUNCA ve/lee/mueve mensajes de otro
/// (anti-IDOR, sección 33). Los {id} de message/folder son un parámetro del servicio
/// que filtra por mailboxId.
/// </summary>
[ApiController]
[Authorize]
[Route("api/mail")]
public class MailApiController : ControllerBase
{
    private readonly IMailboxService _mailbox;
    private readonly ISubmissionService _submit;
    private readonly IMailSearchService _search;
    private readonly ICurrentUser _current;

    public MailApiController(IMailboxService mailbox, ISubmissionService submit, IMailSearchService search, ICurrentUser current)
    {
        _mailbox = mailbox; _submit = submit; _search = search; _current = current;
    }

    private async Task<IActionResult?> ForbiddenIfNoMailbox(CancellationToken ct)
    {
        var mb = await _current.GetMailboxIdAsync(ct);
        if (mb == null) return Unauthorized(new { error = "El usuario no tiene buzón webmail" });
        return null;
    }

    private async Task<long> MailboxId(CancellationToken ct) =>
        (await _current.GetMailboxIdAsync(ct))!.Value;

    [HttpGet("folders")]
    public async Task<IActionResult> Folders(CancellationToken ct)
    {
        var denied = await ForbiddenIfNoMailbox(ct); if (denied != null) return denied;
        return Ok(await _mailbox.ListFoldersAsync(await MailboxId(ct), ct));
    }

    [HttpGet("folder/{folderId:long}/messages")]
    public async Task<IActionResult> FolderMessages(long folderId, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var denied = await ForbiddenIfNoMailbox(ct); if (denied != null) return denied;
        return Ok(await _mailbox.ListFolderMessagesAsync(await MailboxId(ct), folderId, take, ct));
    }

    [HttpGet("message/{messageId:long}")]
    public async Task<IActionResult> Message(long messageId, CancellationToken ct)
    {
        var denied = await ForbiddenIfNoMailbox(ct); if (denied != null) return denied;
        var detail = await _mailbox.ReadMessageAsync(await MailboxId(ct), messageId, ct);
        return detail == null ? NotFound(new { error = "no encontrado" }) : Ok(detail);
    }

    [HttpPost("message/{messageId:long}/read")]
    public async Task<IActionResult> SetRead(long messageId, [FromBody] bool read, CancellationToken ct)
    {
        await _mailbox.SetReadAsync(await MailboxId(ct), messageId, read, ct);
        return Ok(new { ok = true });
    }

    [HttpPost("message/{messageId:long}/flag")]
    public async Task<IActionResult> SetFlag(long messageId, [FromBody] bool flag, CancellationToken ct)
    {
        await _mailbox.SetFlaggedAsync(await MailboxId(ct), messageId, flag, ct);
        return Ok(new { ok = true });
    }

    [HttpPost("message/{messageId:long}/move")]
    public async Task<IActionResult> Move(long messageId, [FromBody] long destinationFolderId, CancellationToken ct)
    {
        await _mailbox.MoveAsync(await MailboxId(ct), messageId, destinationFolderId, ct);
        return Ok(new { ok = true });
    }

    [HttpPost("compose")]
    public async Task<IActionResult> Compose([FromBody] ComposeMessageRequest req, CancellationToken ct)
    {
        var denied = await ForbiddenIfNoMailbox(ct); if (denied != null) return denied;
        var result = await _submit.SubmitAsync(req, _current.Username!, _current.Ip, ct);
        return result.Success ? Ok(result) : BadRequest(new { error = result.Error });
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string? sender, [FromQuery] string? recipient,
        [FromQuery] string? subject, [FromQuery] string? body, [FromQuery] string? attachmentName, CancellationToken ct)
    {
        var denied = await ForbiddenIfNoMailbox(ct); if (denied != null) return denied;
        var q = new MessageSearchQuery(sender, recipient, subject, body, null, null, attachmentName, 200);
        return Ok(await _search.SearchAsync(await MailboxId(ct), q, ct));
    }
}