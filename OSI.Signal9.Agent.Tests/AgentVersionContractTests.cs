using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.SignalR.Client;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OSI.Signal9.Agent.Identity;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Agents;

namespace OSI.Signal9.Agent.Tests;

/// <summary>
/// The agent's side of its version contract with api: it copes with what a newer api sends, and it recognises when api
/// refuses its version.
/// </summary>
[TestClass]
public sealed class AgentVersionContractTests(TestContext testContext)
{
    private CancellationToken Ct => testContext.CancellationToken;

    [TestMethod]
    public async Task A_command_type_newer_than_the_agent_is_reported_as_not_supported_instead_of_failing_the_fetch()
    {
        var api = Client(HttpStatusCode.OK, """
            {"items":[{"id":"6f1b8a52-3a4e-4b8e-9d55-0d6c2c6a1e01","deviceId":"6f1b8a52-3a4e-4b8e-9d55-0d6c2c6a1e02",
            "url":"/api/v1/devices/6f1b8a52-3a4e-4b8e-9d55-0d6c2c6a1e02/commands/6f1b8a52-3a4e-4b8e-9d55-0d6c2c6a1e01",
            "type":"ReimageMachine","parameters":null,"priority":"Urgent","timeoutSeconds":60,"status":"Pending",
            "createdAt":"2026-10-26T00:00:00+00:00","completedAt":null,"output":null,"error":null}],
            "page":1,"pageSize":100,"totalCount":1}
            """);

        var commands = await api.GetPendingCommandsAsync(Guid.NewGuid(), Ct);
        var result = await new CommandExecutor([], new FakeTimeProvider(), NullLogger<CommandExecutor>.Instance)
            .ExecuteAsync(commands.Single(), Ct);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("This command type is not supported by this agent version.", result.Error);
    }

    [TestMethod]
    public async Task Every_request_an_agent_connection_sends_carries_the_agent_s_version()
    {
        var transport = new RecordingTransport();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=agent", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var connection = new AgentConnection(new AgentIdentity(Guid.NewGuid(), certificate), transport, new FakeTimeProvider());

        await connection.Api.SendHeartbeatAsync(connection.AgentId, new(), Ct);
        connection.ConnectToHub();

        Assert.HasCount(2, transport.Requests, "a token request, then the heartbeat");
        foreach (var request in transport.Requests)
            Assert.AreEqual(AgentVersion.Current.ToString(), request.Headers.GetValues(AgentProtocol.VersionHeader).Single(), request.RequestUri!.ToString());
        Assert.AreEqual(AgentVersion.Current.ToString(), transport.HubHeaders![AgentProtocol.VersionHeader]);
    }

    [TestMethod]
    public async Task Api_refusing_the_agent_s_version_is_recognised()
    {
        var api = Client(HttpStatusCode.BadRequest, $$"""
            {"type":"{{AgentProtocol.UnsupportedVersion.ProblemType}}","title":"Agent version not supported","status":400,
            "minimumVersion":"2.0.0","maximumVersion":"2.1.0"}
            """, "application/problem+json");

        var refusal = await Assert.ThrowsExactlyAsync<UnsupportedAgentVersionException>(() =>
            api.SendHeartbeatAsync(Guid.NewGuid(), new(), Ct));

        Assert.AreEqual("2.0.0", refusal.MinimumVersion);
        Assert.AreEqual("2.1.0", refusal.MaximumVersion);
    }

    [TestMethod]
    public async Task The_token_endpoint_refusing_the_agent_s_version_is_recognised()
    {
        using var http = new HttpClient(new Answering(HttpStatusCode.BadRequest,
            $$"""{"error":"{{AgentProtocol.UnsupportedVersion.Error}}","error_description":"Update the agent."}""", "application/json"))
        { BaseAddress = new Uri("https://api.invalid") };
        using var tokens = new AgentTokens(Guid.NewGuid(), http, new FakeTimeProvider());

        await Assert.ThrowsExactlyAsync<UnsupportedAgentVersionException>(() => tokens.GetAsync(Ct));
    }

    [TestMethod]
    public async Task Other_bad_requests_are_not_taken_for_a_version_refusal()
    {
        var api = Client(HttpStatusCode.BadRequest, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","status":400}""",
            "application/problem+json");

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => api.SendHeartbeatAsync(Guid.NewGuid(), new(), Ct));
    }

    private static AgentApiClient Client(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(new HttpClient(new Answering(status, body, mediaType)) { BaseAddress = new Uri("https://api.invalid") });

    private sealed class Answering(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
    }

    /// <summary>Answers token requests with a token and anything else with an empty heartbeat response, recording each.</summary>
    private sealed class RecordingTransport : IAgentTransport
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public IReadOnlyDictionary<string, string>? HubHeaders { get; private set; }

        public HttpClient CreateClient(AgentIdentity? identity, params DelegatingHandler[] handlers) =>
            new(MutualTlsTransport.Chain(handlers, new Recording(this))) { BaseAddress = new Uri("https://api.invalid") };

        public HubConnection ConnectToHub(AgentIdentity identity, Func<Task<string?>> accessToken, IReadOnlyDictionary<string, string> headers)
        {
            HubHeaders = headers;
            return new HubConnectionBuilder().WithUrl("https://api.invalid/hubs/agents").Build();
        }

        private sealed class Recording(RecordingTransport transport) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                transport.Requests.Add(request);
                var body = request.RequestUri!.AbsolutePath.EndsWith(AgentProtocol.TokenEndpoint, StringComparison.Ordinal)
                    ? """{"access_token":"token","token_type":"Bearer","expires_in":600}"""
                    : """{"hasPendingWork":false}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
            }
        }
    }
}
