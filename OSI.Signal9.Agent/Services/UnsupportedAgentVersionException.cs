namespace OSI.Signal9.Agent.Services;

/// <summary>api doesn't support this agent's version: the agent must be updated, and retrying can't help.</summary>
public sealed class UnsupportedAgentVersionException(string message, string? minimumVersion, string? maximumVersion)
    : Exception(message)
{
    public string? MinimumVersion { get; } = minimumVersion;

    public string? MaximumVersion { get; } = maximumVersion;
}
