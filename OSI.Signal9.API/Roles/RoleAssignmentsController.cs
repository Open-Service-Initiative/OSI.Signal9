using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.API.Tenants;
using OpenIddict.Abstractions;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Roles;

/// <summary>
/// Role assignments, at the tenant they grant the role at: each covers that tenant and its descendants (ADR-0010).
/// </summary>
[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/role-assignments")]
public sealed class RoleAssignmentsController(
    Signal9DbContext db, CallerAudit audit, CallerAccess access, IAssignedTenants caller, TimeProvider time,
    SignInSessions sessions) : ControllerBase
{
    /// <summary>Lists the role assignments made at the tenant.</summary>
    [RequireScope(Scopes.RoleAssignments.Read)]
    [HttpGet]
    [ProducesResponseType<PagedResult<RoleAssignmentDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<RoleAssignmentDto>>> List(Guid tenantId, [FromQuery] PageQuery paging,
        CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();
        return await db.RoleAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id)
            .ToPagedResultAsync(paging, a => a.ToDto(), cancellationToken);
    }

    [RequireScope(Scopes.RoleAssignments.Read)]
    [HttpGet("{roleAssignmentId:guid}", Name = nameof(GetRoleAssignment))]
    [OptimisticConcurrency]
    [ProducesResponseType<RoleAssignmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleAssignmentDto>> GetRoleAssignment(Guid tenantId, Guid roleAssignmentId,
        CancellationToken cancellationToken)
    {
        var assignment = await db.RoleAssignments.AsNoTracking()
            .SingleOrDefaultAsync(a => a.TenantId == tenantId && a.Id == roleAssignmentId, cancellationToken);
        return assignment is null ? NotFound() : this.Conditional(assignment.ToDto(), assignment.ETag());
    }

    /// <summary>
    /// Gives a user a role at the tenant (story 17). The user must be within the caller's reach, and the role no higher
    /// than the caller's own access at the tenant, the scopes both their roles there and their token grant (story 18,
    /// 403). The same assignment can't be made twice (409).
    /// </summary>
    [RequireScope(Scopes.RoleAssignments.Write)]
    [HttpPost]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<RoleAssignmentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RoleAssignmentDto>> Create(Guid tenantId, CreateRoleAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();
        if (this.Preconditions(current: null) is { } refused)
            return refused;
        if (!await db.Users.AnyAsync(u => u.Id == request.UserId, cancellationToken))
            ModelState.AddModelError(nameof(request.UserId), "User does not exist.");
        var role = await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.RoleId, cancellationToken);
        if (role is null)
            ModelState.AddModelError(nameof(request.RoleId), "Role does not exist.");
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        if (!caller.IsSuperadministrator)
        {
            var own = await access.RoleScopesAtAsync(db, tenantId, cancellationToken);
            var beyond = role!.Scopes.Where(scope => !own.Contains(scope) || !User.HasScope(scope)).ToList();
            if (beyond.Count > 0)
                return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Role higher than your own",
                    detail: $"You can grant only roles within your own access at this tenant; {role.Name} also grants {string.Join(", ", beyond)}.");
        }

        var assignment = new RoleAssignment
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            UserId = request.UserId!.Value,
            RoleId = request.RoleId!.Value,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.RoleAssignments.Add(assignment);
        audit.Record(new AuditEntry
        {
            Action = AuditActions.RoleAssignmentCreated,
            TenantId = tenantId,
            TargetType = "role-assignment",
            TargetId = assignment.Id,
            UserId = assignment.UserId,
            RoleId = assignment.RoleId,
        });
        if (!await db.TrySaveUniqueAsync(cancellationToken))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Role assignment already exists",
                detail: "The user already has this role at this tenant.");

        this.SetETag(assignment.ETag());
        return CreatedAtRoute(nameof(GetRoleAssignment), new { tenantId, roleAssignmentId = assignment.Id }, assignment.ToDto());
    }

    /// <summary>
    /// Revokes a role assignment, which takes effect on its holder's next request. A peer's Administrator assignment at
    /// a tenant where the caller is an Administrator can be revoked only by an Administrator above it (story 22, 403),
    /// and an Administrator assignment can't be revoked when no other Administrator would remain at the tenant or above
    /// it (story 23, 409), except by a Superadministrator. To change someone's role, grant the new one and revoke the
    /// old one.
    /// </summary>
    [RequireScope(Scopes.RoleAssignments.Write)]
    [HttpDelete("{roleAssignmentId:guid}")]
    [OptimisticConcurrency]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid tenantId, Guid roleAssignmentId, CancellationToken cancellationToken)
    {
        var found = await db.RoleAssignments.AsNoTracking()
            .Where(a => a.TenantId == tenantId && a.Id == roleAssignmentId)
            .Select(a => new { Assignment = a, a.User!.EntraTenantId, a.User.EntraObjectId })
            .SingleOrDefaultAsync(cancellationToken);
        if (found is null)
            return NotFound();
        if (this.Preconditions(found.Assignment.ETag()) is { } refused)
            return refused;

        var administrator = found.Assignment.RoleId == BuiltInRoles.Administrator.Id;
        var own = PersonSubject.TryParse(User, out var entraTenantId, out var entraObjectId)
            && found.EntraTenantId == entraTenantId && found.EntraObjectId == entraObjectId;
        if (administrator && !own && !await AncestorAdministrator.AdmitsAsync(HttpContext, tenantId))
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Peer Administrator",
                detail: "Only an Administrator above this tenant can revoke another Administrator at it.");

        var lockedOut = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            // The tree lock: tenant moves and other revocations change who administers the tenant.
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({TenantTree.LockKey})", cancellationToken);
            if (administrator && !caller.IsSuperadministrator && !await OtherAdministratorRemainsAsync(found.Assignment, cancellationToken))
                return true;

            var assignment = found.Assignment;
            db.RoleAssignments.Attach(assignment);
            db.RoleAssignments.Remove(assignment);
            audit.Record(new AuditEntry
            {
                Action = AuditActions.RoleAssignmentDeleted,
                TenantId = tenantId,
                TargetType = "role-assignment",
                TargetId = assignment.Id,
                UserId = assignment.UserId,
                RoleId = assignment.RoleId,
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return false;
        });
        if (lockedOut)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Last Administrator",
                detail: "This is the last Administrator at this tenant or above it; grant another Administrator first.");

        // Removing a user's last role assignment cuts their access at once, open sessions included (story 27).
        var person = PersonSubject.Format(found.EntraTenantId, found.EntraObjectId);
        if (!await sessions.HoldsRoleAssignmentAsync(person, cancellationToken))
        {
            await sessions.EndForPersonAsync(person, new SessionEnd("No role assignment left",
                PersonSubject.TryParse(User, out _, out _) ? AuditActorKind.Person : AuditActorKind.Application,
                User.FindFirst(OpenIddictConstants.Claims.Subject)?.Value,
                HttpContext.Connection.RemoteIpAddress?.ToString()), cancellationToken);
        }
        return NoContent();
    }

    // Whether another Administrator assignment covers the tenant: at it or at an ancestor, which may lie outside the
    // caller's reach.
    private Task<bool> OtherAdministratorRemainsAsync(RoleAssignment revoked, CancellationToken cancellationToken) =>
        db.RoleAssignments.AsNoTracking()
            .IgnoreTenantScope(TenantScopeBypass.AdministratorLockoutGuard)
            .AnyAsync(a => a.Id != revoked.Id && a.RoleId == BuiltInRoles.Administrator.Id
                && db.TenantClosures.Any(c => c.DescendantId == revoked.TenantId && c.AncestorId == a.TenantId), cancellationToken);
}
