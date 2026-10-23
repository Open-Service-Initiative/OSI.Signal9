using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Users;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Users;

/// <summary>
/// Users: people known to Signal9, each owned by one tenant and linked to an Entra identity. Top-level, because a user
/// can be transferred to another owning tenant (#93).
/// </summary>
[ApiController]
[Route("api/v1/users")]
public sealed class UsersController(Signal9DbContext db, TimeProvider time) : ControllerBase
{
    /// <summary>Lists users, optionally only those owned by one tenant.</summary>
    [RequireScope(Scopes.Users.Read)]
    [HttpGet]
    [ProducesResponseType<PagedResult<UserDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<UserDto>> List([FromQuery] PageQuery paging, [FromQuery] Guid? tenantId,
        CancellationToken cancellationToken)
    {
        var query = db.Users.AsNoTracking();
        if (tenantId is not null)
            query = query.Where(u => u.TenantId == tenantId);
        return await query.OrderBy(u => u.DisplayName).ThenBy(u => u.Id)
            .ToPagedResultAsync(paging, u => u.ToDto(), cancellationToken);
    }

    [RequireScope(Scopes.Users.Read)]
    [HttpGet("{userId:guid}", Name = nameof(GetUser))]
    [OptimisticConcurrency]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UserDto>> GetUser(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user is null ? NotFound() : this.Conditional(user.ToDto(), user.ETag());
    }

    /// <summary>
    /// Creates a user owned by a tenant, linked to an Entra identity (<c>tid</c>, <c>oid</c>). An Entra identity links
    /// to only one user (409).
    /// </summary>
    [RequireScope(Scopes.Users.Write)]
    [HttpPost]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (this.Preconditions(current: null) is { } refused)
            return refused;
        // A tenant outside the caller's reach is treated as missing, so nothing is learned about it.
        if (!await db.Tenants.AnyAsync(t => t.Id == request.TenantId, cancellationToken))
        {
            ModelState.AddModelError(nameof(request.TenantId), "Tenant does not exist.");
            return ValidationProblem(ModelState);
        }

        var user = new User
        {
            Id = Guid.CreateVersion7(),
            TenantId = request.TenantId!.Value,
            EntraTenantId = request.EntraTenantId!.Value,
            EntraObjectId = request.EntraObjectId!.Value,
            DisplayName = request.DisplayName,
            CreatedAt = time.GetUtcNow().UtcDateTime,
        };
        db.Users.Add(user);
        if (!await db.TrySaveUniqueAsync(cancellationToken))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "User already exists",
                detail: "A user is already linked to this Entra identity.");

        this.SetETag(user.ETag());
        return CreatedAtRoute(nameof(GetUser), new { userId = user.Id }, user.ToDto());
    }
}
