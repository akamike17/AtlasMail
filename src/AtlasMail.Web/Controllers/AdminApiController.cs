using AtlasMail.Application;
using AtlasMail.Application.Abstractions;
using AtlasMail.Application.Dtos;
using AtlasMail.Application.Services;
using AtlasMail.Domain.Enums;
using AtlasMail.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AtlasMail.Web.Controllers;

/// <summary>
/// API administrativa (secciones 26, 27, 28, 30). Requiere rol de administración.
/// SuperAdmin gestiona todo; DomainAdmin se limita a su dominio (mínimo privilegio, sección 4).
/// Nunca expone endpoints administrativos anónimos (sección 30).
/// </summary>
[ApiController]
[Authorize(Roles = "SuperAdmin,DomainAdmin,SecurityAdmin")]
[Route("api/admin")]
public class AdminApiController : ControllerBase
{
    private readonly IAdminService _admin;
    private readonly IAdminDashboardService _dashboard;
    private readonly IOutboundQueueService _queue;
    private readonly IMessageTraceService _trace;
    private readonly IAuditService _audit;
    private readonly IBackupService _backup;
    private readonly IDomainMailAuthService _mailAuth;
    private readonly IQuarantineService _quarantine;
    private readonly IGroupService _groups;
    private readonly ICurrentUser _current;

    public AdminApiController(IAdminService admin, IAdminDashboardService dashboard, IOutboundQueueService queue,
        IMessageTraceService trace, IAuditService audit, IBackupService backup, IDomainMailAuthService mailAuth, IQuarantineService quarantine, IGroupService groups, ICurrentUser current)
    {
        _admin = admin; _dashboard = dashboard; _queue = queue; _trace = trace; _audit = audit; _backup = backup; _mailAuth = mailAuth; _quarantine = quarantine; _groups = groups; _current = current;
    }

    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard() => Ok(await _dashboard.GetAsync());

    // ---- Controles de rol para acciones restringidas (apartado 30 y mínimo privilegio) ----

    [HttpPost("domain")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> CreateDomain([FromBody] CreateDomainRequest req)
    {
        try { return Ok(new { id = await _admin.CreateDomainAsync(req, _current.Username!, _current.Ip) }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("domain/{id:long}/toggle")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> ToggleDomain(long id, [FromBody] bool enabled)
    {
        try { await _admin.SetDomainEnabledAsync(id, enabled, _current.Username!, _current.Ip); return Ok(new { ok = true }); }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpPost("mailbox")]
    [Authorize(Roles = "SuperAdmin,DomainAdmin")]
    public async Task<IActionResult> CreateMailbox([FromBody] CreateMailboxRequest req)
    {
        try { return Ok(new { id = await _admin.CreateMailboxAsync(req, _current.Username!, _current.Ip) }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("mailbox/{id:long}/status")]
    [Authorize(Roles = "SuperAdmin,DomainAdmin")]
    public async Task<IActionResult> SetMailboxStatus(long id, [FromBody] MailboxStatus status)
    {
        try { await _admin.SetMailboxStatusAsync(id, status, _current.Username!, _current.Ip); return Ok(new { ok = true }); }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpPost("alias")]
    [Authorize(Roles = "SuperAdmin,DomainAdmin")]
    public async Task<IActionResult> CreateAlias([FromBody] CreateAliasRequest req)
    {
        try { return Ok(new { id = await _admin.CreateAliasAsync(req, _current.Username!, _current.Ip) }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("user")]
    [Authorize(Roles = "SuperAdmin,DomainAdmin")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req)
    {
        try { return Ok(new { id = await _admin.CreateUserAsync(req, _current.Username!, _current.Ip) }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    // ---- Lecturas ----

    [HttpGet("domains")]
    public async Task<IActionResult> Domains() => Ok(await _admin.ListDomainsAsync());

    [HttpGet("mailboxes")]
    public async Task<IActionResult> Mailboxes([FromQuery] long? domainId) => Ok(await _admin.ListMailboxesAsync(domainId));

    [HttpGet("aliases")]
    public async Task<IActionResult> Aliases([FromQuery] long? domainId) => Ok(await _admin.ListAliasesAsync(domainId));

    [HttpGet("users")]
    [Authorize(Roles = "SuperAdmin,SecurityAdmin")]
    public async Task<IActionResult> Users() => Ok(await _admin.ListUsersAsync());

    [HttpGet("queue")]
    public async Task<IActionResult> Queue([FromQuery] int take = 100) => Ok(await _queue.ListAsync(take));

    [HttpGet("trace")]
    public async Task<IActionResult> Trace([FromQuery] string? messageId, [FromQuery] string? sender,
        [FromQuery] string? recipient, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
        => Ok(await _trace.SearchAsync(messageId, sender, recipient, from, to));

    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] int take = 200) => Ok(await _audit.ListRecentAsync(take));

    [HttpPost("backup")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> Backup()
    {
        try { return Ok(await _backup.CreateBackupAsync(_current.Username!)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("backup/{id}/restore")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> Restore(string id)
    {
        try { return Ok(await _backup.RestoreAsync(id, _current.Username!)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    // ---- FASE 4: autenticación de correo por dominio (spec §17-19) ----

    [HttpGet("domain/{id:long}/auth")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> DomainAuth(long id)
    {
        try { return Ok(await _mailAuth.GetStatusAsync(id)); }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpPost("domain/{id:long}/auth/dkim/enable")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> EnableDkim(long id)
    {
        try { return Ok(await _mailAuth.EnableDkimAsync(id)); }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpPost("domain/{id:long}/auth/dmarc")]
    [Authorize(Roles = "SuperAdmin")]
    public async Task<IActionResult> SetDmarc(long id, [FromBody] string policy)
    {
        try { return Ok(await _mailAuth.SetDmarcPolicyAsync(id, policy)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    // ---------- Quarantine / blocklist (FASE 5, spec §22) ----------

    [HttpGet("quarantine")]
    [Authorize(Roles = "SuperAdmin,SecurityAdmin")]
    public async Task<IActionResult> QuarantineList(int skip = 0, int take = 50, long? mailboxId = null)
        => Ok(await _quarantine.ListAsync(skip, take, mailboxId));

    [HttpGet("quarantine/{id:long}")]
    [Authorize(Roles = "SuperAdmin,SecurityAdmin")]
    public async Task<IActionResult> QuarantineInspect(long id)
    {
        var item = await _quarantine.InspectAsync(id);
        return item == null ? NotFound(new { error = "No existe o no está en cuarentena" }) : Ok(item);
    }

    [HttpPost("quarantine/{id:long}/release")]
    [Authorize(Roles = "SuperAdmin,SecurityAdmin")]
    public async Task<IActionResult> QuarantineRelease(long id)
        => (await _quarantine.ReleaseAsync(id)) ? Ok(new { ok = true }) : NotFound(new { error = "No existe o no está en cuarentena" });

    [HttpDelete("quarantine/{id:long}")]
    [Authorize(Roles = "SuperAdmin,SecurityAdmin")]
    public async Task<IActionResult> QuarantineDelete(long id)
        => (await _quarantine.DeleteAsync(id)) ? Ok(new { ok = true }) : NotFound(new { error = "No existe o no está en cuarentena" });

    [HttpPost("quarantine/block")]
    [Authorize(Roles = "SuperAdmin,SecurityAdmin")]
    public async Task<IActionResult> BlockSender([FromBody] BlockSenderRequest req)
    {
        var kind = req?.Kind?.Equals("domain", StringComparison.OrdinalIgnoreCase) == true ? SenderMatchKind.Domain : SenderMatchKind.Exact;
        var ok = await _quarantine.BlockSenderAsync(req?.Value ?? "", kind, req?.Reason, _current.Username, CancellationToken.None);
        return ok ? Ok(new { blocked = true }) : BadRequest(new { error = "Dirección inválida, ya bloqueada o datos incompletos" });
    }

    [HttpGet("quarantine/blocked")]
    [Authorize(Roles = "SuperAdmin,SecurityAdmin")]
    public async Task<IActionResult> BlockedList() => Ok(await _quarantine.ListBlockedAsync());

    [HttpDelete("quarantine/blocked/{id:long}")]
    [Authorize(Roles = "SuperAdmin,SecurityAdmin")]
    public async Task<IActionResult> UnblockSender(long id)
        => (await _quarantine.UnblockAsync(id)) ? Ok(new { unblocked = true }) : NotFound(new { error = "No existe" });

    // ---------- Grupos / listas de distribución (FASE 6, spec §16) ----------

    [HttpGet("domain/{domainId:long}/groups")]
    [Authorize(Roles = "SuperAdmin,DomainAdmin")]
    public async Task<IActionResult> GroupList(long domainId) => Ok(await _groups.ListAsync(domainId));

    [HttpPost("domain/{domainId:long}/groups")]
    [Authorize(Roles = "SuperAdmin,DomainAdmin")]
    public async Task<IActionResult> GroupCreate(long domainId, [FromBody] GroupInputExt input)
    {
        try { return Ok(await _groups.CreateAsync(domainId, new GroupInput(input!.Name, input.LocalPart, input.Description, input.Enabled, input.SendPolicy ?? "internal", input.ModerationEnabled, input.MaxMembers))); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpPut("domain/{domainId:long}/groups/{id:long}")]
    [Authorize(Roles = "SuperAdmin,DomainAdmin")]
    public async Task<IActionResult> GroupUpdate(long domainId, long id, [FromBody] GroupInputExt input)
    {
        try { return Ok(await _groups.UpdateAsync(domainId, id, new GroupInput(input!.Name, input.LocalPart, input.Description, input.Enabled, input.SendPolicy ?? "internal", input.ModerationEnabled, input.MaxMembers))); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpDelete("domain/{domainId:long}/groups/{id:long}")]
    [Authorize(Roles = "SuperAdmin,DomainAdmin")]
    public async Task<IActionResult> GroupDelete(long domainId, long id)
        => (await _groups.DeleteAsync(domainId, id)) ? Ok(new { ok = true }) : NotFound(new { error = "No existe" });

    [HttpPost("domain/{domainId:long}/groups/{id:long}/members")]
    [Authorize(Roles = "SuperAdmin,DomainAdmin")]
    public async Task<IActionResult> GroupAddMember(long domainId, long id, [FromBody] GroupMemberInputExt m)
        => (await _groups.AddMemberAsync(domainId, id, m?.Address ?? "", m?.DisplayName)) ? Ok(new { ok = true }) : BadRequest(new { error = "Miembro inválido o ya existe" });

    [HttpDelete("domain/{domainId:long}/groups/members/{memberId:long}")]
    [Authorize(Roles = "SuperAdmin,DomainAdmin")]
    public async Task<IActionResult> GroupRemoveMember(long domainId, long memberId)
        => (await _groups.RemoveMemberAsync(domainId, memberId)) ? Ok(new { ok = true }) : NotFound(new { error = "No existe" });

    [HttpGet("groups/expand/{localPart}/{domainName}")]
    [Authorize(Roles = "SuperAdmin,DomainAdmin")]
    public async Task<IActionResult> GroupExpand(string localPart, string domainName)
    {
        try { return Ok(new { members = await _groups.ExpandAsync($"{localPart}@{domainName}") }); }
        catch (InvalidOperationException ex) { return NotFound(new { error = ex.Message }); }
    }
}

public sealed record GroupInputExt(string? Name, string LocalPart, string? Description, bool Enabled, string? SendPolicy, bool ModerationEnabled, int MaxMembers);
public sealed record GroupMemberInputExt(string? Address, string? DisplayName);

public sealed record BlockSenderRequest(string? Value, string? Kind, string? Reason);