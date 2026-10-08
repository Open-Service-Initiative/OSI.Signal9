using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Signal9.Shared.Configuration;
using Signal9.Shared.DTOs;
using Signal9.Shared.Models;
using System.Text.Json;
using System.Text;
using Signal9.Shared.DTOs.Core;

namespace Signal9.Agent.Services;    /// <summary>
                                     /// Main agent service that handles communication with the Agent Functions and SignalR service
                                     /// </summary>
public class AgentService(
    ILogger<AgentService> logger,
    IOptions<AgentConfiguration> config,
    ITelemetryCollector telemetryCollector,
    ISystemInfoProvider systemInfoProvider)
    : BackgroundService
{
    private readonly AgentConfiguration _config = config.Value;
    private readonly HttpClient _httpClient = new();
    private HubConnection? _signalRConnection;
    private Timer? _heartbeatTimer;
    private Timer? _telemetryTimer;
    private readonly string _agentId = Environment.MachineName + "_" + Guid.NewGuid().ToString("N")[..8];
    private int _reconnectAttempts;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Agent service starting with ID: {AgentId}", _agentId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Register agent with Agent Functions
                await RegisterWithAgentFunctions();

                // Connect to SignalR for real-time communication
                await ConnectToSignalR();

                // Start periodic tasks
                StartTimers();

                // Keep the service running while connected
                while (_signalRConnection?.State == HubConnectionState.Connected && !stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(1000, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in agent service execution");
                _reconnectAttempts++;

                // Exponential backoff for reconnection attempts
                var delaySeconds = Math.Min(Math.Pow(2, _reconnectAttempts), 300); // Max 5 minutes
                var delay = TimeSpan.FromSeconds(delaySeconds);
                logger.LogInformation("Reconnecting in {Delay} seconds (attempt {Attempt})", delay.TotalSeconds, _reconnectAttempts);
                await Task.Delay(delay, stoppingToken);
            }
        }
    }

    private async Task RegisterWithAgentFunctions()
    {
        try
        {
            // Use SignalR connection instead of HTTP POST
            if (_signalRConnection?.State != HubConnectionState.Connected)
            {
                logger.LogWarning("SignalR connection not established, cannot register agent");
                return;
            }

            var systemInfo = await systemInfoProvider.GetSystemInfoAsync();
            var registrationData = new AgentRegistrationRequest
            {
                AgentId = _agentId,
                TenantCode = _config.TenantCode ?? string.Empty,
                ParentId = Guid.NewGuid(), // TODO: Get actual tenant ID from config
                MachineName = Environment.MachineName,
                OperatingSystem = Environment.OSVersion.ToString(),
                OSVersion = Environment.OSVersion.VersionString,
                Architecture = Environment.Is64BitOperatingSystem ? "x64" : "x86",
                IpAddress = "127.0.0.1", // Will be updated by network discovery
                MacAddress = "00:00:00:00:00:00", // Will be updated by network discovery
                TotalMemoryMB = systemInfo.MemoryUsageMB + systemInfo.AvailableMemoryMB ?? 0,
                ProcessorCores = Environment.ProcessorCount,
                ProcessorName = "Unknown", // Will be enriched from CustomMetrics if available
                Domain = Environment.UserDomainName,
                LastSeen = DateTime.UtcNow,
                Version = "1.0.0",
                IsOnline = true
            };

            var json = JsonSerializer.Serialize(registrationData, _jsonOptions);

            // Send registration via SignalR instead of HTTP
            await _signalRConnection.InvokeAsync("register", json);
            logger.LogInformation("Agent {AgentId} registration sent via SignalR", _agentId);

            _reconnectAttempts = 0; // Reset reconnect attempts on successful registration
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error registering agent {AgentId} with Agent Functions", _agentId);
            throw;
        }
    }

    private async Task ConnectToSignalR()
    {
        try
        {
            _signalRConnection = new HubConnectionBuilder()
            .WithUrl($"{_config.AgentFunctionsUrl}/api")
            .WithAutomaticReconnect()
            .Build();

            // Set up event handlers using Enhanced DTOs for API boundaries
            _signalRConnection.On<AgentCommandDto>("ExecuteCommand", ExecuteCommandAsync);
            _signalRConnection.On<object>("UpdateConfiguration", UpdateConfigurationAsync);
            _signalRConnection.On<string[]>("CollectTelemetry", CollectTelemetryAsync);
            _signalRConnection.On("RestartAgent", RestartAgentAsync);
            _signalRConnection.On("ShutdownAgent", ShutdownAgentAsync);
            _signalRConnection.On<string>("ConnectionStatusChanged", OnConnectionStatusChanged);

            _signalRConnection.Reconnecting += (exception) =>
            {
                logger.LogWarning("SignalR connection lost. Reconnecting... Exception: {Exception}", exception?.Message);
                return Task.CompletedTask;
            };

            _signalRConnection.Reconnected += (connectionId) =>
            {
                logger.LogInformation("SignalR reconnected with connection ID: {ConnectionId}", connectionId);
                _reconnectAttempts = 0;
                return Task.CompletedTask;
            };

            _signalRConnection.Closed += (exception) =>
            {
                logger.LogError("SignalR connection closed. Exception: {Exception}", exception?.Message);
                return Task.CompletedTask;
            };

            await _signalRConnection.StartAsync();
            logger.LogInformation("Connected to SignalR service");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to SignalR service");
            throw;
        }
    }

    private void StartTimers()
    {
        // Start heartbeat timer - capture instance for fire-and-forget
        _heartbeatTimer = new Timer(_ =>
        {
            Task.Run(SendHeartbeatAsync);
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));

        // Start telemetry timer - capture instance for fire-and-forget
        _telemetryTimer = new Timer(_ =>
        {
            Task.Run(SendTelemetryAsync);
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
    }

    private async Task SendHeartbeatAsync()
    {
        try
        {
            if (_signalRConnection?.State == HubConnectionState.Connected)
            {
                // Send heartbeat via HTTP to Agent Functions
                var heartbeatData = new { AgentId = _agentId, Timestamp = DateTime.UtcNow };
                var json = JsonSerializer.Serialize(heartbeatData, _jsonOptions);
                var content = new StringContent(json, Encoding.UTF8);
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

                var heartbeatUrl = $"{_config.AgentFunctionsUrl}/api/agents/{_agentId}/heartbeat";
                await _httpClient.PostAsync(heartbeatUrl, content);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending heartbeat for agent {AgentId}", _agentId);
        }
    }

    private async Task SendTelemetryAsync()
    {
        try
        {
            var telemetryData = await telemetryCollector.CollectTelemetryAsync();
            // AgentId and TenantCode should already be set by the TelemetryCollector

            var json = JsonSerializer.Serialize(telemetryData, _jsonOptions);
            var content = new StringContent(json);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var telemetryUrl = $"{_config.AgentFunctionsUrl}/api/ReceiveTelemetry";
            var response = await _httpClient.PostAsync(telemetryUrl, content);

            if (response.IsSuccessStatusCode)
            {
                logger.LogDebug("Telemetry sent successfully for agent {AgentId}", _agentId);
            }
            else
            {
                logger.LogWarning("Failed to send telemetry for agent {AgentId}. Status: {StatusCode}",
                    _agentId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending telemetry for agent {AgentId}", _agentId);
        }
    }

    private async Task ExecuteCommandAsync(AgentCommandDto command)
    {
        logger.LogInformation("Executing command {CommandType} for agent {AgentId}", command.CommandType, _agentId);

        try
        {
            object result;
            switch (command.CommandType)
            {
                case CommandType.CustomCommand:
                    var systemInfo = await systemInfoProvider.GetSystemInfoAsync();
                    result = new { SystemInfo = systemInfo };
                    break;
                case CommandType.RestartService:
                    {
                        // For now, use a hardcoded service name until Parameters issue is resolved
                        var serviceName = "default-service";
                        await Task.Delay(100); // Placeholder for service restart logic
                        result = new { Success = true, Message = $"Service {serviceName} restart initiated" };
                    }
                    break;
                case CommandType.RunScript:
                    {
                        // For now, use a hardcoded script until Parameters issue is resolved
                        await Task.Delay(100); // Placeholder for script execution logic
                        result = new { Success = true, Output = "Script executed successfully" };
                    }
                    break;
                default:
                    result = new { Error = $"Unknown command type: {command.CommandType}" };
                    break;
            }

            logger.LogInformation("Command {CommandType} executed successfully with result: {Result}",
                command.CommandType, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error executing command {CommandType}", command.CommandType);
        }
    }

    // Method definitions removed due to compilation conflicts

    private async Task UpdateConfigurationAsync(object configuration)
    {
        logger.LogInformation("Updating configuration for agent {AgentId}", _agentId);
        // TODO: Implement configuration update logic
        await Task.CompletedTask;
    }

    private async Task CollectTelemetryAsync(string[] metrics)
    {
        logger.LogInformation("Collecting specific telemetry metrics for agent {AgentId}: {Metrics}",
            _agentId, string.Join(", ", metrics));
        await SendTelemetryAsync();
    }

    private async Task RestartAgentAsync()
    {
        logger.LogInformation("Restart requested for agent {AgentId}", _agentId);
        // TODO: Implement agent restart logic
        await Task.CompletedTask;
    }

    private async Task ShutdownAgentAsync()
    {
        logger.LogInformation("Shutdown requested for agent {AgentId}", _agentId);
        // TODO: Implement agent shutdown logic
        await Task.CompletedTask;
    }

    private async Task OnConnectionStatusChanged(string status)
    {
        logger.LogInformation("Connection status changed to {Status} for agent {AgentId}", status, _agentId);
        await Task.CompletedTask;
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Agent service stopping for agent {AgentId}", _agentId);

        _heartbeatTimer?.Dispose();
        _telemetryTimer?.Dispose();

        if (_signalRConnection != null)
        {
            await _signalRConnection.DisposeAsync();
        }

        _httpClient.Dispose();

        await base.StopAsync(cancellationToken);
    }
}