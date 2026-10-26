using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.API.Agents;

internal static class ActiveAgents
{
    /// <summary>
    /// Finds the agent <paramref name="agentId"/>, for requests an agent sends about itself, if it may still make them:
    /// it's its device's active agent, or it awaits an identification decision and has no device yet. The agent is the
    /// one the access token names; a request naming any other agent is refused (story 100).
    /// </summary>
    /// <returns>
    /// The tracked agent with its device (and the device's active agent) or pending identification, or the 403 (not the
    /// caller, or revoked), 404 (unknown agent) or 409 (replaced agent) to return.
    /// </returns>
    public static async Task<(Agent? Agent, IActionResult? Refusal)> FindActiveAgentAsync(
        this Signal9DbContext db, ControllerBase controller, Guid agentId, CancellationToken cancellationToken)
    {
        if (controller.User.AgentId() != agentId)
            return (null, controller.Forbid());

        var agent = await db.Agents.IgnoreTenantScope(TenantScopeBypass.AgentSelfService)
            .Include(a => a.Device!).ThenInclude(d => d.ActiveAgent).Include(a => a.PendingIdentification)
            .SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
            return (null, controller.NotFound());
        if (agent.RevokedAt is not null)
            return (null, controller.Forbid());
        if (agent.Device is { } device && device.ActiveAgentId != agent.Id)
            return (null, controller.Problem(statusCode: StatusCodes.Status409Conflict, title: "Agent replaced",
                detail: "This agent is no longer its device's active agent."));
        return (agent, null);
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
