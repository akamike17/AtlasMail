using AtlasMail.Application.Abstractions;
using AtlasMail.Domain.Entities;
using AtlasMail.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AtlasMail.Application.Services;

public sealed record GroupDto(long Id, long DomainId, string DomainName, string Name, string LocalPart,
    string Email, string? Description, bool Enabled, string SendPolicy, bool ModerationEnabled, int MaxMembers,
    IReadOnlyList<GroupMemberDto> Members);

public sealed record GroupMemberDto(long Id, string Address, string? DisplayName);

public interface IGroupService
{
    Task<IReadOnlyList<GroupDto>> ListAsync(long domainId, CancellationToken ct = default);
    Task<IReadOnlyList<GroupDto>> ListAllAsync(CancellationToken ct = default);
    Task<GroupDto?> GetAsync(long domainId, long groupId, CancellationToken ct = default);
    Task<GroupDto> CreateAsync(long domainId, GroupInput input, CancellationToken ct = default);
    Task<GroupDto> UpdateAsync(long domainId, long groupId, GroupInput input, CancellationToken ct = default);
    Task<bool> DeleteAsync(long domainId, long groupId, CancellationToken ct = default);
    Task<bool> AddMemberAsync(long domainId, long groupId, string address, string? displayName, CancellationToken ct = default);
    Task<bool> RemoveMemberAsync(long domainId, long memberId, CancellationToken ct = default);
    /// <summary>Expande una lista de distribución a direcciones (RFC aplica dedupe y límite de profundidad
    /// para prevenir loops). Lanza InvalidOperationException si la dirección no es una lista.</summary>
    Task<IReadOnlyList<string>> ExpandAsync(string address, CancellationToken ct = default);
    /// <summary>Comprueba si una dirección corresponde a una lista de distribución.</summary>
    Task<bool> IsDistributionAsync(string address, CancellationToken ct = default);
}

public sealed record GroupInput(string Name, string LocalPart, string? Description, bool Enabled,
    string SendPolicy, bool ModerationEnabled, int MaxMembers);

/// <summary>
/// Listas de distribución (spec §16): ventas@empresa.mx → ana@, juan@, maria@.
/// Políticas: quién puede enviar (interno/externo), moderación opcional, límite de miembros.
/// Prevención de loops en la expansión (profundidad máx + detección de ciclos + dedupe).
/// </summary>
public class GroupService : IGroupService
{
    private const int MaxDepth = 8;
    private readonly IApplicationDbContext _db;
    public GroupService(IApplicationDbContext db) => _db = db;

    public async Task<IReadOnlyList<GroupDto>> ListAsync(long domainId, CancellationToken ct = default)
    {
        var groups = await _db.DistributionLists.AsNoTracking()
            .Where(g => g.DomainId == domainId)
            .Include(g => g.Domain)
            .Include(g => g.Members)
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
        return groups.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<GroupDto>> ListAllAsync(CancellationToken ct = default)
    {
        var groups = await _db.DistributionLists.AsNoTracking()
            .Include(g => g.Domain).Include(g => g.Members)
            .OrderBy(g => g.Name).ToListAsync(ct);
        return groups.Select(ToDto).ToList();
    }

    public async Task<GroupDto?> GetAsync(long domainId, long groupId, CancellationToken ct = default)
    {
        var g = await LoadAsync(domainId, groupId, ct);
        return g == null ? null : ToDto(g);
    }

    public async Task<GroupDto> CreateAsync(long domainId, GroupInput input, CancellationToken ct = default)
    {
        var domain = await _db.Domains.AsNoTracking().FirstOrDefaultAsync(d => d.Id == domainId, ct)
            ?? throw new InvalidOperationException("Dominio no existe");
        var lp = input.LocalPart.Trim().ToLowerInvariant();
        if (lp.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(lp, "^[a-z0-9._%+-]+$"))
            throw new ArgumentException("Local part inválido");
        var exists = await _db.DistributionLists.AnyAsync(g => g.DomainId == domainId && g.LocalPart == lp, ct);
        if (exists) throw new ArgumentException("Ya existe una lista con esa dirección");
        // la dirección no debe chocar con buzones/alias locales
        if (await _db.Mailboxes.AnyAsync(m => m.DomainId == domainId && m.LocalPart == lp, ct)
            || await _db.Aliases.AnyAsync(a => a.DomainId == domainId && a.LocalPart == lp, ct))
            throw new ArgumentException("La dirección ya la usa un buzón o alias");

        var g = new DistributionList
        {
            DomainId = domainId,
            Name = input.Name,
            LocalPart = lp,
            Description = input.Description,
            Enabled = input.Enabled,
            SendPolicy = ParsePolicy(input.SendPolicy),
            ModerationEnabled = input.ModerationEnabled,
            MaxMembers = Math.Max(0, input.MaxMembers)
        };
        _db.DistributionLists.Add(g);
        await _db.SaveChangesAsync(ct);
        return ToDto(await LoadAsync(domainId, g.Id, ct)!);
    }

    public async Task<GroupDto> UpdateAsync(long domainId, long groupId, GroupInput input, CancellationToken ct = default)
    {
        var g = await LoadAsync(domainId, groupId, ct) ?? throw new InvalidOperationException("Lista no existe");
        g.Name = input.Name;
        g.Description = input.Description;
        g.Enabled = input.Enabled;
        g.SendPolicy = ParsePolicy(input.SendPolicy);
        g.ModerationEnabled = input.ModerationEnabled;
        g.MaxMembers = Math.Max(0, input.MaxMembers);
        await _db.SaveChangesAsync(ct);
        return ToDto(await LoadAsync(domainId, groupId, ct)!);
    }

    public async Task<bool> DeleteAsync(long domainId, long groupId, CancellationToken ct = default)
    {
        var g = await _db.DistributionLists
            .Include(x => x.Members)
            .FirstOrDefaultAsync(x => x.Id == groupId && x.DomainId == domainId, ct);
        if (g == null) return false;
        _db.DistributionListMembers.RemoveRange(g.Members);
        _db.DistributionLists.Remove(g);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AddMemberAsync(long domainId, long groupId, string address, string? displayName, CancellationToken ct = default)
    {
        var g = await _db.DistributionLists.FirstOrDefaultAsync(x => x.Id == groupId && x.DomainId == domainId, ct);
        if (g == null) return false;
        var a = address.Trim();
        if (a.Length == 0) return false;
        var exists = await _db.DistributionListMembers.AnyAsync(m => m.DistributionListId == groupId && m.AddressOrLocalPart == a, ct);
        if (exists) return false;
        _db.DistributionListMembers.Add(new DistributionListMember { DistributionListId = groupId, AddressOrLocalPart = a, DisplayName = displayName });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveMemberAsync(long domainId, long memberId, CancellationToken ct = default)
    {
        var m = await _db.DistributionListMembers
            .Include(x => x.DistributionList)
            .FirstOrDefaultAsync(x => x.Id == memberId && x.DistributionList!.DomainId == domainId, ct);
        if (m == null) return false;
        _db.DistributionListMembers.Remove(m);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public Task<bool> IsDistributionAsync(string address, CancellationToken ct = default)
    {
        var lp = ExtractAddress(address);
        if (lp == null) return Task.FromResult(false);
        return _db.DistributionLists.AsNoTracking()
            .AnyAsync(g => g.Enabled && g.LocalPart == lp.Value.LocalPart && g.Domain!.Name == lp.Value.Domain, ct);
    }

    public async Task<IReadOnlyList<string>> ExpandAsync(string address, CancellationToken ct = default)
    {
        var root = await ResolveGroupAsync(address, ct);
        if (root == null) throw new InvalidOperationException("La dirección no es una lista de distribución");
        var visited = new HashSet<long>();
        var result = new List<string>();
        await ExpandRecursiveAsync(root, result, visited, 0, ct);
        return result;
    }

    private async Task ExpandRecursiveAsync(DistributionList group, List<string> result, HashSet<long> visited, int depth, CancellationToken ct)
    {
        if (depth > MaxDepth || !visited.Add(group.Id)) return;   // prevent loops / depth
        var members = await _db.DistributionListMembers.AsNoTracking()
            .Where(m => m.DistributionListId == group.Id).ToListAsync(ct);
        foreach (var m in members)
        {
            var sub = await ResolveGroupAsync(m.AddressOrLocalPart, ct);
            if (sub != null)
            {
                await ExpandRecursiveAsync(sub, result, visited, depth + 1, ct);
            }
            else
            {
                var full = NormalizeMemberAddress(m.AddressOrLocalPart, group.Domain?.Name);
                if (full != null && !result.Contains(full)) result.Add(full);
            }
        }
    }

    /// <summary>Resuelve por dirección entera SOBRE el contexto del dominio de la lista.</summary>
    private async Task<DistributionList?> ResolveGroupAsync(string address, CancellationToken ct)
    {
        // 1) dominio explícito
        var lp = ExtractAddress(address);
        if (lp != null)
        {
            var g = await _db.DistributionLists.AsNoTracking()
                .Include(x => x.Domain)
                .FirstOrDefaultAsync(x => x.Enabled && x.LocalPart == lp.Value.LocalPart && x.Domain!.Name == lp.Value.Domain, ct);
            if (g != null) return g;
        }
        return null;
    }

    private static string? NormalizeMemberAddress(string member, string? defaultDomain)
    {
        var m = member.Trim();
        if (m.Length == 0) return null;
        // "ana" o "ana@dominio" -> normalizar a ana@dominio
        if (!m.Contains('@'))
        {
            if (string.IsNullOrEmpty(defaultDomain)) return null;
            m = m + "@" + defaultDomain;
        }
        return m.ToLowerInvariant();
    }

    private static (string LocalPart, string Domain)? ExtractAddress(string address)
    {
        var a = address.Trim();
        if (a.StartsWith("<")) a = a.Trim('<', '>');
        if (a.StartsWith("MAIL FROM:")) a = a["MAIL FROM:".Length..].Trim().Trim('<', '>');
        int at = a.IndexOf('@');
        if (at <= 0 || at == a.Length - 1) return null;
        return (a[..at].ToLowerInvariant(), a[(at + 1)..].ToLowerInvariant());
    }

    private static DistributionSendPolicy ParsePolicy(string s) =>
        s?.ToLowerInvariant() == "external" ? DistributionSendPolicy.ExternalAllowed : DistributionSendPolicy.InternalOnly;

    private async Task<DistributionList?> LoadAsync(long domainId, long groupId, CancellationToken ct)
        => await _db.DistributionLists.AsNoTracking()
            .Include(x => x.Domain).Include(x => x.Members)
            .FirstOrDefaultAsync(x => x.Id == groupId && x.DomainId == domainId, ct);

    private static GroupDto ToDto(DistributionList g)
    {
        var email = $"{g.LocalPart}@{g.Domain?.Name ?? ""}";
        return new GroupDto(g.Id, g.DomainId, g.Domain?.Name ?? "", g.Name, g.LocalPart, email,
            g.Description, g.Enabled, g.SendPolicy.ToString(), g.ModerationEnabled, g.MaxMembers,
            (g.Members ?? new List<DistributionListMember>())
                .Select(m => new GroupMemberDto(m.Id, m.AddressOrLocalPart, m.DisplayName)).ToList());
    }
}