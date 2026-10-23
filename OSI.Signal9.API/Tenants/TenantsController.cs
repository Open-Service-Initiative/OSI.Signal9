using Microsoft.AspNetCore.Mvc;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Server.Identity;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Tenants;

[ApiController]
[Route("api/v1/tenants")]
public sealed class TenantsController(Signal9DbContext db, TenantTree tree, TimeProvider time) : ControllerBase
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

        var result = await query
            .OrderBy(t => t.Name)
            .Select(t => new { Tenant = t, DeviceCount = t.Devices.Count })
            .ToPagedResultAsync(paging, x => x.Tenant.ToDto(x.DeviceCount), cancellationToken);
        return result;
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

        return found is null ? NotFound() : this.Conditional(found.Tenant.ToDto(found.DeviceCount), found.Tenant.ETag(found.DeviceCount));
    }

    [RequireScope(Scopes.Tenants.Write)]
    [HttpPost]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<TenantDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TenantDto>> Create(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        if (this.Preconditions(current: null) is { } refused)
            return refused;

        var now = time.GetUtcNow().UtcDateTime;
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            ParentTenantId = request.ParentTenantId,
            Name = request.Name,
            Description = request.Description,
            ContactEmail = request.ContactEmail,
            ContactPhone = request.ContactPhone,
            Plan = request.Plan,
            MaxDevices = request.MaxDevices,
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        switch (await tree.AddAsync(tenant, cancellationToken))
        {
            case TenantTreeError.ParentNotFound:
                ModelState.AddModelError(nameof(request.ParentTenantId), "Parent tenant does not exist.");
                return ValidationProblem(ModelState);
            case TenantTreeError.TooDeep:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Tenant tree too deep",
                    detail: "The parent tenant is already at the maximum depth of the tenant tree.");
        }

        this.SetETag(tenant.ETag(deviceCount: 0));
        return CreatedAtRoute(nameof(GetTenant), new { tenantId = tenant.Id }, tenant.ToDto(deviceCount: 0));
    }

    [RequireScope(Scopes.Tenants.Write)]
    [HttpPut("{tenantId:guid}")]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<TenantDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantDto>> Update(Guid tenantId, UpdateTenantRequest request, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
            return NotFound();
        var deviceCount = await db.Devices.CountAsync(d => d.TenantId == tenantId, cancellationToken);
        if (this.Preconditions(tenant.ETag(deviceCount)) is { } refused)
            return refused;

        tenant.Name = request.Name;
        tenant.Description = request.Description;
        tenant.ContactEmail = request.ContactEmail;
        tenant.ContactPhone = request.ContactPhone;
        tenant.Plan = request.Plan;
        tenant.MaxDevices = request.MaxDevices;
        tenant.Status = request.Status;
        tenant.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        deviceCount = await db.Devices.CountAsync(d => d.TenantId == tenantId, cancellationToken);
        this.SetETag(tenant.ETag(deviceCount));
        return tenant.ToDto(deviceCount);
    }

    /// <summary>Deletes a tenant. Tenants that still own devices or child tenants cannot be deleted.</summary>
    [RequireScope(Scopes.Tenants.Delete)]
    [HttpDelete("{tenantId:guid}")]
    [OptimisticConcurrency]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid tenantId, CancellationToken cancellationToken)
    {
        var found = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { Tenant = t, DeviceCount = t.Devices.Count })
            .SingleOrDefaultAsync(cancellationToken);
        if (found is null)
            return NotFound();
        if (this.Preconditions(found.Tenant.ETag(found.DeviceCount)) is { } refused)
            return refused;

        if (found.DeviceCount > 0)
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Tenant has devices",
                detail: "Delete or transfer the tenant's devices before deleting the tenant.");

        switch (await tree.RemoveAsync(tenantId, found.Tenant.Version, cancellationToken))
        {
            case TenantTreeError.TenantNotFound:
                return NotFound();
            case TenantTreeError.HasChildTenants:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Tenant has child tenants",
                    detail: "Delete or transfer the tenant's child tenants before deleting it.");
        }

        return NoContent();
    }
}
