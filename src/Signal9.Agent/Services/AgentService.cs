using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Signal9.Shared.Configuration;
using Signal9.Shared.DTOs;
using Signal9.Shared.Models;
using System.Text.Json;
using System.Text;
using System.Threading.Channels;

namespace Signal9.Agent.Services;

/// <summary>
/// Main agent service that handles communication with the Agent Functions and SignalR service
/// Enhanced with .NET 9 performance optimizations
/// </summary>
public class AgentService : BackgroundService
{
    private readonly ILogger<AgentService> _logger;
    private readonly AgentConfiguration _config;
    private readonly ITelemetryCollector _telemetryCollector;
    private readonly ISystemInfoProvider _systemInfoProvider;
    private readonly HttpClient _httpClient;
    private HubConnection? _signalRConnection;
    private Timer? _heartbeatTimer;
    private Timer? _telemetryTimer;
    private string _agentId;
    private int _reconnectAttempts = 0;
    private readonly JsonSerializerOptions _jsonOptions;
    
    // .NET 9 Channel-based command processing for better performance
    private readonly Channel<AgentCommand> _commandChannel;
    private readonly ChannelWriter<AgentCommand> _commandWriter;
    private readonly ChannelReader<AgentCommand> _commandReader;

    public AgentService(
        ILogger<AgentService> logger,
        IOptions<AgentConfiguration> config,
        ITelemetryCollector telemetryCollector,
        ISystemInfoProvider systemInfoProvider)
    {
        _logger = logger;
        _config = config.Value;
        _telemetryCollector = telemetryCollector;
        _systemInfoProvider = systemInfoProvider;
        _agentId = Environment.MachineName + "_" + Guid.NewGuid().ToString("N")[..8];
        _httpClient = new HttpClient();
        _jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        
        // Initialize high-performance command channel
        var channelOptions = new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _commandChannel = Channel.CreateBounded<AgentCommand>(channelOptions);
        _commandWriter = _commandChannel.Writer;
        _commandReader = _commandChannel.Reader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Agent service starting with ID: {AgentId}", _agentId);

        // Start command processing task
        var commandProcessingTask = ProcessCommandsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Register agent with Agent Functions
                await RegisterWithAgentFunctions().ConfigureAwait(false);
                
                // Connect to SignalR hub
                await ConnectToSignalRHub().ConfigureAwait(false);
                
                // Start heartbeat and telemetry timers
                StartTimers();
                
                // Wait for connection to close or cancellation
                await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in agent service execution");
                _reconnectAttempts++;
                
                var delaySeconds = Math.Min(Math.Pow(2, _reconnectAttempts), 300); // Max 5 minutes
                var delay = TimeSpan.FromSeconds(delaySeconds);
                _logger.LogInformation("Reconnecting in {Delay} seconds (attempt {Attempt})", delay.TotalSeconds, _reconnectAttempts);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
        }

        await commandProcessingTask.ConfigureAwait(false);
    }

    private async Task ProcessCommandsAsync(CancellationToken cancellationToken)
    {
        await foreach (var command in _commandReader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await ProcessCommandAsync(command).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing command {CommandType}", command.Type);
            }
        }
    }

    private async Task ProcessCommandAsync(AgentCommand command)
    {
        _logger.LogInformation("Processing command: {CommandType}", command.Type);
        
        switch (command.Type)
        {
            case "GetSystemInfo":
                var systemInfo = await _systemInfoProvider.GetSystemInfoAsync().ConfigureAwait(false);
                await SendCommandResponse(command.Id, systemInfo).ConfigureAwait(false);
                break;
                
            case "GetPerformanceMetrics":
                var metrics = await _systemInfoProvider.GetPerformanceMetricsAsync().ConfigureAwait(false);
                await SendCommandResponse(command.Id, metrics).ConfigureAwait(false);
                break;
                
            case "RestartAgent":
                _logger.LogInformation("Restart command received");
                Environment.Exit(0);
                break;
                
            default:
                _logger.LogWarning("Unknown command type: {CommandType}", command.Type);
                break;
        }
    }

    private async Task SendCommandResponse(string commandId, object response)
    {
        try
        {
            if (_signalRConnection?.State == HubConnectionState.Connected)
            {
                await _signalRConnection.SendAsync("CommandResponse", commandId, response).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending command response");
        }
    }

    private async Task RegisterWithAgentFunctions()
    {
        try
        {
            var systemInfo = await _systemInfoProvider.GetSystemInfoAsync().ConfigureAwait(false);
            var registrationData = new AgentRegistrationDto
            {
                AgentId = _agentId,
                TenantCode = _config.TenantCode ?? string.Empty,
                MachineName = Environment.MachineName,
                OperatingSystem = systemInfo.OperatingSystem,
                Architecture = systemInfo.Architecture ?? string.Empty,
                LastSeen = DateTime.UtcNow,
                Version = "1.0.0",
                IsOnline = true
            };

            var json = JsonSerializer.Serialize(registrationData, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var registrationUrl = $"{_config.AgentFunctionsUrl}/api/RegisterAgent";
            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("x-functions-key", _config.FunctionKey);

            var response = await _httpClient.PostAsync(registrationUrl, content).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Agent {AgentId} registered successfully with Agent Functions", _agentId);
                _reconnectAttempts = 0; // Reset reconnect attempts on successful registration
            }
            else
            {
                _logger.LogError("Failed to register agent {AgentId} with Agent Functions. Status: {StatusCode}", 
                    _agentId, response.StatusCode);
                throw new InvalidOperationException($"Registration failed with status {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering agent {AgentId} with Agent Functions", _agentId);
            throw;
        }
    }

    private async Task ConnectToSignalRHub()
    {
        try
        {
            _signalRConnection = new HubConnectionBuilder()
                .WithUrl($"{_config.AgentFunctionsUrl}/api")
                .WithAutomaticReconnect()
                .Build();

            // Set up event handlers for commands
            _signalRConnection.On<AgentCommand>("ExecuteCommand", async (command) =>
            {
                if (_commandWriter.TryWrite(command))
                {
                    _logger.LogInformation("Command {CommandType} queued for execution", command.Type);
                }
                else
                {
                    _logger.LogWarning("Command queue full, dropping command {CommandType}", command.Type);
                }
            });

            _signalRConnection.Reconnecting += (exception) =>
            {
                _logger.LogWarning("SignalR connection lost. Reconnecting... Exception: {Exception}", exception?.Message);
                return Task.CompletedTask;
            };

            _signalRConnection.Reconnected += (connectionId) =>
            {
                _logger.LogInformation("SignalR reconnected with connection ID: {ConnectionId}", connectionId);
                _reconnectAttempts = 0;
                return Task.CompletedTask;
            };

            _signalRConnection.Closed += (exception) =>
            {
                _logger.LogError("SignalR connection closed. Exception: {Exception}", exception?.Message);
                return Task.CompletedTask;
            };

            await _signalRConnection.StartAsync().ConfigureAwait(false);
            _logger.LogInformation("Connected to SignalR service");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to SignalR service");
            throw;
        }
    }

    private void StartTimers()
    {
        // Start heartbeat timer
        _heartbeatTimer = new Timer(async _ => await SendHeartbeatAsync().ConfigureAwait(false), 
            null, TimeSpan.Zero, TimeSpan.FromSeconds(30));

        // Start telemetry timer
        _telemetryTimer = new Timer(async _ => await SendTelemetryAsync().ConfigureAwait(false), 
            null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1));
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
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var heartbeatUrl = $"{_config.AgentFunctionsUrl}/api/agents/{_agentId}/heartbeat";
                await _httpClient.PostAsync(heartbeatUrl, content).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending heartbeat for agent {AgentId}", _agentId);
        }
    }

    private async Task SendTelemetryAsync()
    {
        try
        {
            var telemetryData = await _telemetryCollector.CollectTelemetryAsync().ConfigureAwait(false);
            telemetryData.AgentId = _agentId;
            telemetryData.TenantCode = _config.TenantCode ?? string.Empty;

            var json = JsonSerializer.Serialize(telemetryData, _jsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var telemetryUrl = $"{_config.AgentFunctionsUrl}/api/ReceiveTelemetry";
            var response = await _httpClient.PostAsync(telemetryUrl, content).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Telemetry sent successfully for agent {AgentId}", _agentId);
            }
            else
            {
                _logger.LogWarning("Failed to send telemetry for agent {AgentId}. Status: {StatusCode}", 
                    _agentId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending telemetry for agent {AgentId}", _agentId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Agent service stopping for agent {AgentId}", _agentId);

        _heartbeatTimer?.Dispose();
        _telemetryTimer?.Dispose();

        if (_signalRConnection != null)
        {
            await _signalRConnection.DisposeAsync();
        }

        _httpClient?.Dispose();
        _commandWriter.Complete();

        await base.StopAsync(cancellationToken);
    }
}
