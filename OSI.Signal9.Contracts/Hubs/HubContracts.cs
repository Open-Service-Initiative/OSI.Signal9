namespace OSI.Signal9.Contracts.Hubs;

public static class HubRoutes
{
    /// <summary>Agents connect here with ?agentId={id} to hear the doorbell.</summary>
    public const string Agents = "/hubs/agents";

    /// <summary>The dashboard connects here, through web, to hear what changes in the tenants it can see.</summary>
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

/// <summary>
/// What the API tells connected dashboards: small notices that something changed, never its details (ADR-0009). The
/// dashboard fetches what it needs from the notice's canonical URL, through web, where every read is authorized.
/// </summary>
public interface IDashboardHubClient
{
    Task DeviceChanged(DeviceNotice notice);
    Task DeviceDeleted(DeviceNotice notice);
    Task TelemetryReceived(TelemetryNotice notice);
    Task CommandChanged(CommandNotice notice);
}

/// <summary>A device changed or was deleted.</summary>
/// <param name="Url">The device's canonical URL.</param>
public sealed record DeviceNotice(Guid DeviceId, string Url);

/// <summary>A command was queued or changed.</summary>
/// <param name="Url">The command's canonical URL.</param>
public sealed record CommandNotice(Guid DeviceId, Guid CommandId, string Url);

/// <summary>A device sent a telemetry sample.</summary>
/// <param name="Url">The sample's canonical URL.</param>
public sealed record TelemetryNotice(Guid DeviceId, Guid SampleId, string Url);
