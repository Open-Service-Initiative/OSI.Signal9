using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OSI.Signal9.Agent.Identity;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Devices;

namespace OSI.Signal9.Agent.Tests;

/// <summary>
/// An agent instance's identity (#128): its non-exportable key, certificate and state, all scoped to the instance so
/// instances for different deployments or root tenant trees on one machine never collide.
/// </summary>
[TestClass]
public sealed class AgentIdentityTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), $"signal9-agent-tests-{Guid.NewGuid():N}");
    private readonly InMemoryKeyStore _keys = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));

    private AgentIdentityStore Store(string instanceId) => new(_keys, Options.Create(Agent(instanceId)), _time);

    private AgentOptions Agent(string instanceId, string? enrollmentToken = null) => new()
    {
        ApiBaseUrl = new Uri("https://api.invalid"),
        InstanceId = instanceId,
        StateDirectory = _stateDirectory,
        EnrollmentToken = enrollmentToken,
    };

    [TestMethod]
    public void An_instance_that_never_enrolled_has_no_identity()
    {
        Assert.IsNull(Store("default").Load());
    }

    [TestMethod]
    public void An_enrolled_instance_loads_its_agent_id_and_certificate_with_its_private_key()
    {
        var store = Store("default");
        var agentId = Guid.NewGuid();
        using var certificate = store.CreateCertificate();
        store.Save(agentId, certificate);

        var identity = Store("default").Load();

        Assert.IsNotNull(identity);
        Assert.AreEqual(agentId, identity.AgentId);
        Assert.AreEqual(certificate.Thumbprint, identity.Certificate.Thumbprint);
        Assert.IsTrue(identity.Certificate.HasPrivateKey);
    }

    [TestMethod]
    public void The_certificate_is_a_self_signed_p256_client_certificate_valid_for_a_year()
    {
        using var certificate = Store("default").CreateCertificate();

        Assert.AreEqual(certificate.SubjectName.Name, certificate.IssuerName.Name);
        using var key = certificate.GetECDsaPublicKey();
        Assert.IsNotNull(key);
        Assert.AreEqual(256, key.KeySize);
        Assert.AreEqual(X509KeyUsageFlags.DigitalSignature, certificate.Extensions.OfType<X509KeyUsageExtension>().Single().KeyUsages);
        Assert.AreEqual("1.3.6.1.5.5.7.3.2",
            certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>().Single().Value);
        Assert.IsFalse(certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.IsTrue(certificate.NotBefore.ToUniversalTime() <= _time.GetUtcNow().UtcDateTime);
        Assert.AreEqual(_time.GetUtcNow().UtcDateTime + AgentIdentityStore.CertificateLifetime, certificate.NotAfter.ToUniversalTime());
    }

    [TestMethod]
    public void Two_instances_on_one_machine_keep_separate_keys_certificates_and_state()
    {
        var contoso = Store("contoso");
        var fabrikam = Store("fabrikam");
        Guid contosoAgent = Guid.NewGuid(), fabrikamAgent = Guid.NewGuid();
        using var contosoCertificate = contoso.CreateCertificate();
        contoso.Save(contosoAgent, contosoCertificate);
        using var fabrikamCertificate = fabrikam.CreateCertificate();
        fabrikam.Save(fabrikamAgent, fabrikamCertificate);

        var first = Store("contoso").Load()!;
        var second = Store("fabrikam").Load()!;

        Assert.AreNotEqual(contoso.KeyName, fabrikam.KeyName);
        Assert.AreNotEqual(contoso.StatePath, fabrikam.StatePath);
        Assert.AreEqual(contosoAgent, first.AgentId);
        Assert.AreEqual(fabrikamAgent, second.AgentId);
        Assert.AreEqual(contosoCertificate.Thumbprint, first.Certificate.Thumbprint);
        Assert.AreEqual(fabrikamCertificate.Thumbprint, second.Certificate.Thumbprint);
        Assert.IsNull(Store("other").Load());
    }

    [TestMethod]
    public void An_instance_whose_key_is_gone_has_no_identity()
    {
        var store = Store("default");
        using var certificate = store.CreateCertificate();
        store.Save(Guid.NewGuid(), certificate);

        _keys.Delete(store.KeyName);

        Assert.IsNull(store.Load());
    }

    [TestMethod]
    [DataRow("default", true)]
    [DataRow("contoso-msp", true)]
    [DataRow("../escape", false)]
    [DataRow("a/b", false)]
    [DataRow("", false)]
    [DataRow("-leading", false)]
    public void Instance_ids_are_safe_names_for_keys_and_folders(string instanceId, bool valid)
    {
        var options = Agent(instanceId);

        var isValid = Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);

        Assert.AreEqual(valid, isValid);
    }

    [TestMethod]
    public async Task An_instance_that_never_enrolled_needs_an_enrollment_token()
    {
        var enrollment = Enrollment(Agent("default"), new RecordingHandler(HttpStatusCode.OK, "{}"));

        var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => enrollment.EnsureEnrolledAsync(CancellationToken.None));

        StringAssert.Contains(refused.Message, "Agent:EnrollmentToken");
    }

    [TestMethod]
    public async Task An_enrolled_instance_does_not_enroll_again()
    {
        var store = Store("default");
        var agentId = Guid.NewGuid();
        using var certificate = store.CreateCertificate();
        store.Save(agentId, certificate);
        var network = new RecordingHandler(HttpStatusCode.InternalServerError, "");

        var identity = await Enrollment(Agent("default", "s9e_token"), network).EnsureEnrolledAsync(CancellationToken.None);

        Assert.AreEqual(agentId, identity.AgentId);
        Assert.AreEqual(0, network.Requests);
    }

    [TestMethod]
    public async Task An_instance_enrolls_with_its_token_and_new_certificate_and_keeps_the_agent_id()
    {
        var agentId = Guid.NewGuid();
        var network = new RecordingHandler(HttpStatusCode.Created,
            $$"""{"id":"{{agentId}}","url":"/api/v1/agents/{{agentId}}","deviceId":"{{Guid.NewGuid()}}","tenantId":"{{Guid.NewGuid()}}","isActive":true,"enrolledAt":"2026-10-25T12:00:00Z","lastSeenAt":"2026-10-25T12:00:00Z"}""");

        var identity = await Enrollment(Agent("default", "s9e_token"), network).EnsureEnrolledAsync(CancellationToken.None);

        Assert.AreEqual(agentId, identity.AgentId);
        Assert.AreEqual("/api/v1/agents", network.LastPath);
        StringAssert.Contains(network.LastBody, "\"enrollmentToken\":\"s9e_token\"");
        StringAssert.Contains(network.LastBody, "BEGIN CERTIFICATE");
        Assert.DoesNotContain("PRIVATE KEY", network.LastBody!);
        Assert.AreEqual(identity.Certificate.Thumbprint, Store("default").Load()!.Certificate.Thumbprint);
    }

    [TestMethod]
    public async Task Tokens_are_reused_until_they_near_expiry()
    {
        var network = new RecordingHandler(HttpStatusCode.OK, """{"access_token":"t","token_type":"Bearer","expires_in":600}""");
        using var http = new HttpClient(network) { BaseAddress = new Uri("https://api.invalid") };
        using var tokens = new AgentTokens(Guid.NewGuid(), http, _time);

        await tokens.GetAsync();
        _time.Advance(TimeSpan.FromMinutes(8));
        await tokens.GetAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await tokens.GetAsync();

        Assert.AreEqual(2, network.Requests);
        Assert.AreEqual("/connect/token", network.LastPath);
        StringAssert.Contains(network.LastBody, "grant_type=client_credentials");
        StringAssert.Contains(network.LastBody, "scope=agent");
    }

    [TestMethod]
    public async Task Enrollment_reports_the_machine_s_facts_within_api_s_limits()
    {
        var installedAt = new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero);
        var machine = new FakeMachine
        {
            // Darwin's uname -srv runs well past api's 100 characters.
            Facts = new FakeMachine().Facts with { OsFamily = OsFamily.MacOS, OperatingSystem = "Darwin 24.1.0 " + new string('x', 200) },
            Hardware = new HardwareIdentity(Guid.Parse("4c4c4544-0042-3510-8058-b4c04f4d5a31"), new string('A', 300), "C02XYZ"),
            OsInstalledAt = installedAt,
        };
        var agentId = Guid.NewGuid();
        var network = new RecordingHandler(HttpStatusCode.Created,
            $$"""{"id":"{{agentId}}","url":"/api/v1/agents/{{agentId}}","deviceId":"{{Guid.NewGuid()}}","tenantId":"{{Guid.NewGuid()}}","isActive":true,"enrolledAt":"2026-10-25T12:00:00Z","lastSeenAt":"2026-10-25T12:00:00Z"}""");

        await Enrollment(Agent("default", "s9e_token"), network, machine).EnsureEnrolledAsync(CancellationToken.None);

        var request = JsonSerializer.Deserialize<EnrollAgentRequest>(network.LastBody!, AgentJson.Options)!;
        Assert.AreEqual(OsFamily.MacOS, request.OsFamily);
        Assert.HasCount(EnrollAgentRequest.OperatingSystemMaxLength, request.OperatingSystem);
        StringAssert.StartsWith(request.OperatingSystem, "Darwin 24.1.0 ");
        Assert.HasCount(EnrollAgentRequest.FirmwareStringMaxLength, request.Manufacturer!);
        Assert.AreEqual("C02XYZ", request.SerialNumber);
        Assert.AreEqual(machine.Hardware.SmbiosUuid, request.SmbiosUuid);
        Assert.AreEqual(installedAt, request.OsInstalledAt);
        Assert.AreEqual("FAKE-PC", request.MachineName);
        Assert.AreEqual("10.0.0.2", request.IpAddress);
        Assert.IsTrue(Validator.TryValidateObject(request, new ValidationContext(request), [], validateAllProperties: true));
    }

    [TestMethod]
    public async Task A_refused_token_is_replaced_on_the_next_request()
    {
        var network = new RecordingHandler(HttpStatusCode.OK, """{"access_token":"t","token_type":"Bearer","expires_in":600}""");
        using var http = new HttpClient(network) { BaseAddress = new Uri("https://api.invalid") };
        using var tokens = new AgentTokens(Guid.NewGuid(), http, _time);

        tokens.Refused(await tokens.GetAsync());
        await tokens.GetAsync();

        Assert.AreEqual(2, network.Requests);
    }

    private AgentEnrollment Enrollment(AgentOptions options, RecordingHandler network, FakeMachine? machine = null) => new(
        new AgentIdentityStore(_keys, Options.Create(options), _time),
        new FakeTransport(network),
        machine ?? new FakeMachine(),
        Options.Create(options),
        NullLogger<AgentEnrollment>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
            Directory.Delete(_stateDirectory, recursive: true);
    }

    /// <summary>Stands in for the TPM or DPAPI key store.</summary>
    private sealed class InMemoryKeyStore : IAgentKeyStore
    {
        private readonly Dictionary<string, ECParameters> _keys = [];

        public ECDsa Create(string name)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _keys[name] = key.ExportParameters(includePrivateParameters: true);
            return key;
        }

        public ECDsa? Open(string name) => _keys.TryGetValue(name, out var parameters) ? ECDsa.Create(parameters) : null;

        public void Delete(string name) => _keys.Remove(name);
    }

    private sealed class FakeTransport(HttpMessageHandler network) : IAgentTransport
    {
        public HttpClient CreateClient(AgentIdentity? identity, params DelegatingHandler[] handlers) =>
            new(MutualTlsTransport.Chain(handlers, network), disposeHandler: false) { BaseAddress = new Uri("https://api.invalid") };

        public Microsoft.AspNetCore.SignalR.Client.HubConnection ConnectToHub(AgentIdentity identity, Func<Task<string?>> accessToken,
            IReadOnlyDictionary<string, string> headers) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public string? LastPath { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
