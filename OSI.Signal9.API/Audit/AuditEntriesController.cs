using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Audit;

/// <summary>
/// The append-only audit log (#115), read-only. The caller sees the entries stored at the tenants where they hold
/// <c>audit.read</c> and their descendants; deployment-level entries only if they're a Superadministrator. Entries are
/// top-level resources because deployment-level ones have no tenant.
/// </summary>
[ApiController]
[Route("api/v1/audit-entries")]
public sealed class AuditEntriesController(Signal9DbContext db) : ControllerBase
{
    /// <summary>Lists the audit entries the caller can read, newest first.</summary>
    [RequireScope(Scopes.Audit.Read)]
    [HttpGet]
    [ProducesResponseType<PagedResult<AuditEntryDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<AuditEntryDto>> List([FromQuery] PageQuery paging, CancellationToken cancellationToken)
    {
        var page = await db.AuditEntries.AsNoTracking()
            .OrderByDescending(e => e.Sequence)
            .ToPagedResultAsync(paging, e => e, cancellationToken);
        var names = await AuditNames.ResolveAsync(db, page.Items, cancellationToken);
        return new PagedResult<AuditEntryDto>([.. page.Items.Select(names.ToDto)], page.Page, page.PageSize, page.TotalCount);
    }

    [RequireScope(Scopes.Audit.Read)]
    [HttpGet("{auditEntryId:guid}", Name = nameof(GetAuditEntry))]
    [OptimisticConcurrency]
    [ProducesResponseType<AuditEntryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AuditEntryDto>> GetAuditEntry(Guid auditEntryId, CancellationToken cancellationToken)
    {
        var entry = await db.AuditEntries.AsNoTracking().SingleOrDefaultAsync(e => e.Id == auditEntryId, cancellationToken);
        if (entry is null)
            return NotFound();
        var dto = (await AuditNames.ResolveAsync(db, [entry], cancellationToken)).ToDto(entry);
        // An entry never changes, but the names resolved beside it can.
        return this.Conditional(dto, ConditionalRequests.ETag(0, dto.Id, dto.TenantName, dto.DeviceName,
            dto.UserDisplayName, dto.RoleName, dto.Actor.UserId, dto.Actor.DisplayName));
    }
}

/// <summary>
/// The names beside a page of entries, resolved when they're read, through the tenant filter: a name the reader
/// couldn't otherwise see stays unresolved. An entry about a device that can't be resolved (erased, or outside the
/// reader's reach) returns IDs only, so no name tied to the device survives its erasure (#63, Audit).
/// </summary>
internal sealed class AuditNames
{
    private Dictionary<Guid, string> _tenants = [];
    private Dictionary<Guid, string> _devices = [];
    private Dictionary<Guid, string> _users = [];
    private Dictionary<Guid, string> _roles = [];
    private Dictionary<string, (Guid Id, string Name)> _people = [];

    public static async Task<AuditNames> ResolveAsync(Signal9DbContext db, IReadOnlyCollection<AuditEntry> entries,
        CancellationToken cancellationToken)
    {
        var tenantIds = entries.Select(e => e.TenantId).OfType<Guid>().Distinct().ToList();
        var deviceIds = entries.Select(e => e.DeviceId).OfType<Guid>().Distinct().ToList();
        var userIds = entries.Select(e => e.UserId).OfType<Guid>().Distinct().ToList();
        var roleIds = entries.Select(e => e.RoleId).OfType<Guid>().Distinct().ToList();
        var people = entries.Where(e => e.ActorKind == AuditActorKind.Person).Select(e => e.ActorSubject).OfType<string>()
            .Select(s => s.Split(':')).Where(p => p.Length == 2 && Guid.TryParse(p[1], out _))
            .Select(p => Guid.Parse(p[1])).Distinct().ToList();

        var names = new AuditNames
        {
            _tenants = await db.Tenants.AsNoTracking().Where(t => tenantIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken),
            _devices = await db.Devices.AsNoTracking().Where(d => deviceIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.MachineName, cancellationToken),
            _roles = await db.Roles.AsNoTracking().Where(r => roleIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Name, cancellationToken),
        };
        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id) || people.Contains(u.EntraObjectId))
            .Select(u => new { u.Id, u.EntraTenantId, u.EntraObjectId, u.DisplayName })
            .ToListAsync(cancellationToken);
        names._users = users.Where(u => userIds.Contains(u.Id)).ToDictionary(u => u.Id, u => u.DisplayName);
        names._people = users.ToDictionary(u => PersonSubject.Format(u.EntraTenantId, u.EntraObjectId), u => (u.Id, u.DisplayName));
        return names;
    }

    public AuditEntryDto ToDto(AuditEntry entry)
    {
        // IDs only for an entry about a device whose name can't be resolved.
        var named = entry.DeviceId is not { } device || _devices.ContainsKey(device);
        string? Name(Dictionary<Guid, string> names, Guid? id) =>
            named && id is { } key && names.TryGetValue(key, out var name) ? name : null;
        (Guid Id, string Name)? person = entry.ActorKind == AuditActorKind.Person && entry.ActorSubject is { } subject
            && _people.TryGetValue(subject, out var found) ? found : null;

        return new AuditEntryDto(
            entry.Id,
            entry.OccurredAt.AsUtcOffset(),
            entry.Action,
            entry.Outcome,
            new AuditActorDto(entry.ActorKind, entry.ActorSubject, entry.ActorClientId, entry.ActorSuperadministrator,
                person?.Id, named ? person?.Name : null),
            entry.TenantId, Name(_tenants, entry.TenantId),
            entry.TargetType, entry.TargetId,
            entry.DeviceId, Name(_devices, entry.DeviceId),
            entry.UserId, Name(_users, entry.UserId),
            entry.RoleId, Name(_roles, entry.RoleId),
            entry.Detail,
            entry.ClientIp);
    }
}
