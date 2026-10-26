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
/// no methods for agents to call. The connection closes when the token expires (story 115), and when the agent is
/// revoked or rotates its certificate (#129).
/// </summary>
[RequireScope(Scopes.Agent)]
[AgentEndpoint]
public sealed class AgentHub(Signal9DbContext db, HubConnections<AgentHub> connections) : Hub<IAgentHubClient>
{
    public static string GroupFor(Guid agentId) => $"agent:{agentId}";

    public override async Task OnConnectedAsync()
    {
        if (Context.User?.AgentId() is not { } agentId)
        {
            Context.Abort();
            return;
        }

        // Only an enrolled agent, still its device's active agent, awaiting identification or a held fresh-OS claim
        // awaiting approval, hears a doorbell. The
        // connection is recorded before that's checked, so a revocation committed meanwhile either fails the check or
        // finds the connection to close.
        var connection = connections.Add(Context);
        if (!await db.Agents.IgnoreTenantScope(TenantScopeBypass.AgentSelfService)
                .AnyAsync(a => a.Id == agentId && a.RevokedAt == null
                    && (a.DeviceId == null || a.Device!.ActiveAgentId == agentId || a.AwaitingApproval),
                    Context.ConnectionAborted))
        {
            connections.Close(connection);
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(agentId));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Closes the agent hub connections this api replica holds whose access token is no longer good: revoking an agent,
/// rotating its key or deleting its tenant revokes its tokens (#129), wherever its connection is.
/// </summary>
public sealed class AgentHubAccess(Signal9DbContext db, HubConnections<AgentHub> connections, TimeProvider time)
{
    public async Task RecheckAllAsync(CancellationToken cancellationToken)
    {
        foreach (var connection in connections.Where(_ => true))
        {
            if (connection.Context.User is { } user && await HubTokens.IsLiveAsync(db, user, time, cancellationToken))
                continue;
            connections.Close(connection);
        }
    }
}

/// <summary>The agent hub connections of one agent.</summary>
public static class AgentConnections
{
    /// <summary>
    /// Closes every agent hub connection of <paramref name="agentId"/> at once, as it loses its credential (story 101).
    /// </summary>
    public static void Close(this HubConnections<AgentHub> connections, Guid agentId)
    {
        foreach (var connection in connections.Where(c => c.Context.User?.AgentId() == agentId))
            connections.Close(connection);
    }
}
