using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using OSI.Signal9.Agent.Identity;

namespace OSI.Signal9.Agent.Tests;

/// <summary>
/// An agent rotates its key and certificate before the certificate nears its expiry (#129, story 102), and never
/// strands itself: the new key is kept until the API has taken the new certificate, and a rotation whose response was
/// lost is finished on the next attempt.
/// </summary>
[TestClass]
public sealed class AgentKeyRotationTests : IDisposable
{
    private readonly string _stateDirectory = Path.Combine(Path.GetTempPath(), $"signal9-agent-tests-{Guid.NewGuid():N}");
    private readonly InMemoryKeyStore _keys = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero));
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly AgentIdentityStore _store;
    private readonly FakeApi _api = new();

    public AgentKeyRotationTests()
    {
        _store = new AgentIdentityStore(_keys, Options.Create(new AgentOptions
        {
            ApiBaseUrl = new Uri("https://api.invalid"),
            StateDirectory = _stateDirectory,
        }), _time);
        using var certificate = _store.CreateCertificate();
        _store.Save(_agentId, certificate);
        _api.Registered = certificate.RawData;
    }

    private AgentCertificateRotation Rotation => new(_store, new FakeTransport(_api), _time,
        NullLogger<AgentCertificateRotation>.Instance);

    private TimeSpan UntilDue => AgentIdentityStore.RenewalDue(_store.Load()!.Certificate) - _time.GetUtcNow();

    [TestMethod]
    public async Task A_certificate_far_from_expiry_is_kept()
    {
        var identity = _store.Load()!;
        _time.Advance(UntilDue - TimeSpan.FromMinutes(1));

        var kept = await Rotation.RotateIfDueAsync(identity, CancellationToken.None);

        Assert.AreEqual(identity.Certificate.Thumbprint, kept.Certificate.Thumbprint);
        Assert.AreEqual(0, _api.Requests);
    }

    [TestMethod]
    public async Task A_certificate_nearing_expiry_is_replaced_by_a_new_keys_and_the_old_key_is_deleted()
    {
        var identity = _store.Load()!;
        var oldKeys = _keys.Names;
        _time.Advance(UntilDue);

        var rotated = await Rotation.RotateIfDueAsync(identity, CancellationToken.None);

        Assert.AreEqual(_agentId, rotated.AgentId);
        Assert.AreNotEqual(identity.Certificate.Thumbprint, rotated.Certificate.Thumbprint);
        Assert.IsTrue(rotated.Certificate.HasPrivateKey);
        CollectionAssert.AreEqual(rotated.Certificate.RawData, _api.Registered);
        Assert.AreEqual($"/api/v1/agents/{_agentId}/certificate", _api.LastPutPath);
        Assert.AreEqual(rotated.Certificate.Thumbprint, _store.Load()!.Certificate.Thumbprint);
        Assert.AreEqual(1, _keys.Names.Count);
        Assert.IsFalse(oldKeys.SetEquals(_keys.Names));
        Assert.AreEqual(_time.GetUtcNow().UtcDateTime + AgentIdentityStore.CertificateLifetime, rotated.Certificate.NotAfter.ToUniversalTime());
    }

    [TestMethod]
    public async Task A_certificate_the_api_refuses_is_dropped_and_the_old_one_kept()
    {
        var identity = _store.Load()!;
        _time.Advance(UntilDue);
        _api.PutStatus = HttpStatusCode.BadRequest;

        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => Rotation.RotateIfDueAsync(identity, CancellationToken.None));

        Assert.AreEqual(identity.Certificate.Thumbprint, _store.Load()!.Certificate.Thumbprint);
        Assert.AreEqual(1, _keys.Names.Count);
        Assert.IsNull(_store.LoadNext());
    }

    [TestMethod]
    public async Task After_a_failed_attempt_the_next_one_offers_the_same_new_certificate()
    {
        var identity = _store.Load()!;
        _time.Advance(UntilDue);
        _api.PutStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => Rotation.RotateIfDueAsync(identity, CancellationToken.None));
        var offered = _api.LastPutCertificate;

        _api.PutStatus = HttpStatusCode.OK;
        var rotated = await Rotation.RotateIfDueAsync(identity, CancellationToken.None);

        Assert.AreEqual(offered, _api.LastPutCertificate);
        CollectionAssert.AreEqual(rotated.Certificate.RawData, _api.Registered);
    }

    [TestMethod]
    public async Task A_rotation_whose_response_was_lost_is_finished_on_the_next_attempt()
    {
        var identity = _store.Load()!;
        _time.Advance(UntilDue);
        _api.LoseResponse = true;
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() => Rotation.RotateIfDueAsync(identity, CancellationToken.None));
        Assert.AreEqual(identity.Certificate.Thumbprint, _store.Load()!.Certificate.Thumbprint);

        _api.LoseResponse = false;
        var rotated = await Rotation.RotateIfDueAsync(identity, CancellationToken.None);

        CollectionAssert.AreEqual(_api.Registered, rotated.Certificate.RawData);
        Assert.AreEqual(rotated.Certificate.Thumbprint, _store.Load()!.Certificate.Thumbprint);
        Assert.AreEqual(1, _keys.Names.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stateDirectory))
            Directory.Delete(_stateDirectory, recursive: true);
    }

    /// <summary>Stands in for the TPM or DPAPI key store.</summary>
    private sealed class InMemoryKeyStore : IAgentKeyStore
    {
        private readonly Dictionary<string, ECParameters> _keys = [];

        public HashSet<string> Names => [.. _keys.Keys];

        public ECDsa Create(string name)
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _keys[name] = key.ExportParameters(includePrivateParameters: true);
            return key;
        }

        public ECDsa? Open(string name) => _keys.TryGetValue(name, out var parameters) ? ECDsa.Create(parameters) : null;

        public void Delete(string name) => _keys.Remove(name);
    }

    /// <summary>
    /// The API's token endpoint and certificate resource for one agent: a token is issued only to the registered
    /// certificate, and putting a certificate registers it in place of the old one.
    /// </summary>
    private sealed class FakeApi
    {
        public byte[] Registered { get; set; } = [];

        public HttpStatusCode PutStatus { get; set; } = HttpStatusCode.OK;

        /// <summary>The API takes the certificate, but its response never arrives.</summary>
        public bool LoseResponse { get; set; }

        public int Requests { get; private set; }

        public string? LastPutPath { get; private set; }

        public string? LastPutCertificate { get; private set; }

        public async Task<HttpResponseMessage> SendAsync(X509Certificate2? presented, HttpRequestMessage request)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/connect/token")
            {
                return presented is not null && presented.RawData.AsSpan().SequenceEqual(Registered)
                    ? Json(HttpStatusCode.OK, """{"access_token":"t","token_type":"Bearer","expires_in":600}""")
                    : Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
            }

            LastPutPath = path;
            LastPutCertificate = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.GetProperty("certificate").GetString();
            if (PutStatus != HttpStatusCode.OK)
                return Json(PutStatus, "{}");
            using var certificate = X509Certificate2.CreateFromPem(LastPutCertificate);
            Registered = certificate.RawData;
            if (LoseResponse)
                throw new HttpRequestException("The connection was reset.");
            return Json(HttpStatusCode.OK, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class FakeTransport(FakeApi api) : IAgentTransport
    {
        public HttpClient CreateClient(AgentIdentity? identity, params DelegatingHandler[] handlers) =>
            new(MutualTlsTransport.Chain(handlers, new Network(api, identity?.Certificate))) { BaseAddress = new Uri("https://api.invalid") };

        public Microsoft.AspNetCore.SignalR.Client.HubConnection ConnectToHub(AgentIdentity identity, Func<Task<string?>> accessToken,
            IReadOnlyDictionary<string, string> headers) =>
            throw new NotSupportedException();

        private sealed class Network(FakeApi api, X509Certificate2? certificate) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                api.SendAsync(certificate, request);
        }
    }
}
