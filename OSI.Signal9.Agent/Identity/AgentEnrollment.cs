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
    IMachineFactsSource machine,
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
            Request(machine, enrollmentToken, certificate.ExportCertificatePem()), AgentJson.Options, cancellationToken);
        await AgentApiClient.EnsureSuccessAsync(response, cancellationToken);
        var agent = (await response.Content.ReadFromJsonAsync<AgentDto>(AgentJson.Options, cancellationToken))!;

        identities.Save(agent.Id, certificate);
        logger.LogInformation("Instance {Instance} enrolled as agent {AgentId} of device {DeviceId} in tenant {TenantId}",
            options.Value.InstanceId, agent.Id, agent.DeviceId, agent.TenantId);
        return new AgentIdentity(agent.Id, certificate);
    }

    /// <summary>
    /// The enrollment request presenting <paramref name="enrollmentToken"/> and the certificate, with the machine's facts
    /// cut to api's limits: an OS's own description can run past them (Darwin's does).
    /// </summary>
    private static EnrollAgentRequest Request(IMachineFactsSource machine, string enrollmentToken, string certificatePem)
    {
        var facts = machine.GetFacts();
        var hardware = machine.GetHardware();
        return new EnrollAgentRequest
        {
            EnrollmentToken = enrollmentToken,
            Certificate = certificatePem,
            MachineName = facts.MachineName,
            Domain = facts.Domain,
            OsFamily = facts.OsFamily,
            OperatingSystem = Truncate(facts.OperatingSystem, EnrollAgentRequest.OperatingSystemMaxLength)!,
            OsVersion = facts.OsVersion,
            Architecture = facts.Architecture,
            ProcessorName = facts.ProcessorName,
            ProcessorCores = facts.ProcessorCores,
            TotalMemoryMb = facts.TotalMemoryMb,
            IpAddress = facts.IpAddress,
            MacAddress = facts.MacAddress,
            SmbiosUuid = hardware.SmbiosUuid,
            Manufacturer = Truncate(hardware.Manufacturer, EnrollAgentRequest.FirmwareStringMaxLength),
            SerialNumber = Truncate(hardware.SerialNumber, EnrollAgentRequest.FirmwareStringMaxLength),
            OsInstalledAt = machine.GetOsInstalledAt(),
        };
    }

    private static string? Truncate(string? value, int length) => value is not null && value.Length > length ? value[..length] : value;
}
