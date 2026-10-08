using Microsoft.AspNetCore.SignalR.Client;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.Web.Services;

/// <summary>
/// Live updates from the API's dashboard hub. Pages subscribe to the events they care about.
/// </summary>
public sealed class DashboardHubConnection : IDashboardHubClient, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ILogger<DashboardHubConnection> _logger;
    private bool _starting;
    private bool _lastAttemptFailed;

    public DashboardHubConnection(Uri apiBaseAddress, ILogger<DashboardHubConnection> logger)
    {
        _logger = logger;
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(apiBaseAddress, HubRoutes.Dashboard.TrimStart('/')))
            .AddJsonProtocol(options => Signal9Json.Configure(options.PayloadSerializerOptions))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<AgentDto>(nameof(AgentChanged), AgentChanged);
        _connection.On<Guid>(nameof(AgentDeleted), AgentDeleted);
        _connection.On<TelemetrySampleDto>(nameof(TelemetryReceived), TelemetryReceived);
        _connection.On<AgentCommandDto>(nameof(CommandChanged), CommandChanged);
        _connection.Reconnecting += _ => RaiseStateChanged();
        _connection.Reconnected += _ => RaiseStateChanged();
        _connection.Closed += async error =>
        {
            await RaiseStateChanged();
            _ = StartAsync(); // automatic reconnect gave up; keep trying in the background
        };
    }

    public event Func<AgentDto, Task>? OnAgentChanged;
    public event Func<Guid, Task>? OnAgentDeleted;
    public event Func<TelemetrySampleDto, Task>? OnTelemetryReceived;
    public event Func<AgentCommandDto, Task>? OnCommandChanged;
    public event Func<Task>? OnStateChanged;

    public HubConnectionState State => _connection.State switch
    {
        // Between retries the connection is Disconnected; report Connecting until an attempt has failed.
        HubConnectionState.Disconnected when _starting && !_lastAttemptFailed => HubConnectionState.Connecting,
        var state => state,
    };

    /// <summary>
    /// Connects to the hub, retrying with backoff until it succeeds. Automatic reconnect only covers
    /// connections that were established once, so the initial connection needs its own retry loop.
    /// </summary>
    public async Task StartAsync()
    {
        if (_starting || _connection.State != HubConnectionState.Disconnected)
            return;
        _starting = true;
        _lastAttemptFailed = false;
        await RaiseStateChanged();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await _connection.StartAsync();
                    return;
                }
                catch (Exception ex)
                {
                    _lastAttemptFailed = true;
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                    _logger.LogWarning(ex, "Could not connect to the dashboard hub; retrying in {Delay}", delay);
                    await RaiseStateChanged();
                    await Task.Delay(delay);
                }
            }
        }
        finally
        {
            _starting = false;
            await RaiseStateChanged();
        }
    }

    public Task AgentChanged(AgentDto agent) => OnAgentChanged?.Invoke(agent) ?? Task.CompletedTask;
    public Task AgentDeleted(Guid agentId) => OnAgentDeleted?.Invoke(agentId) ?? Task.CompletedTask;
    public Task TelemetryReceived(TelemetrySampleDto sample) => OnTelemetryReceived?.Invoke(sample) ?? Task.CompletedTask;
    public Task CommandChanged(AgentCommandDto command) => OnCommandChanged?.Invoke(command) ?? Task.CompletedTask;

    private Task RaiseStateChanged() => OnStateChanged?.Invoke() ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
