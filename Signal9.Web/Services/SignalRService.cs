using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System;
using System.Threading.Tasks;

namespace Signal9.Web.Services;

/// <summary>
/// SignalR service for WebAssembly dashboard real-time updates
/// </summary>
public class SignalRService : IAsyncDisposable
{
    private readonly ILogger<SignalRService> _logger;
    private readonly IJSRuntime _jsRuntime;
    private HubConnection? _hubConnection;
    private bool _isDisposed;

    public SignalRService(ILogger<SignalRService> logger, IJSRuntime jsRuntime)
    {
        _logger = logger;
        _jsRuntime = jsRuntime;
    }

    /// <summary>
    /// Connection state of the SignalR hub
    /// </summary>
    public HubConnectionState ConnectionState => _hubConnection?.State ?? HubConnectionState.Disconnected;

    /// <summary>
    /// Initialize SignalR connection to DashboardHub
    /// </summary>
    public async Task InitializeAsync()
    {
        try
        {
            _hubConnection = new HubConnectionBuilder()
                .WithUrl("/api/dashboardhub", options =>
                {
                    // For Static Web Apps, the API will be proxied
                    // The negotiate endpoint is handled automatically by SignalR
                    options.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets | 
                                        Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling;
                })
                .WithAutomaticReconnect()
                .Build();

            // Set up event handlers for dashboard-specific events
            _hubConnection.On<string, string>("ReceiveAgentStatusUpdate", OnAgentStatusUpdate);
            _hubConnection.On<string, object>("ReceiveTelemetryUpdate", OnTelemetryUpdate);
            _hubConnection.On<string>("ReceiveError", OnError);

            // Connection state change handlers
            _hubConnection.Reconnecting += OnReconnecting;
            _hubConnection.Reconnected += OnReconnected;
            _hubConnection.Closed += OnClosed;

            await _hubConnection.StartAsync();
            _logger.LogInformation("SignalR connection established");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SignalR connection");
            throw;
        }
    }

    /// <summary>
    /// Join a tenant group for receiving tenant-specific updates
    /// </summary>
    public async Task JoinTenantAsync(Guid tenantId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _hubConnection.InvokeAsync("JoinTenant", tenantId);
                _logger.LogDebug("Joined tenant group {TenantId}", tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to join tenant group {TenantId}", tenantId);
            }
        }
    }

    /// <summary>
    /// Leave current tenant group
    /// </summary>
    public async Task LeaveTenantAsync(Guid tenantId)
    {
        if (_hubConnection?.State == HubConnectionState.Connected)
        {
            try
            {
                await _hubConnection.InvokeAsync("LeaveTenant", tenantId);
                _logger.LogDebug("Left tenant group {TenantId}", tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to leave tenant group {TenantId}", tenantId);
            }
        }
    }

    /// <summary>
    /// Stop the SignalR connection
    /// </summary>
    public async Task StopAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.StopAsync();
            _logger.LogInformation("SignalR connection stopped");
        }
    }

    // Event handlers
    private async Task OnAgentStatusUpdate(string agentId, string status)
    {
        try
        {
            _logger.LogDebug("Agent {AgentId} status changed to {Status}", agentId, status);
            
            // Update UI via JavaScript interop
            await _jsRuntime.InvokeVoidAsync("dashboard.updateAgentStatus", agentId, status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling agent status update");
        }
    }

    private async Task OnTelemetryUpdate(string agentId, object telemetryData)
    {
        try
        {
            _logger.LogDebug("Telemetry update received for agent {AgentId}", agentId);
            
            // Update UI via JavaScript interop
            await _jsRuntime.InvokeVoidAsync("dashboard.updateTelemetry", agentId, telemetryData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling telemetry update");
        }
    }

    private async Task OnError(string message)
    {
        try
        {
            _logger.LogError("SignalR error: {Message}", message);
            
            // Show error notification
            await _jsRuntime.InvokeVoidAsync("dashboard.showError", message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling SignalR error message");
        }
    }

    private Task OnReconnecting(Exception? exception)
    {
        _logger.LogWarning("SignalR reconnecting: {Exception}", exception?.Message);
        return Task.CompletedTask;
    }

    private Task OnReconnected(string? connectionId)
    {
        _logger.LogInformation("SignalR reconnected with connection ID: {ConnectionId}", connectionId);
        return Task.CompletedTask;
    }

    private Task OnClosed(Exception? exception)
    {
        _logger.LogWarning("SignalR connection closed: {Exception}", exception?.Message);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            if (_hubConnection != null)
            {
                await _hubConnection.DisposeAsync();
            }
            _isDisposed = true;
        }
    }
}
