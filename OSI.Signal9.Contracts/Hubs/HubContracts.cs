using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.Contracts.Hubs;

public static class HubRoutes
{
    /// <summary>Agents connect here with ?agentId={id} to receive commands.</summary>
    public const string Agents = "/hubs/agents";

    /// <summary>The web dashboard connects here to receive live updates.</summary>
    public const string Dashboard = "/hubs/dashboard";
}

/// <summary>Methods the API invokes on connected agents.</summary>
public interface IAgentHubClient
{
    Task CommandQueued(AgentCommandDto command);
}

/// <summary>Methods the API invokes on connected dashboards.</summary>
public interface IDashboardHubClient
{
    Task AgentChanged(AgentDto agent);
    Task AgentDeleted(Guid agentId);
    Task TelemetryReceived(TelemetrySampleDto sample);
    Task CommandChanged(AgentCommandDto command);
}
