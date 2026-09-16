using AtlasMail.Application.Dtos;
using AtlasMail.Domain.Enums;
using AtlasMail.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MailDomain = AtlasMail.Domain.Entities.Domain;

namespace AtlasMail.Application.Services;

public class AuthService : IAuthService
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IPasswordPolicy _policy;
    private readonly ILogger<AuthService> _logger;
    private readonly IAuditService _audit;
    // Config de rate-limit (brute force) — simple en memoria por proceso.
    private static readonly Dictionary<(string, string), Queue<DateTime>> _failures = new();
    private const int MaxFailures = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(15);

    public AuthService(IApplicationDbContext db, IPasswordHasher hasher, IPasswordPolicy policy,
        ILogger<AuthService> logger, IAuditService audit)
    {
        _db = db; _hasher = hasher; _policy = policy; _logger = logger; _audit = audit;
    }

    public async Task<LoginResult> LoginAsync(LoginRequest req, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return await Fail(req.Username, ipAddress, "missing_credentials", ct);

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username == req.Username.Trim(), ct);
        if (user == null || !user.Enabled)
            return await Fail(req.Username, ipAddress, "invalid_credentials", ct);

        bool ok = _hasher.Verify(req.Password, user.PasswordHash);
        if (!ok) return await Fail(req.Username, ipAddress, "invalid_password", ct);

        await _db.LoginAttempts.AddAsync(new LoginAttempt
        {
            Username = req.Username, IpAddress = ipAddress, Success = true, UserAgent = userAgent ?? string.Empty
        }, ct);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("Auth.LoginSuccess", req.Username, user.Id.ToString(), ipAddress,
            "user", user.Id.ToString(), "OK", null, ct);
        return new LoginResult(true, null, user.Username, user.Role, user.DomainId);
    }

    private async Task<LoginResult> Fail(string? username, string? ip, string reason, CancellationToken ct)
    {
        var key = (ip ?? "?", Normalize(username ?? "?"));
        lock (_failures)
        {
            if (!_failures.ContainsKey(key)) _failures[key] = new Queue<DateTime>();
            _failures[key].Enqueue(DateTime.UtcNow);
            while (_failures[key].Count > 0 && DateTime.UtcNow - _failures[key].Peek() > Window)
                _failures[key].Dequeue();
        }
        await _db.LoginAttempts.AddAsync(new LoginAttempt { Username = username, IpAddress = ip, Success = false, FailureReason = reason }, ct);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("Auth.LoginFailure", username, null, ip, "user", null, "DENIED:" + reason, null, ct);
        _logger.LogInformation("Login fallido para {User} desde {Ip}: {Reason}", username, ip, reason);
        return new LoginResult(false, "Credenciales inválidas", username, null, null);
    }

    public bool IsBlocked(string? ipAddress, string? username)
    {
        var key = (ipAddress ?? "?", Normalize(username ?? "?"));
        lock (_failures)
        {
            if (!_failures.ContainsKey(key)) return false;
            var recent = _failures[key].Count;
            if (recent >= MaxFailures)
            {
                // ¿pasó la duración de bloqueo?
                var oldest = _failures[key].Peek();
                if (DateTime.UtcNow - oldest > BlockDuration) { _failures.Remove(key); return false; }
                return true;
            }
            return false;
        }
    }

    private static string Normalize(string s) => s.Trim().ToLowerInvariant();
}

public class AdminService : IAdminService
{
    private readonly IApplicationDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly IPasswordPolicy _policy;
    private readonly IAuditService _audit;
    private readonly ILogger<AdminService> _logger;

    public AdminService(IApplicationDbContext db, IPasswordHasher hasher, IPasswordPolicy policy,
        IAuditService audit, ILogger<AdminService> logger)
    {
        _db = db; _hasher = hasher; _policy = policy; _audit = audit; _logger = logger;
    }

    public async Task<long> CreateDomainAsync(CreateDomainRequest req, string actor, string? ip, CancellationToken ct = default)
    {
        req = req with { Name = req.Name.Trim().ToLowerInvariant() };
        if (string.IsNullOrWhiteSpace(req.Name) || !req.Name.Contains('.'))
            throw new ArgumentException("Dominio inválido");
        if (await _db.Domains.AnyAsync(d => d.Name == req.Name, ct))
            throw new InvalidOperationException("El dominio ya existe");

        var domain = new MailDomain
        {
            Name = req.Name,
            MaxMailboxQuotaBytes = req.MaxMailboxQuotaBytes,
            MaxRecipientsPerMessage = req.MaxRecipientsPerMessage > 0 ? req.MaxRecipientsPerMessage : 100,
            MaxMessageSizeBytes = req.MaxMessageSizeBytes > 0 ? req.MaxMessageSizeBytes : 50 * 1024 * 1024,
            PlusAddressingEnabled = req.PlusAddressingEnabled,
            CatchAllEnabled = req.CatchAllEnabled,
            CatchAllTargetLocalPart = req.CatchAllTargetLocalPart
        };
        _db.Domains.Add(domain);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("Domain.Create", actor, null, ip, "domain", domain.Name, "OK", null, ct);
        return domain.Id;
    }

    public async Task<IReadOnlyList<DomainDto>> ListDomainsAsync(CancellationToken ct = default)
    {
        var domains = await _db.Domains.AsNoTracking()
            .Select(d => new { d.Id, d.Name, d.Enabled, d.MaxMailboxQuotaBytes, MailboxCount = d.Mailboxes.Count })
            .OrderBy(d => d.Name).ToListAsync(ct);
        return domains.Select(d => new DomainDto(d.Id, d.Name, d.Enabled, d.MaxMailboxQuotaBytes, d.MailboxCount)).ToList();
    }

    public async Task SetDomainEnabledAsync(long domainId, bool enabled, string actor, string? ip, CancellationToken ct = default)
    {
        var d = await _db.Domains.FindAsync([domainId], ct) ?? throw new InvalidOperationException("Dominio no encontrado");
        d.Enabled = enabled; d.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("Domain.SetEnabled", actor, null, ip, "domain", d.Name, enabled ? "ENABLED" : "DISABLED", null, ct);
    }

    public async Task<long> CreateMailboxAsync(CreateMailboxRequest req, string actor, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.LocalPart)) throw new ArgumentException("Localpart vacío");
        var domain = await _db.Domains.FindAsync([req.DomainId], ct) ?? throw new InvalidOperationException("Dominio no encontrado");
        if (!_policy.IsCompliant(req.Password, out string? reason))
            throw new ArgumentException("La contraseña no cumple la política: " + reason);
        if (await _db.Mailboxes.AnyAsync(m => m.DomainId == req.DomainId && m.LocalPart == req.LocalPart, ct))
            throw new InvalidOperationException("El buzón ya existe");

        var mb = new Domain.Entities.Mailbox
        {
            DomainId = req.DomainId,
            LocalPart = req.LocalPart.Trim().ToLowerInvariant(),
            DisplayName = req.DisplayName.Trim(),
            PasswordHash = _hasher.Hash(req.Password),
            QuotaBytes = domain.MaxMailboxQuotaBytes
        };
        _db.Mailboxes.Add(mb);
        await _db.SaveChangesAsync(ct);

        // Crear carpetas estándar
        foreach (var systemFolder in Enum.GetValues<SystemFolder>())
        {
            _db.Folders.Add(new Domain.Entities.Folder
            {
                MailboxId = mb.Id,
                Name = StandardFolderName(systemFolder),
                SystemName = systemFolder,
                SortOrder = (int)systemFolder
            });
        }
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("Mailbox.Create", actor, null, ip, "mailbox", $"{req.LocalPart}@{domain.Name}", "OK", null, ct);
        return mb.Id;
    }

    public static string StandardFolderName(SystemFolder f) => f switch
    {
        SystemFolder.Inbox => "Inbox", SystemFolder.Sent => "Sent", SystemFolder.Drafts => "Drafts",
        SystemFolder.Trash => "Trash", SystemFolder.Spam => "Spam", SystemFolder.Archive => "Archive",
        _ => f.ToString()
    };

    public async Task<IReadOnlyList<MailboxDto>> ListMailboxesAsync(long? domainId, CancellationToken ct = default)
    {
        var q = _db.Mailboxes.AsNoTracking();
        if (domainId.HasValue) q = q.Where(m => m.DomainId == domainId.Value);
        var list = await q.Select(m => new { m.Id, m.DomainId, DomainName = m.Domain!.Name, m.DisplayName, m.Status, m.QuotaBytes, m.UsedBytes, m.LocalPart })
            .OrderBy(m => m.LocalPart).ToListAsync(ct);
        return list.Select(m => new MailboxDto(m.Id, m.DomainId, $"{m.LocalPart}@{m.DomainName}", m.DisplayName, m.Status, m.QuotaBytes, m.UsedBytes)).ToList();
    }

    public async Task SetMailboxStatusAsync(long mailboxId, MailboxStatus status, string actor, string? ip, CancellationToken ct = default)
    {
        var mb = await _db.Mailboxes.FindAsync([mailboxId], ct) ?? throw new InvalidOperationException("Buzón no encontrado");
        mb.Status = status;
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("Mailbox.SetStatus", actor, null, ip, "mailbox", mb.EmailAddress, status.ToString(), null, ct);
    }

    public async Task<long> CreateAliasAsync(CreateAliasRequest req, string actor, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.LocalPart)) throw new ArgumentException("Localpart vacío");
        var domain = await _db.Domains.FindAsync([req.DomainId], ct) ?? throw new InvalidOperationException("Dominio no encontrado");
        if (await _db.Aliases.AnyAsync(a => a.DomainId == req.DomainId && a.LocalPart == req.LocalPart, ct))
            throw new InvalidOperationException("El alias ya existe");
        if (await _db.Mailboxes.AnyAsync(m => m.DomainId == req.DomainId && m.LocalPart == req.LocalPart, ct))
            throw new InvalidOperationException("Un buzón con ese nombre ya existe");

        var alias = new Domain.Entities.Alias { DomainId = req.DomainId, LocalPart = req.LocalPart.Trim().ToLowerInvariant(), TargetMailboxId = req.TargetMailboxId };
        _db.Aliases.Add(alias);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("Alias.Create", actor, null, ip, "alias", $"{alias.LocalPart}@{domain.Name}", "OK", null, ct);
        return alias.Id;
    }

    public async Task<IReadOnlyList<AliasDto>> ListAliasesAsync(long? domainId, CancellationToken ct = default)
    {
        var q = _db.Aliases.AsNoTracking();
        if (domainId.HasValue) q = q.Where(a => a.DomainId == domainId.Value);
        var list = await q.Select(a => new { a.Id, a.LocalPart, a.Enabled, DomainName = a.Domain!.Name, Target = a.TargetMailboxId.HasValue ? a.TargetMailbox!.EmailAddress : (string?)null })
            .OrderBy(a => a.LocalPart).ToListAsync(ct);
        return list.Select(a => new AliasDto(a.Id, $"{a.LocalPart}@{a.DomainName}", a.Target, a.Enabled)).ToList();
    }

    public async Task<long> CreateUserAsync(CreateUserRequest req, string actor, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Username)) throw new ArgumentException("Usuario vacío");
        if (!_policy.IsCompliant(req.Password, out string? reason))
            throw new ArgumentException("La contraseña no cumple la política: " + reason);
        if (await _db.Users.AnyAsync(u => u.Username == req.Username, ct))
            throw new InvalidOperationException("El usuario ya existe");
        var user = new Domain.Entities.User
        {
            Username = req.Username.Trim().ToLowerInvariant(),
            DisplayName = req.DisplayName.Trim(),
            PasswordHash = _hasher.Hash(req.Password),
            Role = req.Role,
            DomainId = req.DomainId
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        await _audit.RecordAsync("User.Create", actor, null, ip, "user", user.Username, "OK", $"role={user.Role}", ct);
        return user.Id;
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct = default)
    {
        var list = await _db.Users.AsNoTracking().OrderBy(u => u.Username)
            .Select(u => new { u.Id, u.Username, u.DisplayName, u.Role, u.Enabled }).ToListAsync(ct);
        return list.Select(u => new UserDto(u.Id, u.Username, u.DisplayName, u.Role, u.Enabled)).ToList();
    }
}