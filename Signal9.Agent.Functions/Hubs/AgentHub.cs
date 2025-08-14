using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Signal9.Shared.DTOs;
using Signal9.Shared.Services;
using System.Text.Json;

namespace Signal9.Agent.Functions.Hubs;

/// <summary>
/// SignalR Hub for real-time agent communication in Signal9 RMM platform
/// Handles agent registration, telemetry, commands, and heartbeats
/// </summary>
public class AgentHub
{
    private readonly ILogger<AgentHub> _logger;
    private readonly IAgentService _agentService;

    public AgentHub(ILogger<AgentHub> logger, IAgentService agentService)
    {
        _logger = logger;
        _agentService = agentService;
    }

    /// <summary>
    /// Handle agent registration via SignalR
    /// </summary>
    [Function("OnAgentRegister")]
    public async Task<SignalRMessageAction> OnAgentRegister(
        [SignalRTrigger("AgentHub", "messages", "register")] SignalRInvocationContext invocationContext,
        string registrationData)
    {
        var connectionId = invocationContext.ConnectionId;
        _logger.LogInformation("Agent registration received via SignalR from connection {ConnectionId}", connectionId);

        try
        {
            var request = JsonSerializer.Deserialize<AgentRegistrationRequest>(registrationData);
            if (request == null)
            {
                _logger.LogWarning("Invalid registration data received from connection {ConnectionId}", connectionId);
                return new SignalRMessageAction("registrationResponse")
                {
                    Arguments = new object[] { new { Success = false, Message = "Invalid registration data" } }
                };
            }

            // Register agent through service layer
            var agentDto = request.ToAgentDto();
            var registeredAgent = await _agentService.RegisterAgentAsync(agentDto);

            // Generate agent configuration
            var configuration = await _agentService.GenerateAgentConfigurationAsync(registeredAgent.Id);

            // Add agent to SignalR group for tenant isolation
            var parentGroup = $"parent-{request.ParentId}";
            var agentGroup = $"agent-{registeredAgent.Id}";

            _logger.LogInformation("Agent {AgentId} registered successfully for parent {ParentId}", 
                registeredAgent.Id, request.ParentId);

            return new SignalRMessageAction("registrationResponse")
            {
                Arguments = new object[] { new { 
                    Success = true, 
                    AgentId = registeredAgent.Id,
                    Configuration = configuration,
                    ParentGroup = parentGroup,
                    AgentGroup = agentGroup
                } }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during agent registration for connection {ConnectionId}", connectionId);
            return new SignalRMessageAction("registrationResponse")
            {
                Arguments = new object[] { new { Success = false, Message = "Registration failed" } }
            };
        }
    }

    /// <summary>
    /// Handle agent telemetry data via SignalR
    /// </summary>
    [Function("OnTelemetryData")]
    public Task<SignalRMessageAction?> OnTelemetryData(
        [SignalRTrigger("AgentHub", "messages", "telemetry")] SignalRInvocationContext invocationContext,
        string telemetryData)
    {
        try
        {
            var telemetry = JsonSerializer.Deserialize<TelemetryDataDto>(telemetryData);
            if (telemetry == null) return Task.FromResult<SignalRMessageAction?>(null);

            // Process telemetry through service layer
            // TODO: Implement telemetry processing
            
            return Task.FromResult<SignalRMessageAction?>(null); // No response needed for telemetry
        }
        catch (Exception)
        {
            return Task.FromResult<SignalRMessageAction?>(null);
        }
    }

    /// <summary>
    /// Handle agent heartbeat via SignalR
    /// </summary>
    [Function("OnAgentHeartbeat")]
    public Task<SignalRMessageAction?> OnAgentHeartbeat(
        [SignalRTrigger("AgentHub", "messages", "heartbeat")] SignalRInvocationContext invocationContext,
        string heartbeatData)
    {
        try
        {
            var heartbeat = JsonSerializer.Deserialize<AgentHeartbeatRequest>(heartbeatData);
            if (heartbeat == null) return Task.FromResult<SignalRMessageAction?>(null);

            // Update agent last seen timestamp
            // TODO: Implement heartbeat processing
            
            return Task.FromResult<SignalRMessageAction?>(null); // No response needed for heartbeat
        }
        catch (Exception)
        {
            return Task.FromResult<SignalRMessageAction?>(null);
        }
    }

    /// <summary>
    /// Send command to specific agent via SignalR
    /// </summary>
    [Function("SendCommandToAgent")]
    public static SignalRMessageAction SendCommandToAgent(
        [SignalRTrigger("AgentHub", "messages", "sendCommand")] SignalRInvocationContext invocationContext,
        string agentId,
        AgentCommandDto command)
    {
        return new SignalRMessageAction("executeCommand")
        {
            GroupName = $"agent-{agentId}",
            Arguments = new object[] { command }
        };
    }

    /// <summary>
    /// Handle command execution result from agent
    /// </summary>
    [Function("OnCommandResult")]
    public Task<SignalRMessageAction?> OnCommandResult(
        [SignalRTrigger("AgentHub", "messages", "commandResult")] SignalRInvocationContext invocationContext,
        string resultData)
    {
        try
        {
            // TODO: Process command execution result
            return Task.FromResult<SignalRMessageAction?>(null);
        }
        catch (Exception)
        {
            return Task.FromResult<SignalRMessageAction?>(null);
        }
    }
}
