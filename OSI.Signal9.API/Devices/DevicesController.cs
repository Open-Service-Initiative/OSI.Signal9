using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using OSI.Signal9.API.Audit;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.API.Tenants;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Server.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Hubs;

namespace OSI.Signal9.API.Devices;

/// <summary>
/// Devices: the managed machines, which keep their history when their agent is replaced.
/// </summary>
[ApiController]
[Route("api/v1/devices")]
public sealed class DevicesController(
    Signal9DbContext db,
    DevicePresence presence,
    CallerAudit audit,
    DashboardEvents dashboard) : ControllerBase
{
    /// <summary>Lists devices across tenants, with optional filters.</summary>
    [RequireScope(Scopes.Devices.Read)]
    [HttpGet]
    [ProducesResponseType<PagedResult<DeviceDto>>(StatusCodes.Status200OK)]
    public async Task<PagedResult<DeviceDto>> List(
        [FromQuery] PageQuery paging,
        [FromQuery] Guid? tenantId,
        [FromQuery] DeviceStatus? status,
        [FromQuery] string? group,
        [FromQuery] string? search,
        CancellationToken cancellationToken)
    {
        var query = db.Devices.AsNoTracking().Include(d => d.ActiveAgent).AsQueryable();
        if (tenantId is not null)
            query = query.Where(d => d.TenantId == tenantId);
        if (status is not null)
            query = presence.WhereStatus(query, status.Value);
        if (!string.IsNullOrWhiteSpace(group))
            query = query.Where(d => d.GroupName == group);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(d => d.MachineName.Contains(search) || (d.IpAddress != null && d.IpAddress.Contains(search)));

        return await query
            .OrderBy(d => d.MachineName)
            .ThenBy(d => d.Id)
            .ToPagedResultAsync(paging, d => d.ToDto(presence), cancellationToken);
    }

    [RequireScope(Scopes.Devices.Read)]
    [HttpGet("{deviceId:guid}", Name = nameof(GetDevice))]
    [OptimisticConcurrency]
    [ProducesResponseType<DeviceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceDto>> GetDevice(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await db.Devices.AsNoTracking().Include(d => d.ActiveAgent)
            .SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        return device is null ? NotFound() : this.Conditional(device.ToDto(presence), device.ETag(presence));
    }

    /// <summary>
    /// Updates a device's group, tags and maintenance mode with a JSON merge patch (RFC 7396).
    /// Status is read-only: it comes from the active agent's check-ins.
    /// </summary>
    [RequireScope(Scopes.Devices.Write)]
    [HttpPatch("{deviceId:guid}")]
    [OptimisticConcurrency]
    [Consumes(MergePatch.MediaType)]
    [ProducesResponseType<DeviceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public async Task<ActionResult<DeviceDto>> Update(Guid deviceId, [FromBody] JsonElement patch, CancellationToken cancellationToken)
    {
        var device = await db.Devices.Include(d => d.ActiveAgent)
            .SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
            return NotFound();
        if (this.Preconditions(device.ETag(presence)) is { } refused)
            return refused;

        UpdateDeviceRequest request;
        try
        {
            request = MergePatch.Apply(
                new UpdateDeviceRequest { GroupName = device.GroupName, Tags = device.Tags, MaintenanceMode = device.MaintenanceMode },
                patch);
        }
        catch (JsonException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return ValidationProblem(ModelState);
        }
        if (!TryValidateModel(request))
            return ValidationProblem(ModelState);

        device.GroupName = string.IsNullOrWhiteSpace(request.GroupName) ? null : request.GroupName.Trim();
        device.Tags = request.Tags.Select(t => (t ?? string.Empty).Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        device.MaintenanceMode = request.MaintenanceMode;
        await db.SaveChangesAsync(cancellationToken);

        var dto = device.ToDto(presence);
        await dashboard.DeviceChangedAsync(device.TenantId, device.Id, cancellationToken);
        this.SetETag(device.ETag(presence));
        return dto;
    }

    /// <summary>
    /// Moves a device to another tenant (story 75). Its agent, telemetry and command history go with it, so from then on
    /// they're visible from the new tenant's subtree and not the old one's. Needs <c>devices.transfer</c> at both
    /// tenants: a device the caller can't transfer reads as missing (404), and so does a destination (400).
    /// </summary>
    /// <response code="400">The destination tenant is missing, or the caller can't transfer devices into it.</response>
    /// <response code="409">The device is already in that tenant, the tenant is at its device limit, or the device would
    /// join another root tenant tree that already holds a device for the same hardware (one device per machine per root
    /// tree).</response>
    [RequireScope(Scopes.Devices.Transfer)]
    [HttpPost("{deviceId:guid}:transfer")]
    [OptimisticConcurrency]
    [Consumes("application/json")]
    [ProducesResponseType<DeviceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status412PreconditionFailed)]
    public async Task<ActionResult<DeviceDto>> Transfer(Guid deviceId, TransferDeviceRequest request, CancellationToken cancellationToken)
    {
        // Under the tenant tree's lock, so neither a tree change nor another transfer can alter the destination's root
        // tree between the hardware check and the move. Inside the execution strategy, as every explicit transaction is.
        var attempt = 0;
        var result = await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
                db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({TenantTree.LockKey})", cancellationToken);
            var (refused, device, from) = await MoveAsync(deviceId, request.TenantId!.Value, cancellationToken);
            if (refused is not null)
                return (refused, device, from);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (refused, device, from);
        });
        if (result.refused is not null)
            return result.refused;

        var moved = result.device!;
        var dto = moved.ToDto(presence);
        // Both sides hear it: the former one, to drop the device, and the new one.
        await dashboard.DeviceChangedAsync([result.from, moved.TenantId], moved.Id, cancellationToken);
        this.SetETag(moved.ETag(presence));
        return dto;
    }

    // Moves the device to the tenant, with the audit entries recording it, unless the request is refused.
    private async Task<(ActionResult? refused, Device? device, Guid from)> MoveAsync(Guid deviceId, Guid tenantId, CancellationToken cancellationToken)
    {
        var device = await db.Devices.Include(d => d.ActiveAgent).SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
            return (NotFound(), null, default);
        if (this.Preconditions(device.ETag(presence)) is { } failed)
            return (failed, null, default);

        // A tenant the caller can't transfer devices into is treated as missing, so nothing is learned about it.
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant is null)
        {
            ModelState.AddModelError(nameof(TransferDeviceRequest.TenantId), "Tenant does not exist.");
            return (ValidationProblem(ModelState), null, default);
        }
        if (tenant.Id == device.TenantId)
            return (Conflict("Already in that tenant", "The device already belongs to that tenant."), null, default);
        // The caller can transfer into the tenant, so they see all of its devices.
        if (await db.Devices.CountAsync(d => d.TenantId == tenant.Id, cancellationToken) >= tenant.MaxDevices)
            return (Conflict("Device limit reached", $"Tenant '{tenant.Name}' allows at most {tenant.MaxDevices} devices."), null, default);
        if (await HardwareTakenAsync(device, tenant.Id, cancellationToken))
            return (Conflict("Machine already in that tenant tree",
                "The destination's root tenant tree already holds a device for the same hardware."), null, default);

        var from = device.TenantId;
        device.TenantId = tenant.Id;
        foreach (var (at, detail) in new[] { (from, $"to tenant {tenant.Id}"), (tenant.Id, $"from tenant {from}") })
            audit.Record(new AuditEntry
            {
                Action = AuditActions.DeviceTransferred,
                TenantId = at,
                TargetType = "device",
                TargetId = device.Id,
                DeviceId = device.Id,
                Detail = detail,
            });
        return (null, device, from);
    }

    /// <summary>
    /// Whether moving <paramref name="device"/> into <paramref name="tenantId"/> takes it into another root tenant tree
    /// that already holds a device sharing its SMBIOS UUID, or its manufacturer and serial (the identifiers #131
    /// matches on), wherever in that tree it is: one device per machine per root tree.
    /// </summary>
    private async Task<bool> HardwareTakenAsync(Device device, Guid tenantId, CancellationToken cancellationToken)
    {
        var (uuid, manufacturer, serial) = (device.SmbiosUuid, device.Manufacturer, device.SerialNumber);
        if (uuid is null && (manufacturer is null || serial is null))
            return false;
        var root = await RootOfAsync(tenantId, cancellationToken);
        if (root == await RootOfAsync(device.TenantId, cancellationToken))
            return false;

        return await db.Devices.IgnoreTenantScope(TenantScopeBypass.DeviceTransferHardwareCheck)
            .AnyAsync(d => d.Id != device.Id
                && db.TenantClosures.Any(c => c.AncestorId == root && c.DescendantId == d.TenantId)
                && ((uuid != null && d.SmbiosUuid == uuid)
                    || (serial != null && manufacturer != null && d.SerialNumber == serial && d.Manufacturer == manufacturer)),
                cancellationToken);
    }

    private Task<Guid> RootOfAsync(Guid tenantId, CancellationToken cancellationToken) =>
        db.TenantClosures.Where(c => c.DescendantId == tenantId)
            .OrderByDescending(c => c.Depth).Select(c => c.AncestorId).FirstAsync(cancellationToken);

    private ObjectResult Conflict(string title, string detail) =>
        Problem(statusCode: StatusCodes.Status409Conflict, title: title, detail: detail);

    /// <summary>Deletes a device along with its agents, commands and telemetry.</summary>
    // TODO(#89): deletion becomes anonymised erasure that keeps an anonymous record for alerts and history.
    [RequireScope(Scopes.Devices.Delete)]
    [HttpDelete("{deviceId:guid}")]
    [OptimisticConcurrency]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await db.Devices.AsNoTracking().Include(d => d.ActiveAgent)
            .SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        if (device is null)
            return NotFound();
        if (this.Preconditions(device.ETag(presence)) is { } refused)
            return refused;

        var deleted = await db.Devices.Where(d => d.Id == deviceId && d.Version == device.Version).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
            throw new DbUpdateConcurrencyException("The device changed or was deleted after it was read.");

        await dashboard.DeviceDeletedAsync(device.TenantId, deviceId, cancellationToken);
        return NoContent();
    }
}
