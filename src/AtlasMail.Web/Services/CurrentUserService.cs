using System.Security.Claims;
using AtlasMail.Application;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Web.Services;

/// <summary>
/// Resuelve la identidad y el buzón del usuario autenticado en el request actual.
/// Aisla por usuario (IDOR: un usuario nunca ve buzones de otros).
/// </summary>
public class CurrentUserService
{
    private readonly IHttpContextAccessor _http;
    private readonly IApplicationDbContext _db;

    public CurrentUserService(IHttpContextAccessor http, IApplicationDbContext db)
    {
        _http = http; _db = db;
    }

    public string? Username => _http.HttpContext?.User.Identity?.Name;
    public string Role => _http.HttpContext?.User.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role)?.Value ?? "User";
    public string? DomainId => _http.HttpContext?.User.Claims.FirstOrDefault(c => c.Type == "domainId")?.Value;
    public bool IsSuperAdmin => _http.HttpContext?.User.IsInRole("SuperAdmin") ?? false;
    public string? Ip => _http.HttpContext?.Connection.RemoteIpAddress?.ToString();

    /// <summary>Buzón del usuario logueado (por localpart dentro de su dominio).</summary>
    public async Task<long?> GetMailboxIdAsync(CancellationToken ct = default)
    {
        var username = Username;
        if (string.IsNullOrWhiteSpace(username)) return null;
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == username, ct);
        if (user?.DomainId == null) return null;
        var mailbox = await _db.Mailboxes.AsNoTracking()
            .FirstOrDefaultAsync(m => m.DomainId == user.DomainId.Value && m.LocalPart == user.Username && m.Status != Domain.Enums.MailboxStatus.SoftDeleted, ct);
        return mailbox?.Id;
    }
}

/// <summary>Para inyección basada en contexto HTTP. Registrado en Program.cs.</summary>
public interface ICurrentUser
{
    string? Username { get; }
    string Role { get; }
    bool IsSuperAdmin { get; }
    string? Ip { get; }
    Task<long?> GetMailboxIdAsync(CancellationToken ct = default);
}

public class HttpCurrentUser : ICurrentUser
{
    private readonly CurrentUserService _inner;
    public HttpCurrentUser(CurrentUserService inner) => _inner = inner;
    public string? Username => _inner.Username;
    public string Role => _inner.Role;
    public bool IsSuperAdmin => _inner.IsSuperAdmin;
    public string? Ip => _inner.Ip;
    public Task<long?> GetMailboxIdAsync(CancellationToken ct = default) => _inner.GetMailboxIdAsync(ct);
}