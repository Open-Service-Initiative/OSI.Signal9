using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Roles;

/// <summary>
/// Role definitions: named bundles of scopes, stored as data (ADR-0010). Roles belong to the deployment rather than a
/// tenant, so a caller needs the scope from a role at any tenant. Only the built-in roles exist until custom roles get
/// their own spec, and they can't be changed (story 77).
/// </summary>
[ApiController]
[Route("api/v1/roles")]
public sealed class RolesController(Signal9DbContext db, IAssignedTenants caller) : ControllerBase
{
    [RequireScope(Scopes.Roles.Read)]
    [HttpGet]
    [ProducesResponseType<PagedResult<RoleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<PagedResult<RoleDto>>> List([FromQuery] PageQuery paging, CancellationToken cancellationToken)
    {
        if (NotGranted() is { } refused)
            return refused;
        return await db.Roles.AsNoTracking().OrderBy(r => r.Name)
            .ToPagedResultAsync(paging, r => r.ToDto(), cancellationToken);
    }

    [RequireScope(Scopes.Roles.Read)]
    [HttpGet("{roleId:guid}", Name = nameof(GetRole))]
    [OptimisticConcurrency]
    [ProducesResponseType<RoleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<RoleDto>> GetRole(Guid roleId, CancellationToken cancellationToken)
    {
        if (NotGranted() is { } refused)
            return refused;
        var role = await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        return role is null ? NotFound() : this.Conditional(role.ToDto(), role.ETag());
    }

    /// <summary>Would update a custom role. Built-in roles can't be changed (409).</summary>
    [RequireScope(Scopes.Roles.Write)]
    [HttpPatch("{roleId:guid}")]
    [OptimisticConcurrency]
    [Consumes(MergePatch.MediaType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Update(Guid roleId, [FromBody] JsonElement patch, CancellationToken cancellationToken) =>
        RefuseBuiltInAsync(roleId, cancellationToken);

    /// <summary>Would delete a custom role. Built-in roles can't be deleted (409).</summary>
    [RequireScope(Scopes.Roles.Write)]
    [HttpDelete("{roleId:guid}")]
    [OptimisticConcurrency]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IActionResult> Delete(Guid roleId, CancellationToken cancellationToken) =>
        RefuseBuiltInAsync(roleId, cancellationToken);

    // Every role is built in until custom roles exist.
    private async Task<IActionResult> RefuseBuiltInAsync(Guid roleId, CancellationToken cancellationToken)
    {
        if (NotGranted() is { } refused)
            return refused;
        var role = await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
            return NotFound();
        if (this.Preconditions(role.ETag()) is { } failed)
            return failed;
        return Problem(statusCode: StatusCodes.Status409Conflict, title: "Built-in role",
            detail: $"{role.Name} is a built-in role, which can't be changed or deleted.");
    }

    /// <summary>403 unless a role at some tenant grants the endpoint's scope (and the token carries it).</summary>
    private ObjectResult? NotGranted() => caller.IsSuperadministrator || caller.TenantIds.Count > 0 ? null
        : Problem(statusCode: StatusCodes.Status403Forbidden, title: "Not granted",
            detail: "None of your role assignments grants this.");
}
