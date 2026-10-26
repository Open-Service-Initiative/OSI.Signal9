using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
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
public sealed class AgentHub(Signal9DbContext db, AgentHubConnections connections) : Hub<IAgentHubClient>
{
    public static string GroupFor(Guid agentId) => $"agent:{agentId}";

    public override async Task OnConnectedAsync()
    {
        if (Context.User?.AgentId() is not { } agentId)
        {
            Context.Abort();
            return;
        }

        // Only an enrolled agent, still its device's active agent or awaiting identification, hears a doorbell. The
        // connection is recorded before that's checked, so a revocation committed meanwhile either fails the check or
        // finds the connection to close.
        connections.Add(agentId, Context);
        if (!await db.Agents.IgnoreTenantScope(TenantScopeBypass.AgentSelfService)
                .AnyAsync(a => a.Id == agentId && a.RevokedAt == null
                    && (a.DeviceId == null || a.Device!.ActiveAgentId == agentId), Context.ConnectionAborted))
        {
            connections.Remove(agentId, Context);
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(agentId));
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.User?.AgentId() is { } agentId)
            connections.Remove(agentId, Context);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// The agent hub connections this api instance holds, by agent, so they can be closed the moment the agent loses its
/// credential (story 101). SignalR has no way to close a connection from outside its hub, so the hub records each one.
/// </summary>
public sealed class AgentHubConnections
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, HubCallerContext>> _connections = new();

    public void Add(Guid agentId, HubCallerContext connection) =>
        _connections.GetOrAdd(agentId, _ => new())[connection.ConnectionId] = connection;

    public void Remove(Guid agentId, HubCallerContext connection)
    {
        if (_connections.TryGetValue(agentId, out var open))
            open.TryRemove(connection.ConnectionId, out _);
    }

    /// <summary>Every open connection, with its agent.</summary>
    internal IReadOnlyList<(Guid AgentId, HubCallerContext Context)> All() =>
        [.. _connections.SelectMany(open => open.Value.Values.Select(context => (open.Key, context)))];

    /// <summary>Closes every hub connection of <paramref name="agentId"/>.</summary>
    public void Close(Guid agentId)
    {
        if (!_connections.TryRemove(agentId, out var open))
            return;
        foreach (var connection in open.Values)
            connection.Abort();
    }
}

/// <summary>How often api re-checks every open agent hub connection against its access token.</summary>
public sealed class AgentHubOptions
{
    public const string SectionName = "AgentHub";

    /// <summary>
    /// The longest a connection outlives its agent's revocation, key rotation or tenant deletion on another api
    /// replica, or on Azure SignalR Service. The replica that handles the change closes its own connections at once.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan AccessCheckInterval { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Closes the agent hub connections this api replica holds whose access token is no longer good: revoking an agent,
/// rotating its key or deleting its tenant revokes its tokens (#129), wherever its connection is.
/// </summary>
public sealed class AgentHubAccess(Signal9DbContext db, AgentHubConnections connections, TimeProvider time)
{
    public async Task RecheckAllAsync(CancellationToken cancellationToken)
    {
        foreach (var (agentId, context) in connections.All())
        {
            if (context.User is { } user && await HubTokens.IsLiveAsync(db, user, time, cancellationToken))
                continue;
            connections.Remove(agentId, context);
            context.Abort();
        }
    }
}

/// <summary>Re-checks every open agent hub connection on an interval (<see cref="AgentHubOptions"/>).</summary>
public sealed class AgentHubAccessSweeper(
    IServiceScopeFactory scopes,
    IOptions<AgentHubOptions> options,
    TimeProvider time,
    ILogger<AgentHubAccessSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.AccessCheckInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<AgentHubAccess>().RecheckAllAsync(stoppingToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogWarning(exception, "Re-checking agent hub connections failed; trying again next time.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
