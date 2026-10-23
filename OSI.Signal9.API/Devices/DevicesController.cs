using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Data;
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
    IHubContext<DashboardHub, IDashboardHubClient> dashboard) : ControllerBase
{
    /// <summary>Lists devices across tenants, with optional filters.</summary>
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

    [HttpGet("{deviceId:guid}", Name = nameof(GetDevice))]
    [ProducesResponseType<DeviceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeviceDto>> GetDevice(Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await db.Devices.AsNoTracking().Include(d => d.ActiveAgent)
            .SingleOrDefaultAsync(d => d.Id == deviceId, cancellationToken);
        return device is null ? NotFound() : device.ToDto(presence);
    }

    /// <summary>
    /// Updates a device's group, tags and maintenance mode with a JSON merge patch (RFC 7396).
    /// Status is read-only: it comes from the active agent's check-ins.
    /// </summary>
    [HttpPatch("{deviceId:guid}")]
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

        UpdateDeviceRequest request;
        try
        {
            if (patch.ValueKind != JsonValueKind.Object)
                throw new JsonException("A merge patch for a device must be a JSON object.");
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
        await dashboard.Clients.All.DeviceChanged(dto);
        return dto;
    }

    /// <summary>Deletes a device along with its agents, commands and telemetry.</summary>
    // TODO(#89): deletion becomes anonymised erasure that keeps an anonymous record for alerts and history.
    [HttpDelete("{deviceId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid deviceId, CancellationToken cancellationToken)
    {
        var deleted = await db.Devices.Where(d => d.Id == deviceId).ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
            return NotFound();

        await dashboard.Clients.All.DeviceDeleted(deviceId);
        return NoContent();
    }
}
