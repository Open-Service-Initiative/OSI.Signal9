using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Data;

namespace OSI.Signal9.API.Agents;

internal static class ActiveAgents
{
    /// <summary>
    /// Finds the device whose active agent is <paramref name="agentId"/>, for requests an agent sends about itself.
    /// </summary>
    /// <returns>The tracked device with its active agent, or the 404 (unknown agent) or 409 (replaced agent) to return.</returns>
    public static async Task<(Device? Device, IActionResult? Refusal)> FindDeviceOfActiveAgentAsync(
        this Signal9DbContext db, ControllerBase controller, Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await db.Agents.Include(a => a.Device!).ThenInclude(d => d.ActiveAgent)
            .SingleOrDefaultAsync(a => a.Id == agentId, cancellationToken);
        if (agent is null)
            return (null, controller.NotFound());
        if (agent.Device!.ActiveAgentId != agent.Id)
            return (null, controller.Problem(statusCode: StatusCodes.Status409Conflict, title: "Agent replaced",
                detail: "This agent is no longer its device's active agent."));
        return (agent.Device, null);
    }
}
