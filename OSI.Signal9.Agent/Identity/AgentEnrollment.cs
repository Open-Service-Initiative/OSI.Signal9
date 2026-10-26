using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Agents;

namespace OSI.Signal9.Agent.Identity;

/// <summary>
/// Enrolls this agent instance once (stories 98, 99): it generates a non-exportable key and a self-signed certificate
/// for it, and presents the certificate with the configured enrollment token. Signal9 registers the certificate as the
/// agent's credential, and the instance keeps its agent ID and certificate for every later start.
/// </summary>
public sealed class AgentEnrollment(
    AgentIdentityStore identities,
    IAgentTransport transport,
    SystemInfoProvider systemInfo,
    IOptions<AgentOptions> options,
    ILogger<AgentEnrollment> logger)
{
    /// <summary>The instance's identity, enrolling first if it hasn't yet.</summary>
    public async Task<AgentIdentity> EnsureEnrolledAsync(CancellationToken cancellationToken)
    {
        if (identities.Load() is { } enrolled)
            return enrolled;

        if (options.Value.EnrollmentToken is not { Length: > 0 } enrollmentToken)
            throw new InvalidOperationException(
                $"Agent instance '{options.Value.InstanceId}' isn't enrolled, and no enrollment token is configured (Agent:EnrollmentToken).");

        var certificate = identities.CreateCertificate();
        using var http = transport.CreateClient(identity: null, new AgentVersionHandler());
        using var response = await http.PostAsJsonAsync(AgentProtocol.Routes.Enroll,
            systemInfo.GetEnrollment(enrollmentToken, certificate.ExportCertificatePem()), AgentJson.Options, cancellationToken);
        await AgentApiClient.EnsureSuccessAsync(response, cancellationToken);
        var agent = (await response.Content.ReadFromJsonAsync<AgentDto>(AgentJson.Options, cancellationToken))!;

        identities.Save(agent.Id, certificate);
        logger.LogInformation("Instance {Instance} enrolled as agent {AgentId} of device {DeviceId} in tenant {TenantId}",
            options.Value.InstanceId, agent.Id, agent.DeviceId, agent.TenantId);
        return new AgentIdentity(agent.Id, certificate);
    }
}
