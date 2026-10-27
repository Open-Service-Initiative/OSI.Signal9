using System.Reflection;
using OSI.Signal9.Contracts.Agents;

namespace OSI.Signal9.Agent.Services;

/// <summary>
/// This agent's version, which it sends on every request (<see cref="AgentProtocol.VersionHeader"/>) so api can refuse
/// one it no longer supports. It comes from the build's one version (Directory.Build.props), shared with api.
/// </summary>
public static class AgentVersion
{
    public static SemanticVersion Current { get; } = SemanticVersion.Parse(
        typeof(AgentVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]);

    /// <summary>The headers that carry it, for the hub connection.</summary>
    public static IReadOnlyDictionary<string, string> Headers { get; } =
        new Dictionary<string, string> { [AgentProtocol.VersionHeader] = Current.ToString() };
}

/// <summary>Sends each request with the agent's version.</summary>
public sealed class AgentVersionHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(AgentProtocol.VersionHeader);
        request.Headers.Add(AgentProtocol.VersionHeader, AgentVersion.Current.ToString());
        return base.SendAsync(request, cancellationToken);
    }
}
