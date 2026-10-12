using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Data;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Tenants;

[ApiController]
[Route("api/tenants")]
public sealed class TenantsController(Signal9DbContext db, TimeProvider time) : ControllerBase
{
    /// <summary>Lists tenants, optionally filtered by parent or status.</summary>
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
            .Select(t => new { Tenant = t, AgentCount = t.Agents.Count })
            .ToPagedResultAsync(paging, x => x.Tenant.ToDto(x.AgentCount), cancellationToken);
        return result;
    }

    [HttpGet("{tenantId:guid}", Name = nameof(GetTenant))]
    [ProducesResponseType<TenantDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantDto>> GetTenant(Guid tenantId, CancellationToken cancellationToken)
    {
        var found = await db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { Tenant = t, AgentCount = t.Agents.Count })
            .SingleOrDefaultAsync(cancellationToken);

        return found is null ? NotFound() : found.Tenant.ToDto(found.AgentCount);
    }

    [HttpPost]
    [Consumes("application/json")]
    [ProducesResponseType<TenantDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<TenantDto>> Create(CreateTenantRequest request, CancellationToken cancellationToken)
    {
        if (await db.Tenants.AnyAsync(t => t.Code == request.Code, cancellationToken))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Tenant code already in use",
                detail: $"A tenant with code '{request.Code}' already exists.");

        if (request.ParentTenantId is { } parentId)
        {
            var parent = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == parentId, cancellationToken);
            if (parent is null)
                ModelState.AddModelError(nameof(request.ParentTenantId), "Parent tenant does not exist.");
            else if (parent.ParentTenantId is not null)
                ModelState.AddModelError(nameof(request.ParentTenantId), "Tenants can only be nested one level deep.");
            if (!ModelState.IsValid)
                return ValidationProblem(ModelState);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            ParentTenantId = request.ParentTenantId,
            Code = request.Code,
            Name = request.Name,
            Description = request.Description,
            ContactEmail = request.ContactEmail,
            ContactPhone = request.ContactPhone,
            Plan = request.Plan,
            MaxAgents = request.MaxAgents,
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync(cancellationToken);

        return CreatedAtRoute(nameof(GetTenant), new { tenantId = tenant.Id }, tenant.ToDto(agentCount: 0));
    }

    [HttpPut("{tenantId:guid}")]
    [Consumes("application/json")]
    [ProducesResponseType<TenantDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<TenantDto>> Update(Guid tenantId, UpdateTenantRequest request, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
            return NotFound();

        tenant.Name = request.Name;
        tenant.Description = request.Description;
        tenant.ContactEmail = request.ContactEmail;
        tenant.ContactPhone = request.ContactPhone;
        tenant.Plan = request.Plan;
        tenant.MaxAgents = request.MaxAgents;
        tenant.Status = request.Status;
        tenant.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);

        var agentCount = await db.Agents.CountAsync(a => a.TenantId == tenantId, cancellationToken);
        return tenant.ToDto(agentCount);
    }

    /// <summary>Deletes a tenant. Tenants that still own agents or client tenants cannot be deleted.</summary>
    [HttpDelete("{tenantId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
            return NotFound();

        if (await db.Agents.AnyAsync(a => a.TenantId == tenantId, cancellationToken))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Tenant has agents",
                detail: "Delete or move the tenant's agents before deleting the tenant.");
        if (await db.Tenants.AnyAsync(t => t.ParentTenantId == tenantId, cancellationToken))
            return Problem(statusCode: StatusCodes.Status409Conflict, title: "Tenant has client tenants",
                detail: "Delete the tenant's client tenants before deleting it.");

        db.Tenants.Remove(tenant);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}
