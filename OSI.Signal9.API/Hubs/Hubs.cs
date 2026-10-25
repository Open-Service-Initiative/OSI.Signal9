using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Agents;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Hubs;

/// <summary>
/// The agent doorbell (ADR-0009). An agent connects with its certificate-bound <c>agent</c> token and joins its own
/// group, named by the token alone, which the API rings when the agent has work. It carries no command content and has
/// no methods for agents to call. The connection closes when the token expires (story 115).
/// </summary>
[RequireScope(Scopes.Agent)]
public sealed class AgentHub(Signal9DbContext db) : Hub<IAgentHubClient>
{
    public static string GroupFor(Guid agentId) => $"agent:{agentId}";

    public override async Task OnConnectedAsync()
    {
        // Only an enrolled agent, still its device's active agent, hears a doorbell.
        if (Context.User?.AgentId() is not { } agentId
            || !await db.Devices.IgnoreTenantScope(TenantScopeBypass.AgentSelfService)
                .AnyAsync(d => d.ActiveAgentId == agentId, Context.ConnectionAborted))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(agentId));
        await base.OnConnectedAsync();
    }
}

/// <summary>
/// Server-to-client only: the dashboard listens for device, telemetry and command changes. Either scope is enough (#63).
/// </summary>
[RequireScope(Scopes.Devices.Read, Scopes.Telemetry.Read)]
public sealed class DashboardHub : Hub<IDashboardHubClient>;
