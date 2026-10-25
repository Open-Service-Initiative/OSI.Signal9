using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Infrastructure;
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
public sealed class RoleAssignmentsController(Signal9DbContext db, CallerAudit audit, TimeProvider time) : ControllerBase
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
    /// Gives a user a role at the tenant. The user must be within the caller's reach; the same assignment can't be
    /// made twice (409).
    /// </summary>
    [RequireScope(Scopes.RoleAssignments.Write)]
    [HttpPost]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<RoleAssignmentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
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
        if (!await db.Roles.AnyAsync(r => r.Id == request.RoleId, cancellationToken))
            ModelState.AddModelError(nameof(request.RoleId), "Role does not exist.");
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

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
}
