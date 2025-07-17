using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Signal9.Shared.DTOs;
using Signal9.Shared.Models;
using System.Net;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;

namespace Signal9.Agent.Functions;

/// <summary>
/// Functions for handling agent communications through Azure SignalR Service
/// </summary>
public class AgentCommunicationFunctions
{
    private readonly ILogger<AgentCommunicationFunctions> _logger;

    public AgentCommunicationFunctions(ILogger<AgentCommunicationFunctions> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// SignalR negotiate function for agent connections
    /// </summary>
    [Function("negotiate")]
    public SignalRConnectionInfo Negotiate(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req,
        [SignalRConnectionInfoInput(HubName = "AgentHub")] SignalRConnectionInfo connectionInfo)
    {
        _logger.LogInformation("Agent requesting SignalR connection negotiation");
        return connectionInfo;
    }

    /// <summary>
    /// Handle agent registration through HTTP
    /// </summary>
    [Function("RegisterAgent")]
    public async Task<HttpResponseData> RegisterAgentAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        _logger.LogInformation("Processing agent registration");

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var registrationData = JsonSerializer.Deserialize<AgentRegistrationRequest>(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (registrationData == null)
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid registration data");
                return badRequestResponse;
            }

            // Process agent registration
            // TODO: Validate tenant code
            // TODO: Create agent record in database
            // TODO: Generate authentication token

            _logger.LogInformation("Registered agent {AgentId} for tenant {TenantCode}", 
                registrationData.AgentId, registrationData.TenantCode);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(new 
            { 
                Success = true, 
                Message = "Agent registered successfully",
                registrationData.AgentId
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing agent registration");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Registration failed");
            return errorResponse;
        }
    }

    /// <summary>
    /// Handle telemetry data from agents
    /// </summary>
    [Function("ReceiveTelemetry")]
    public async Task<HttpResponseData> ReceiveTelemetryAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
        _logger.LogInformation("Processing telemetry data");

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var telemetryData = JsonSerializer.Deserialize<TelemetryDataDto>(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (telemetryData == null)
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid telemetry data");
                return badRequestResponse;
            }

            // Process telemetry data
            // TODO: Store telemetry in database
            // TODO: Trigger alerts if thresholds exceeded

            _logger.LogInformation("Received telemetry from agent {AgentId}", telemetryData.AgentId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(new 
            { 
                Success = true, 
                Message = "Telemetry received" 
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing telemetry data");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Telemetry processing failed");
            return errorResponse;
        }
    }

    /// <summary>
    /// Send command to specific agent
    /// </summary>
    [Function("SendAgentCommand")]
    public async Task<HttpResponseData> SendAgentCommandAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "agents/{agentId}/commands")] HttpRequestData req,
        string agentId)
    {
        _logger.LogInformation("Sending command to agent {AgentId}", agentId);

        try
        {
            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var command = JsonSerializer.Deserialize<AgentCommand>(requestBody, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            if (command == null)
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("Invalid command data");
                return badRequestResponse;
            }

            // TODO: Send command to specific agent via SignalR
            // This would require a separate SignalR sending mechanism

            _logger.LogInformation("Command {CommandType} queued for agent {AgentId}", command.CommandType, agentId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(new 
            { 
                Success = true, 
                Message = "Command queued successfully" 
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending command to agent {AgentId}", agentId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Command sending failed");
            return errorResponse;
        }
    }

    /// <summary>
    /// Agent heartbeat endpoint
    /// </summary>
    [Function("AgentHeartbeat")]
    public async Task<HttpResponseData> AgentHeartbeatAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "agents/{agentId}/heartbeat")] HttpRequestData req,
        string agentId)
    {
        _logger.LogDebug("Received heartbeat from agent {AgentId}", agentId);

        try
        {
            // TODO: Update agent last seen timestamp in database
            // TODO: Update agent status

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteStringAsync(JsonSerializer.Serialize(new 
            { 
                Success = true, 
                Message = "Heartbeat received",
                Timestamp = DateTime.UtcNow
            }));

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing heartbeat for agent {AgentId}", agentId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Heartbeat processing failed");
            return errorResponse;
        }
    }
}
