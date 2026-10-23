using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.Contracts.Hubs;

public static class HubRoutes
{
    /// <summary>Agents connect here with ?agentId={id} to hear the doorbell.</summary>
    public const string Agents = "/hubs/agents";

    /// <summary>The web dashboard connects here to receive live updates.</summary>
    public const string Dashboard = "/hubs/dashboard";
}

/// <summary>
/// The doorbell the API rings on connected agents (ADR-0009). It carries no content: the agent fetches its pending work
/// over REST when it rings, on every (re)connect, and when a heartbeat response says work is waiting.
/// </summary>
public interface IAgentHubClient
{
    /// <summary>The agent has work waiting.</summary>
    Task WorkAvailable();
}

/// <summary>Methods the API invokes on connected dashboards.</summary>
public interface IDashboardHubClient
{
    Task DeviceChanged(DeviceDto device);
    Task DeviceDeleted(Guid deviceId);
    Task TelemetryReceived(TelemetrySampleDto sample);
    Task CommandChanged(CommandDto command);
}
