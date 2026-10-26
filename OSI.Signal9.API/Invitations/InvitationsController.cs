using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Infrastructure;
using OpenIddict.Abstractions;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Invitations;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Invitations;

/// <summary>
/// Invitations, at the tenant whose role they offer (#121, stories 28-37). Offering a role is granting a role assignment
/// in advance, so invitations take the role assignment scopes and the same rule against escalation. The link is returned
/// once, by the request that creates the invitation; only its reference's hash is kept. People redeem it at auth.
/// </summary>
[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/invitations")]
public sealed class InvitationsController(
    Signal9DbContext db, CallerAudit audit, CallerAccess access, IAssignedTenants caller, IdentityOptions identity,
    TimeProvider time) : ControllerBase
{
    /// <summary>
    /// Lists the tenant's invitations, newest first, and by default those of its descendants too
    /// (<c>includeDescendants</c>), as far as the caller can see. Redeemed, expired and revoked ones are included.
    /// </summary>
    [RequireScope(Scopes.RoleAssignments.Read)]
    [HttpGet]
    [ProducesResponseType<PagedResult<InvitationDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<InvitationDto>>> List(Guid tenantId, [FromQuery] PageQuery paging,
        CancellationToken cancellationToken, [FromQuery] bool includeDescendants = true)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();

        var query = db.Invitations.AsNoTracking();
        query = includeDescendants
            ? query.Where(i => db.TenantClosures.Any(c => c.AncestorId == tenantId && c.DescendantId == i.TenantId))
            : query.Where(i => i.TenantId == tenantId);
        var now = Now;
        return await query.OrderByDescending(i => i.CreatedAt).ThenBy(i => i.Id)
            .ToPagedResultAsync(paging, i => i.ToDto(now, null), cancellationToken);
    }

    [RequireScope(Scopes.RoleAssignments.Read)]
    [HttpGet("{invitationId:guid}", Name = nameof(GetInvitation))]
    [OptimisticConcurrency]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InvitationDto>> GetInvitation(Guid tenantId, Guid invitationId, CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations.AsNoTracking()
            .SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == invitationId, cancellationToken);
        return invitation is null ? NotFound() : this.Conditional(invitation.ToDto(Now), invitation.ETag(Now));
    }

    /// <summary>
    /// Invites a person to one role at the tenant (story 28): optionally an existing user within the caller's reach
    /// (story 35), and optionally only someone signing in from one Entra directory (story 33). The role must be within
    /// the caller's own access at the tenant, the scopes both their roles there and their token grant (403). It can be
    /// redeemed once, within 72 hours. The response carries the link, which can't be read again.
    /// </summary>
    [RequireScope(Scopes.RoleAssignments.Write)]
    [HttpPost]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InvitationDto>> Create(Guid tenantId, CreateInvitationRequest request,
        CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();
        if (this.Preconditions(current: null) is { } refused)
            return refused;
        var role = await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.RoleId, cancellationToken);
        if (role is null)
            ModelState.AddModelError(nameof(request.RoleId), "Role does not exist.");
        if (request.UserId is { } userId)
        {
            // A user outside the caller's reach is treated as missing, so nothing is learned about it.
            var directory = await db.Users.AsNoTracking().Where(u => u.Id == userId)
                .Select(u => (Guid?)u.EntraTenantId).SingleOrDefaultAsync(cancellationToken);
            if (directory is null)
                ModelState.AddModelError(nameof(request.UserId), "User does not exist.");
            else if (request.EntraTenantId is { } pinned && pinned != directory)
                ModelState.AddModelError(nameof(request.EntraTenantId), "The user signs in from another directory.");
        }
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);
        if (!caller.IsSuperadministrator)
        {
            var own = await access.RoleScopesAtAsync(db, tenantId, cancellationToken);
            var beyond = role!.Scopes.Where(scope => !own.Contains(scope) || !User.HasScope(scope)).ToList();
            if (beyond.Count > 0)
                return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Role higher than your own",
                    detail: $"You can offer only roles within your own access at this tenant; {role.Name} also grants {string.Join(", ", beyond)}.");
        }

        var now = Now;
        var reference = InvitationReference.New();
        var invitation = new Invitation
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            TenantId = tenantId,
            RoleId = role!.Id,
            UserId = request.UserId,
            EntraTenantId = request.EntraTenantId,
            ReferenceHash = InvitationReference.Hash(reference),
            CreatedAt = now,
            ExpiresAt = now + Invitation.Lifetime,
        };
        db.Invitations.Add(invitation);
        Record(AuditActions.InvitationCreated, invitation);
        await db.SaveChangesAsync(cancellationToken);

        Response.NoStore();
        this.SetETag(invitation.ETag(now));
        return CreatedAtRoute(nameof(GetInvitation), new { tenantId, invitationId = invitation.Id },
            invitation.ToDto(now, InvitationReference.Link(identity.Issuer, reference)));
    }

    /// <summary>
    /// Withdraws the invitation (story 36): its link stops working at once. An invitation already redeemed or revoked
    /// can't be revoked (409); the role assignment a redemption granted is revoked as a role assignment.
    /// </summary>
    [RequireScope(Scopes.RoleAssignments.Write)]
    [HttpPost("{invitationId:guid}:revoke")]
    [OptimisticConcurrency]
    [ProducesResponseType<InvitationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InvitationDto>> Revoke(Guid tenantId, Guid invitationId, CancellationToken cancellationToken)
    {
        var invitation = await db.Invitations
            .SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Id == invitationId, cancellationToken);
        if (invitation is null)
            return NotFound();
        var now = Now;
        if (this.Preconditions(invitation.ETag(now)) is { } refused)
            return refused;
        if (invitation.StatusAt(now) is InvitationStatus.Redeemed or InvitationStatus.Revoked)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Transition not allowed",
                detail: $"The invitation is {invitation.StatusAt(now).ToString().ToLowerInvariant()}.");

        invitation.RevokedAt = now;
        Record(AuditActions.InvitationRevoked, invitation);
        // A redemption racing this one changes the row version, so only one of them is saved.
        await db.SaveChangesAsync(cancellationToken);

        this.SetETag(invitation.ETag(now));
        return invitation.ToDto(now);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private void Record(string action, Invitation invitation) => audit.Record(new AuditEntry
    {
        Action = action,
        TenantId = invitation.TenantId,
        TargetType = "invitation",
        TargetId = invitation.Id,
        UserId = invitation.UserId,
        RoleId = invitation.RoleId,
    });

    // A response carrying the link must not be stored by any cache (RFC 9111 §5.2.2.5), as for OAuth tokens (RFC 6749 §5.1).
}
