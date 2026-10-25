using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.API.Agents;

internal static class ActiveAgents
{
    /// <summary>
    /// Finds the device whose active agent is <paramref name="agentId"/>, for requests an agent sends about itself. The
    /// agent is the one the access token names; a request naming any other agent is refused (story 100).
    /// </summary>
    /// <returns>
    /// The tracked device with its active agent, or the 403 (not the caller), 404 (unknown agent) or 409 (replaced
    /// agent) to return.
    /// </returns>
    public static async Task<(Device? Device, IActionResult? Refusal)> FindDeviceOfActiveAgentAsync(
        this Signal9DbContext db, ControllerBase controller, Guid agentId, CancellationToken cancellationToken)
    {
        if (controller.User.AgentId() != agentId)
            return (null, controller.Forbid());

        var agent = await db.Agents.IgnoreTenantScope(TenantScopeBypass.AgentSelfService).Include(a => a.Device!).ThenInclude(d => d.ActiveAgent)
            .SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
            return (null, controller.NotFound());
        if (agent.Device!.ActiveAgentId != agent.Id)
            return (null, controller.Problem(statusCode: StatusCodes.Status409Conflict, title: "Agent replaced",
                detail: "This agent is no longer its device's active agent."));
        return (agent.Device, null);
    }

    /// <summary>
    /// Records an agent's check-in, and any facts it reported, straight to the database. Check-ins are last-write-wins
    /// and never fail on optimistic concurrency, so an agent isn't refused because someone edited its device meanwhile.
    /// </summary>
    public static async Task RecordCheckInAsync(
        this Signal9DbContext db, Guid agentId, DateTime seenAt, CancellationToken cancellationToken,
        string? agentVersion = null, string? ipAddress = null)
    {
        await db.Agents.IgnoreTenantScope(TenantScopeBypass.AgentSelfService).Where(a => a.Id == agentId).ExecuteUpdateAsync(set => set
            .SetProperty(a => a.LastSeenAt, seenAt)
            .SetProperty(a => a.AgentVersion, a => agentVersion ?? a.AgentVersion), cancellationToken);
        if (ipAddress is not null)
            await db.Devices.IgnoreTenantScope(TenantScopeBypass.AgentSelfService).Where(d => d.ActiveAgentId == agentId).ExecuteUpdateAsync(set => set
                .SetProperty(d => d.IpAddress, ipAddress), cancellationToken);
    }
}
