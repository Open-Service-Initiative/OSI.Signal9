using Microsoft.AspNetCore.SignalR;
using OSI.Signal9.Contracts.Hubs;

namespace OSI.Signal9.API.Hubs;

/// <summary>
/// Agents connect with ?agentId={id} and are placed in a per-agent group so commands can be pushed to them.
/// </summary>
// TODO(auth): authenticate the agent and take its id from the token instead of the query string.
public sealed class AgentHub : Hub<IAgentHubClient>
{
    public static string GroupFor(Guid agentId) => $"agent:{agentId}";

    public override async Task OnConnectedAsync()
    {
        var agentId = Context.GetHttpContext()?.Request.Query["agentId"].ToString();
        if (!Guid.TryParse(agentId, out var id))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(id));
        await base.OnConnectedAsync();
    }
}

/// <summary>
/// Server-to-client only: the dashboard listens for agent, telemetry and command changes.
/// </summary>
public sealed class DashboardHub : Hub<IDashboardHubClient>;
