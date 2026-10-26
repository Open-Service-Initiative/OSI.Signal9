using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
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
public sealed class UsersController(
    Signal9DbContext db, CallerAudit audit, CallerAccess access, IAssignedTenants caller, TimeProvider time) : ControllerBase
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
        audit.Record(UserEntry(AuditActions.UserCreated, user));
        if (!await db.TrySaveUniqueAsync(cancellationToken))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "User already exists",
                detail: "A user is already linked to this Entra identity.");

        this.SetETag(user.ETag());
        return CreatedAtRoute(nameof(GetUser), new { userId = user.Id }, user.ToDto());
    }

    /// <summary>
    /// Updates a user's editable fields with a JSON merge patch (RFC 7396). A user without role assignments can be
    /// edited with <c>users.write</c> (story 15). A user who holds role assignments can be edited only by a caller with
    /// <c>roleassignments.write</c> at every tenant where they hold one, including tenants outside the caller's reach
    /// (story 16, 403).
    /// </summary>
    [RequireScope(Scopes.Users.Write)]
    [HttpPatch("{userId:guid}")]
    [OptimisticConcurrency]
    [Consumes(MergePatch.MediaType)]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public Task<ActionResult<UserDto>> Update(Guid userId, [FromBody] JsonElement patch, CancellationToken cancellationToken) =>
        db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
            if (user is null)
                return (ActionResult<UserDto>)NotFound();
            // Locks the user against new role assignments (their foreign key takes a key-share lock on the user) until
            // the edit commits, so the check below can't miss one granted meanwhile. Reread for the current version.
            await db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "Users" WHERE "Id" = {userId} FOR UPDATE""", cancellationToken);
            await db.Entry(user).ReloadAsync(cancellationToken);
            if (this.Preconditions(user.ETag()) is { } refused)
                return refused;

            UpdateUserRequest request;
            try
            {
                request = MergePatch.Apply(new UpdateUserRequest { DisplayName = user.DisplayName }, patch);
            }
            catch (JsonException ex)
            {
                ModelState.AddModelError(string.Empty, ex.Message);
                return ValidationProblem(ModelState);
            }
            if (!TryValidateModel(request))
                return ValidationProblem(ModelState);
            if (!await MayEditAsync(userId, cancellationToken))
                return Problem(statusCode: StatusCodes.Status403Forbidden, title: "User holds role assignments",
                    detail: "Editing a user who holds role assignments needs authority to assign roles at every tenant where they hold one.");

            if (request.DisplayName != user.DisplayName)
            {
                user.DisplayName = request.DisplayName;
                audit.Record(UserEntry(AuditActions.UserUpdated, user));
                await db.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            this.SetETag(user.ETag());
            return user.ToDto();
        });

    /// <summary>
    /// Whether the caller may edit the user: they hold no role assignments, or the caller can assign roles at every
    /// tenant where they hold one (their roles there grant <c>roleassignments.write</c>, and so does their token).
    /// </summary>
    private async Task<bool> MayEditAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (caller.IsSuperadministrator)
            return true;
        var tenants = await db.RoleAssignments.AsNoTracking()
            .IgnoreTenantScope(TenantScopeBypass.UserRoleAssignmentGuard)
            .Where(a => a.UserId == userId)
            .Select(a => a.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (tenants.Count == 0)
            return true;
        if (!User.HasScope(Scopes.RoleAssignments.Write) || !access.Permits(Scopes.RoleAssignments.Write))
            return false;
        foreach (var tenantId in tenants)
        {
            if (!(await access.RoleScopesAtAsync(db, tenantId, cancellationToken)).Contains(Scopes.RoleAssignments.Write))
                return false;
        }
        return true;
    }

    private static AuditEntry UserEntry(string action, User user) => new()
    {
        Action = action,
        TenantId = user.TenantId,
        TargetType = "user",
        TargetId = user.Id,
        UserId = user.Id,
    };
}
