using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Server.Identity;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Tenants;

[ApiController]
[Route("api/v1/tenants")]
public sealed class TenantsController(
    Signal9DbContext db, TenantTree tree, IAssignedTenants caller, TimeProvider time, CallerAudit audit) : ControllerBase
{
    /// <summary>Lists tenants, optionally filtered by parent or status.</summary>
    [RequireScope(Scopes.Tenants.Read)]
    [HttpGet]
    [ProducesResponseType<PagedResult<TenantDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<TenantDto>> List(
        [FromQuery] PageQuery paging,
        [FromQuery] Guid? parentTenantId,
        [FromQuery] TenantStatus? status,
        CancellationToken cancellationToken)
    {
        var query = db.Tenants.AsNoTracking();
        if (parentTenantId is not null)
            query = query.Where(t => t.ParentTenantId == parentTenantId);
        if (status is not null)
            query = query.Where(t => t.Status == status);

        var page = await query
            .OrderBy(t => t.Name)
            .Select(t => new { Tenant = t, DeviceCount = t.Devices.Count })
            .ToPagedResultAsync(paging, x => x, cancellationToken);
        var inheriting = await db.InheritingHoldAsync([.. page.Items.Select(x => x.Tenant.Id)], cancellationToken);
        return new PagedResult<TenantDto>(
            [.. page.Items.Select(x => x.Tenant.ToDto(x.DeviceCount, inheriting.Contains(x.Tenant.Id)))],
            page.Page, page.PageSize, page.TotalCount);
    }

    [RequireScope(Scopes.Tenants.Read)]
    [HttpGet("{tenantId:guid}", Name = nameof(GetTenant))]
    [OptimisticConcurrency]
    [ProducesResponseType<TenantDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantDto>> GetTenant(Guid tenantId, CancellationToken cancellationToken)
    {
        var found = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { Tenant = t, DeviceCount = t.Devices.Count })
            .SingleOrDefaultAsync(cancellationToken);

        if (found is null)
            return NotFound();
        var inherited = await db.InheritsHoldAsync(tenantId, cancellationToken);
        return this.Conditional(found.Tenant.ToDto(found.DeviceCount, inherited), found.Tenant.ETag(found.DeviceCount, inherited));
    }

    /// <summary>
    /// Creates a tenant under <c>parentTenantId</c>, or a root tenant without one. Only a Superadministrator creates
    /// root tenants: nobody else has authority above every tenant tree (403).
    /// </summary>
    [RequireScope(Scopes.Tenants.Write)]
    [HttpPost]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<TenantDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TenantDto>> Create(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        if (this.CollectionPreconditions() is { } refused)
            return refused;
        if (request.ParentTenantId is null && !caller.IsSuperadministrator)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Superadministrator required",
                detail: "Only a Superadministrator can create a root tenant.");

        // A parent the caller can't create tenants beneath is treated as missing, so nothing is learned about it.
        if (request.ParentTenantId is { } parent && !await db.Tenants.AnyAsync(t => t.Id == parent, cancellationToken))
            return ParentNotFound(request);

        var now = time.GetUtcNow().UtcDateTime;
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            ParentTenantId = request.ParentTenantId,
            Name = request.Name,
            Description = request.Description,
            ContactEmail = request.ContactEmail,
            ContactPhone = request.ContactPhone,
            MaxDevices = request.MaxDevices,
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        switch (await tree.AddAsync(tenant, cancellationToken))
        {
            case TenantTreeError.ParentNotFound:
                return ParentNotFound(request);
            case TenantTreeError.TooDeep:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Tenant tree too deep",
                    detail: "The parent tenant is already at the maximum depth of the tenant tree.");
        }

        var holdInherited = await db.InheritsHoldAsync(tenant.Id, cancellationToken);
        this.SetETag(tenant.ETag(deviceCount: 0, holdInherited));
        return CreatedAtRoute(nameof(GetTenant), new { tenantId = tenant.Id }, tenant.ToDto(deviceCount: 0, holdInherited));
    }

    private ActionResult ParentNotFound(CreateTenantRequest request)
    {
        ModelState.AddModelError(nameof(request.ParentTenantId), "Parent tenant does not exist.");
        return ValidationProblem(ModelState);
    }

    /// <summary>
    /// Updates a tenant's editable fields with a JSON merge patch (RFC 7396). Status is read-only: it changes through
    /// the <c>:suspend</c>, <c>:activate</c> and <c>:cancel</c> actions. Turning off <c>holdsNewAgentsForApproval</c>
    /// where an ancestor holds new agents is refused (409).
    /// </summary>
    [RequireScope(Scopes.Tenants.Write)]
    [HttpPatch("{tenantId:guid}")]
    [OptimisticConcurrency]
    [Consumes(MergePatch.MediaType)]
    [ProducesResponseType<TenantDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<ActionResult<TenantDto>> Update(Guid tenantId, [FromBody] JsonElement patch, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
            return NotFound();
        var deviceCount = await db.Devices.CountAsync(d => d.TenantId == tenantId, cancellationToken);
        var inherited = await db.InheritsHoldAsync(tenantId, cancellationToken);
        if (this.Preconditions(tenant.ETag(deviceCount, inherited)) is { } refused)
            return refused;

        var held = tenant.HoldsNewAgentsForApproval || inherited;
        UpdateTenantRequest request;
        try
        {
            request = MergePatch.Apply(
                new UpdateTenantRequest
                {
                    Name = tenant.Name,
                    Description = tenant.Description,
                    ContactEmail = tenant.ContactEmail,
                    ContactPhone = tenant.ContactPhone,
                    MaxDevices = tenant.MaxDevices,
                    HoldsNewAgentsForApproval = held,
                },
                patch);
        }
        catch (JsonException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return ValidationProblem(ModelState);
        }
        if (!TryValidateModel(request))
            return ValidationProblem(ModelState);

        tenant.Name = request.Name;
        tenant.Description = request.Description;
        tenant.ContactEmail = request.ContactEmail;
        tenant.ContactPhone = request.ContactPhone;
        tenant.MaxDevices = request.MaxDevices;
        // The hold changes only when the patch changes what applies here, so sending an inherited hold back is no change.
        if (request.HoldsNewAgentsForApproval != held)
        {
            if (inherited)
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Approval hold inherited",
                    detail: "An ancestor tenant holds new agents for approval, so the hold can't be turned off here.");
            tenant.HoldsNewAgentsForApproval = request.HoldsNewAgentsForApproval;
            audit.Record(new AuditEntry
            {
                Action = AuditActions.TenantApprovalHoldChanged,
                TenantId = tenant.Id,
                TargetType = "tenant",
                TargetId = tenant.Id,
                Detail = request.HoldsNewAgentsForApproval ? "on" : "off",
            });
        }
        return await SaveAsync(tenant, cancellationToken);
    }

    /// <summary>Suspends an active tenant. Only active tenants can be suspended (409).</summary>
    [RequireScope(Scopes.Tenants.Write)]
    [HttpPost("{tenantId:guid}:suspend")]
    [OptimisticConcurrency]
    [ProducesResponseType<TenantDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<TenantDto>> Suspend(Guid tenantId, CancellationToken cancellationToken) =>
        TransitionAsync(tenantId, TenantStatus.Suspended, [TenantStatus.Active], cancellationToken);

    /// <summary>Makes a suspended or cancelled tenant active again. An active tenant can't be activated (409).</summary>
    [RequireScope(Scopes.Tenants.Write)]
    [HttpPost("{tenantId:guid}:activate")]
    [OptimisticConcurrency]
    [ProducesResponseType<TenantDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<TenantDto>> Activate(Guid tenantId, CancellationToken cancellationToken) =>
        TransitionAsync(tenantId, TenantStatus.Active, [TenantStatus.Suspended, TenantStatus.Cancelled], cancellationToken);

    /// <summary>Cancels an active or suspended tenant. A cancelled tenant can't be cancelled again (409).</summary>
    [RequireScope(Scopes.Tenants.Write)]
    [HttpPost("{tenantId:guid}:cancel")]
    [OptimisticConcurrency]
    [ProducesResponseType<TenantDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<TenantDto>> Cancel(Guid tenantId, CancellationToken cancellationToken) =>
        TransitionAsync(tenantId, TenantStatus.Cancelled, [TenantStatus.Active, TenantStatus.Suspended], cancellationToken);

    private async Task<ActionResult<TenantDto>> TransitionAsync(
        Guid tenantId, TenantStatus to, TenantStatus[] allowedFrom, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
            return NotFound();
        var deviceCount = await db.Devices.CountAsync(d => d.TenantId == tenantId, cancellationToken);
        if (this.Preconditions(tenant.ETag(deviceCount, await db.InheritsHoldAsync(tenantId, cancellationToken))) is { } refused)
            return refused;
        if (!allowedFrom.Contains(tenant.Status))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Transition not allowed",
                detail: $"The tenant is {tenant.Status}, so it can't become {to}.");

        tenant.Status = to;
        return await SaveAsync(tenant, cancellationToken);
    }

    private async Task<ActionResult<TenantDto>> SaveAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        tenant.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        var deviceCount = await db.Devices.CountAsync(d => d.TenantId == tenant.Id, cancellationToken);
        var inherited = await db.InheritsHoldAsync(tenant.Id, cancellationToken);
        this.SetETag(tenant.ETag(deviceCount, inherited));
        return tenant.ToDto(deviceCount, inherited);
    }

    /// <summary>
    /// Shows what deleting the tenant would do now: the child tenants and live devices that block it, the role
    /// assignments at other tenants that would go with the users it owns, and any tenant that would be left with no
    /// Administrator at it or above it.
    /// </summary>
    [RequireScope(Scopes.Tenants.Delete)]
    [RequireAncestorAdministrator]
    [HttpGet("{tenantId:guid}:preview-delete")]
    [OptimisticConcurrency]
    [ProducesResponseType<TenantDeletionPreviewDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantDeletionPreviewDto>> PreviewDelete(Guid tenantId, TenantDeletion deletion,
        CancellationToken cancellationToken)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
            return NotFound();
        var preview = await deletion.PreviewAsync(tenantId, cancellationToken);
        return this.Conditional(preview, ConditionalRequests.ETag(0,
            string.Join(',', preview.ChildTenantIds), string.Join(',', preview.DeviceIds),
            string.Join(',', preview.OutsideRoleAssignments.Select(a => a.Id)), string.Join(',', preview.TenantsLeftWithoutAdministrator)));
    }

    /// <summary>
    /// Deletes a tenant with everything it owns: its enrollment tokens, invitations, API applications, the role
    /// assignments at it, and the users it owns with all their role assignments, wherever they are. Its child tenants
    /// and live devices must be deleted or transferred first (409, listing them), and it's refused (409) if it would leave
    /// a tenant with no Administrator at it or above it, except for a Superadministrator. See <c>:preview-delete</c>.
    /// Only an Administrator above the tenant, or a Superadministrator, can delete it. Its audit trail stays readable at
    /// the ancestors it had.
    /// </summary>
    [RequireScope(Scopes.Tenants.Delete)]
    [RequireAncestorAdministrator]
    [RequireRecentMfa]
    [HttpDelete("{tenantId:guid}")]
    [OptimisticConcurrency]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid tenantId, TenantDeletion deletion, CancellationToken cancellationToken)
    {
        var found = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { Tenant = t, DeviceCount = t.Devices.Count })
            .SingleOrDefaultAsync(cancellationToken);
        if (found is null)
            return NotFound();
        if (this.Preconditions(found.Tenant.ETag(found.DeviceCount, await db.InheritsHoldAsync(tenantId, cancellationToken))) is { } refused)
            return refused;

        var end = new SessionEnd("", PersonSubject.TryParse(User, out _, out _) ? AuditActorKind.Person : AuditActorKind.Application,
            User.FindFirst(OpenIddict.Abstractions.OpenIddictConstants.Claims.Subject)?.Value,
            HttpContext.Connection.RemoteIpAddress?.ToString());
        switch (await deletion.DeleteAsync(tenantId, found.Tenant.Version, end, cancellationToken))
        {
            case TenantDeletionResult.NotFound:
                return NotFound();
            case TenantDeletionResult.NotEmpty:
                return Refusal("Tenant not empty",
                    "Delete or transfer the tenant's child tenants and devices before deleting it.",
                    ("childTenantIds", deletion.Remaining!.ChildTenantIds), ("deviceIds", deletion.Remaining.DeviceIds));
            case TenantDeletionResult.WouldLeaveWithoutAdministrator:
                return Refusal("Last Administrator",
                    "Deleting the tenant deletes the users it owns, and would leave these tenants with no Administrator at them or above them; grant another Administrator there first.",
                    ("tenantsLeftWithoutAdministrator", deletion.Remaining!.TenantsLeftWithoutAdministrator));
        }

        return NoContent();
    }

    private ObjectResult Refusal(string title, string detail, params (string Name, IReadOnlyList<Guid> Ids)[] lists)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, StatusCodes.Status409Conflict, title, detail: detail);
        foreach (var (name, ids) in lists)
            problem.Extensions[name] = ids;
        return new ObjectResult(problem) { StatusCode = StatusCodes.Status409Conflict, ContentTypes = { "application/problem+json" } };
    }
}
