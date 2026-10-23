using Microsoft.AspNetCore.SignalR.Client;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.Web.Client.Services;

public enum LiveStatus
{
    Disconnected,
    Connecting,
    Connected,
    Unavailable,
}

/// <summary>
/// Live updates from the API's dashboard hub, through web's proxy. The hub is a nicety: when it can't be reached the
/// dashboard still works, says so, and keeps retrying with backoff.
/// </summary>
public sealed class DashboardHubConnection : IDashboardHubClient, IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly TimeProvider _time;
    private readonly ILogger<DashboardHubConnection> _logger;
    private readonly CancellationTokenSource _disposed = new();
    private bool _starting;
    private bool _lastAttemptFailed;

    /// <param name="handlerFactory">Replaces the HTTP handler SignalR uses; tests route negotiate to a fake.</param>
    public DashboardHubConnection(
        Uri baseAddress, TimeProvider time, ILogger<DashboardHubConnection> logger,
        Func<HttpMessageHandler, HttpMessageHandler>? handlerFactory = null)
    {
        _time = time;
        _logger = logger;
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, HubRoutes.Dashboard.TrimStart('/')), options =>
            {
                // Negotiate is a POST, so web wants the CSRF header on it.
                options.Headers[BffRoutes.CsrfHeader] = BffRoutes.CsrfHeaderValue;
                if (handlerFactory is not null)
                    options.HttpMessageHandlerFactory = handlerFactory;
            })
            .AddJsonProtocol(options => Signal9Json.Configure(options.PayloadSerializerOptions))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<DeviceDto>(nameof(DeviceChanged), DeviceChanged);
        _connection.On<Guid>(nameof(DeviceDeleted), DeviceDeleted);
        _connection.On<TelemetrySampleDto>(nameof(TelemetryReceived), TelemetryReceived);
        _connection.On<CommandDto>(nameof(CommandChanged), CommandChanged);
        _connection.Reconnecting += _ => RaiseStatusChanged();
        _connection.Reconnected += _ => RaiseStatusChanged();
        _connection.Closed += async error =>
        {
            await RaiseStatusChanged();
            _ = StartAsync(); // automatic reconnect gave up; keep trying in the background
        };
    }

    public event Func<DeviceDto, Task>? OnDeviceChanged;
    public event Func<Guid, Task>? OnDeviceDeleted;
    public event Func<TelemetrySampleDto, Task>? OnTelemetryReceived;
    public event Func<CommandDto, Task>? OnCommandChanged;
    public event Func<Task>? OnStatusChanged;

    public LiveStatus Status => _connection.State switch
    {
        HubConnectionState.Connected => LiveStatus.Connected,
        HubConnectionState.Connecting or HubConnectionState.Reconnecting => LiveStatus.Connecting,
        // Between retries the connection is Disconnected; it's unavailable once an attempt has failed.
        _ when _lastAttemptFailed => LiveStatus.Unavailable,
        _ when _starting => LiveStatus.Connecting,
        _ => LiveStatus.Disconnected,
    };

    /// <summary>
    /// Connects, retrying with backoff until it succeeds. Automatic reconnect only covers connections that were
    /// established once, so the first connection needs its own retry loop. Returns once connected or disposed.
    /// </summary>
    public async Task StartAsync()
    {
        if (_starting || _disposed.IsCancellationRequested || _connection.State != HubConnectionState.Disconnected)
            return;
        _starting = true;
        _lastAttemptFailed = false;
        await RaiseStatusChanged();
        try
        {
            for (var attempt = 0; !_disposed.IsCancellationRequested; attempt++)
            {
                try
                {
                    await _connection.StartAsync(_disposed.Token);
                    _lastAttemptFailed = false;
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _lastAttemptFailed = true;
                    var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                    _logger.LogWarning(ex, "Could not connect to the dashboard hub; retrying in {Delay}", delay);
                    await RaiseStatusChanged();
                    await Task.Delay(delay, _time, _disposed.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
        }
        finally
        {
            _starting = false;
            await RaiseStatusChanged();
        }
    }

    public Task DeviceChanged(DeviceDto device) => OnDeviceChanged?.Invoke(device) ?? Task.CompletedTask;
    public Task DeviceDeleted(Guid deviceId) => OnDeviceDeleted?.Invoke(deviceId) ?? Task.CompletedTask;
    public Task TelemetryReceived(TelemetrySampleDto sample) => OnTelemetryReceived?.Invoke(sample) ?? Task.CompletedTask;
    public Task CommandChanged(CommandDto command) => OnCommandChanged?.Invoke(command) ?? Task.CompletedTask;

    private Task RaiseStatusChanged() => OnStatusChanged?.Invoke() ?? Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _disposed.CancelAsync();
        await _connection.DisposeAsync();
        _disposed.Dispose();
    }
}
