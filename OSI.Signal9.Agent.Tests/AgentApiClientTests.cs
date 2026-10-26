using System.Net;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.Agent.Tests;

/// <summary>How the agent's API client treats the API's answers.</summary>
[TestClass]
public sealed class AgentApiClientTests
{
    private static AgentApiClient Client(HttpStatusCode status) =>
        new(new HttpClient(new Answering(status)) { BaseAddress = new Uri("https://api.invalid") });

    private static CreateTelemetrySampleRequest Sample => new() { CollectedAt = DateTimeOffset.UnixEpoch };

    [TestMethod]
    public async Task Telemetry_refused_while_the_agent_awaits_identification_is_dropped_not_fatal()
    {
        // 409: the agent has no device until a person decides (#131); its heartbeat still reports a replacement.
        await Client(HttpStatusCode.Conflict).SendTelemetryAsync(Guid.NewGuid(), Sample, CancellationToken.None);
    }

    [TestMethod]
    public async Task Other_refused_telemetry_fails()
    {
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            Client(HttpStatusCode.Forbidden).SendTelemetryAsync(Guid.NewGuid(), Sample, CancellationToken.None));
    }

    private sealed class Answering(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }
}
