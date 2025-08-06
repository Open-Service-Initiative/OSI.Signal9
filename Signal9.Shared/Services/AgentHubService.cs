using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Signal9.Shared.DTOs;
using Signal9.Shared.Models;
using Signal9.Shared.Interfaces;

namespace Signal9.Shared.Services;

/// <summary>
/// SignalR hub for real-time agent communication and notifications.
/// Uses DTOs for consistent data handling across the application.
/// </summary>
public class AgentHub(ILogger<AgentHub> logger, IRelationalDataService relationalDataService) : Hub<IAgentClient>
{

    /// <summary>
    /// Called when an agent connects to the hub
    /// </summary>
    public async Task RegisterAgent(string agentId, Guid tenantId)
    {
        try
        {
            // Verify agent exists and belongs to tenant
            var agent = await relationalDataService.GetAgentAsync(tenantId, agentId);
            if (agent == null)
            {
                logger.LogWarning("Invalid agent registration attempt: AgentId={AgentId}, TenantId={TenantId}", agentId, tenantId);
                await Clients.Caller.ReceiveError("Invalid agent credentials");
                return;
            }

            // Add to tenant group for broadcasting
            await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant-{tenantId}");
            
            // Add to agent-specific group for direct commands
            await Groups.AddToGroupAsync(Context.ConnectionId, $"agent-{agentId}");

            // Update agent status
            agent.LastSeen = DateTime.UtcNow;
            agent.Status = AgentStatus.Online;
            await relationalDataService.UpdateAgentAsync(agent);

            // Notify other users in tenant about agent coming online
            await Clients.Group($"tenant-{tenantId}").ReceiveAgentStatusUpdate(agentId, "Online");

            logger.LogInformation("Agent {AgentId} registered for tenant {TenantId}", agentId, tenantId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error registering agent {AgentId}", agentId);
            await Clients.Caller.ReceiveError("Registration failed");
        }
    }

    /// <summary>
    /// Called when a user (dashboard) connects
    /// </summary>
    public async Task JoinTenant(Guid tenantId)
    {
        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, $"tenant-{tenantId}");
            logger.LogDebug("User joined tenant group {TenantId}", tenantId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error joining tenant {TenantId}", tenantId);
        }
    }

    /// <summary>
    /// Send telemetry data from agent
    /// </summary>
    public async Task SendTelemetry(string agentId, TelemetryData telemetryData)
    {
        try
        {
            if (Guid.TryParse(telemetryData.TenantId, out var tenantId))
            {
                var agent = await relationalDataService.GetAgentAsync(tenantId, agentId);
                if (agent == null)
                {
                    logger.LogWarning("Telemetry from unknown agent {AgentId}", agentId);
                    return;
                }

                // Update last seen
                agent.LastSeen = DateTime.UtcNow;
                await relationalDataService.UpdateAgentAsync(agent);

                // Broadcast to tenant dashboard users
                await Clients.Group($"tenant-{telemetryData.TenantId}").ReceiveTelemetryUpdate(agentId, telemetryData);

                logger.LogDebug("Received telemetry from agent {AgentId}", agentId);
            }
            else
            {
                logger.LogWarning("Invalid TenantId format in telemetry from agent {AgentId}: {TenantId}", agentId, telemetryData.TenantId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing telemetry from agent {AgentId}", agentId);
        }
    }

    public override async Task OnConnectedAsync()
    {
        logger.LogDebug("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        logger.LogDebug("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Implementation of SignalR hub service for sending messages to clients
/// </summary>
public class AgentHubService(IHubContext<AgentHub, IAgentClient> hubContext, ILogger<AgentHubService> logger) : IAgentHubService
{
    public async Task SendCommandToAgentAsync(string agentId, object command)
    {
        try
        {
            await hubContext.Clients.Group($"agent-{agentId}").ReceiveCommand(command);
            logger.LogDebug("Sent command to agent {AgentId}", agentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send command to agent {AgentId}", agentId);
            throw;
        }
    }

    public async Task NotifyAgentStatusChangeAsync(Guid tenantId, string agentId, string status)
    {
        try
        {
            await hubContext.Clients.Group($"tenant-{tenantId}").ReceiveAgentStatusUpdate(agentId, status);
            logger.LogDebug("Notified tenant {TenantId} of agent {AgentId} status change to {Status}", tenantId, agentId, status);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to notify agent status change");
            throw;
        }
    }

    public async Task NotifyTelemetryUpdateAsync(Guid tenantId, string agentId, TelemetryData telemetryData)
    {
        try
        {
            await hubContext.Clients.Group($"tenant-{tenantId}").ReceiveTelemetryUpdate(agentId, telemetryData);
            logger.LogDebug("Notified tenant {TenantId} of telemetry update from agent {AgentId}", tenantId, agentId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to notify telemetry update");
            throw;
        }
    }

    public async Task JoinTenantGroupAsync(string connectionId, Guid tenantId)
    {
        try
        {
            await hubContext.Groups.AddToGroupAsync(connectionId, $"tenant-{tenantId}");
            logger.LogDebug("Added connection {ConnectionId} to tenant group {TenantId}", connectionId, tenantId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to join tenant group");
            throw;
        }
    }

    public async Task LeaveTenantGroupAsync(string connectionId, Guid tenantId)
    {
        try
        {
            await hubContext.Groups.RemoveFromGroupAsync(connectionId, $"tenant-{tenantId}");
            logger.LogDebug("Removed connection {ConnectionId} from tenant group {TenantId}", connectionId, tenantId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to leave tenant group");
            throw;
        }
    }
}

/// <summary>
/// Interface defining what the SignalR hub can send to clients
/// </summary>
public interface IAgentClient
{
    Task ReceiveCommand(object command);
    Task ReceiveAgentStatusUpdate(string agentId, string status);
    Task ReceiveTelemetryUpdate(string agentId, TelemetryData telemetryData);
    Task ReceiveError(string message);
}
