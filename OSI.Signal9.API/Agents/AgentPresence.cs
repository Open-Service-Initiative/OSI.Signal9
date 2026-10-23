using Microsoft.Extensions.Options;
using OSI.Signal9.API.Data;
using OSI.Signal9.Contracts.Agents;

namespace OSI.Signal9.API.Agents;

public sealed class AgentPresenceOptions
{
    public const string SectionName = "AgentPresence";

    /// <summary>An agent with no heartbeat for this long is reported Offline.</summary>
    public TimeSpan OfflineAfter { get; set; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Derives an agent's status from its maintenance flag and last heartbeat.
/// </summary>
public sealed class AgentPresence(IOptions<AgentPresenceOptions> options, TimeProvider time)
{
    public DateTime OnlineCutoff => time.GetUtcNow().UtcDateTime - options.Value.OfflineAfter;

    public AgentStatus StatusOf(Agent agent) =>
        agent.InMaintenance ? AgentStatus.Maintenance
        : agent.LastSeenAt >= OnlineCutoff ? AgentStatus.Online
        : AgentStatus.Offline;

    public IQueryable<Agent> WhereStatus(IQueryable<Agent> agents, AgentStatus status)
    {
        var cutoff = OnlineCutoff;
        return status switch
        {
            AgentStatus.Maintenance => agents.Where(a => a.InMaintenance),
            AgentStatus.Online => agents.Where(a => !a.InMaintenance && a.LastSeenAt >= cutoff),
            _ => agents.Where(a => !a.InMaintenance && a.LastSeenAt < cutoff),
        };
    }
}
