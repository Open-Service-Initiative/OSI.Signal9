using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Enrollment;

/// <summary>
/// Enrollment tokens, at the tenant agents enroll into with them (#127, stories 92-94). The secret is returned once,
/// by the request that issues it (creation or <c>:rotate</c>), and only its hash is kept.
/// </summary>
[ApiController]
[Route("api/v1/tenants/{tenantId:guid}/enrollment-tokens")]
public sealed class EnrollmentTokensController(Signal9DbContext db, CallerAudit audit, TimeProvider time) : ControllerBase
{
    /// <summary>
    /// Lists the tenant's enrollment tokens, newest first, and by default those of its descendants too
    /// (<c>includeDescendants</c>), as far as the caller can see. Revoked and expired tokens are included.
    /// </summary>
    [RequireScope(Scopes.Enrollment.Read)]
    [HttpGet]
    [ProducesResponseType<PagedResult<EnrollmentTokenDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PagedResult<EnrollmentTokenDto>>> List(Guid tenantId, [FromQuery] PageQuery paging,
        CancellationToken cancellationToken, [FromQuery] bool includeDescendants = true)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();

        var query = db.EnrollmentTokens.AsNoTracking();
        query = includeDescendants
            ? query.Where(t => db.TenantClosures.Any(c => c.AncestorId == tenantId && c.DescendantId == t.TenantId))
            : query.Where(t => t.TenantId == tenantId);
        var now = Now;
        return await query.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .ToPagedResultAsync(paging, t => t.ToDto(now), cancellationToken);
    }

    [RequireScope(Scopes.Enrollment.Read)]
    [HttpGet("{enrollmentTokenId:guid}", Name = nameof(GetEnrollmentToken))]
    [OptimisticConcurrency]
    [ProducesResponseType<EnrollmentTokenDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EnrollmentTokenDto>> GetEnrollmentToken(Guid tenantId, Guid enrollmentTokenId,
        CancellationToken cancellationToken)
    {
        var token = await db.EnrollmentTokens.AsNoTracking()
            .SingleOrDefaultAsync(t => t.TenantId == tenantId && t.Id == enrollmentTokenId, cancellationToken);
        return token is null ? NotFound() : this.Conditional(token.ToDto(Now), token.ETag(Now));
    }

    /// <summary>
    /// Issues an enrollment token for the tenant. It's multi-use and never expires unless given <c>maxUses</c> (1 for
    /// single use) or <c>expiresAt</c>. The response carries the secret, which can't be read again.
    /// </summary>
    [RequireScope(Scopes.Enrollment.Write)]
    [HttpPost]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<EnrollmentTokenDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<EnrollmentTokenDto>> Create(Guid tenantId, CreateEnrollmentTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();
        if (this.CollectionPreconditions() is { } refused)
            return refused;
        var now = Now;
        if (request.ExpiresAt is { } expiresAt && expiresAt.UtcDateTime <= now)
        {
            ModelState.AddModelError(nameof(request.ExpiresAt), "Expiry must be in the future.");
            return ValidationProblem(ModelState);
        }

        var secret = EnrollmentSecret.New();
        var token = new EnrollmentToken
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            TenantId = tenantId,
            Name = request.Name,
            SecretHash = EnrollmentSecret.Hash(secret),
            MaxUses = request.MaxUses,
            ExpiresAt = request.ExpiresAt?.UtcDateTime,
            CreatedAt = now,
        };
        db.EnrollmentTokens.Add(token);
        Record(AuditActions.EnrollmentTokenCreated, token);
        await db.SaveChangesAsync(cancellationToken);

        Response.NoStore();
        this.SetETag(token.ETag(now));
        return CreatedAtRoute(nameof(GetEnrollmentToken), new { tenantId, enrollmentTokenId = token.Id },
            token.ToDto(now, secret));
    }

    /// <summary>
    /// Replaces the token's secret: the old one stops working at once, and the response carries the new one. Agents
    /// already enrolled with it are unaffected. A revoked token can't be rotated (409).
    /// </summary>
    [RequireScope(Scopes.Enrollment.Write)]
    [HttpPost("{enrollmentTokenId:guid}:rotate")]
    [OptimisticConcurrency]
    [ProducesResponseType<EnrollmentTokenDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EnrollmentTokenDto>> Rotate(Guid tenantId, Guid enrollmentTokenId,
        CancellationToken cancellationToken)
    {
        var token = await db.EnrollmentTokens
            .SingleOrDefaultAsync(t => t.TenantId == tenantId && t.Id == enrollmentTokenId, cancellationToken);
        if (token is null)
            return NotFound();
        var now = Now;
        if (this.Preconditions(token.ETag(now)) is { } refused)
            return refused;
        if (token.RevokedAt is not null)
            return Revoked();

        var secret = EnrollmentSecret.New();
        token.SecretHash = EnrollmentSecret.Hash(secret);
        token.RotatedAt = now;
        Record(AuditActions.EnrollmentTokenRotated, token);
        await db.SaveChangesAsync(cancellationToken);

        Response.NoStore();
        this.SetETag(token.ETag(now));
        return token.ToDto(now, secret);
    }

    /// <summary>
    /// Revokes the token for good: no agent can enroll with it again. Agents already enrolled with it are unaffected.
    /// A revoked token can't be revoked again (409).
    /// </summary>
    [RequireScope(Scopes.Enrollment.Write)]
    [HttpPost("{enrollmentTokenId:guid}:revoke")]
    [OptimisticConcurrency]
    [ProducesResponseType<EnrollmentTokenDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<EnrollmentTokenDto>> Revoke(Guid tenantId, Guid enrollmentTokenId,
        CancellationToken cancellationToken)
    {
        var token = await db.EnrollmentTokens
            .SingleOrDefaultAsync(t => t.TenantId == tenantId && t.Id == enrollmentTokenId, cancellationToken);
        if (token is null)
            return NotFound();
        var now = Now;
        if (this.Preconditions(token.ETag(now)) is { } refused)
            return refused;
        if (token.RevokedAt is not null)
            return Revoked();

        token.RevokedAt = now;
        Record(AuditActions.EnrollmentTokenRevoked, token);
        await db.SaveChangesAsync(cancellationToken);

        this.SetETag(token.ETag(now));
        return token.ToDto(now);
    }

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    private void Record(string action, EnrollmentToken token) => audit.Record(new AuditEntry
    {
        Action = action,
        TenantId = token.TenantId,
        TargetType = "enrollment-token",
        TargetId = token.Id,
    });

    private ObjectResult Revoked() => Problem(statusCode: StatusCodes.Status409Conflict, title: "Transition not allowed",
        detail: "The enrollment token is revoked.");

    // A response carrying a secret must not be stored by any cache (RFC 9111 §5.2.2.5), as for OAuth tokens (RFC 6749 §5.1).
}
