using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OSI.Signal9.API;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Server.Data;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Hosts <c>api</c> and <c>auth</c> in memory against their own database on the shared PostgreSQL container, with
/// one controllable clock. Clients from <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> call <c>api</c>
/// with an access token for a seeded test client holding every <see cref="GrantableScopes">grantable scope</see>.
/// </summary>
public sealed class Signal9ApiFactory : WebApplicationFactory<ApiHost>
{
    private readonly bool _migrated;
    private readonly IReadOnlyDictionary<string, string?> _settings;

    /// <param name="migrated">Whether the database starts with the schema applied, or empty.</param>
    /// <param name="settings">Configuration for both hosts, on top of the fixture's own.</param>
    /// <param name="start">When the shared clock starts, if not now.</param>
    public Signal9ApiFactory(bool migrated = true, IReadOnlyDictionary<string, string?>? settings = null,
        DateTimeOffset? start = null)
    {
        Time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds((start ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()));
        Entra = new FakeEntra(Time);
        _migrated = migrated;
        _settings = settings ?? new Dictionary<string, string?>();
        ConnectionString = PostgresContainer.CreateDatabase(migrated);
        ClientOptions.BaseAddress = HttpsLocalhost;
    }

    private const string DefaultClientId = "test-client";

    private AccessToken? _defaultToken;
    private Signal9AuthFactory? _auth;
    private Signal9WebFactory? _web;
    private readonly List<Signal9WebFactory> _webReplicas = [];

    /// <summary>
    /// The clock every host shares. It starts at the real time unless a test says otherwise, in whole seconds as
    /// certificates hold it, because X.509 certificates made with the real clock (the tests', and Entra's tokens) must
    /// be valid on it too.
    /// </summary>
    public FakeTimeProvider Time { get; }

    /// <summary>The configuration every host gets on top of the fixture's own.</summary>
    internal IReadOnlyDictionary<string, string?> Settings => _settings;

    /// <summary>The stand-in for Entra that people sign in at through <c>auth</c>.</summary>
    public FakeEntra Entra { get; }

    /// <summary>Connection string of this factory's database.</summary>
    public string ConnectionString { get; }

    /// <summary>The <c>auth</c> host, sharing this factory's database and clock.</summary>
    public Signal9AuthFactory Auth => _auth ??= new Signal9AuthFactory(ConnectionString, Time, settings: _settings, entra: Entra);

    /// <summary>The <c>web</c> host, the dashboard's backend-for-frontend, sharing this factory's database and clock.</summary>
    public Signal9WebFactory Web => _web ??= new Signal9WebFactory(this);

    /// <summary>Another <c>web</c> replica sharing the database, as when the dashboard scales out (story 54).</summary>
    public Signal9WebFactory CreateWebReplica()
    {
        var replica = new Signal9WebFactory(this);
        _webReplicas.Add(replica);
        return replica;
    }

    /// <summary>
    /// Another <c>api</c> replica sharing the database, clock and configuration, as when api scales out (story 117).
    /// </summary>
    public WebApplicationFactory<ApiHost> CreateApiReplica()
    {
        var replica = new ApiReplica(this);
        _apiReplicas.Add(replica);
        return replica;
    }

    private readonly List<ApiReplica> _apiReplicas = [];

    private sealed class ApiReplica(Signal9ApiFactory owner) : WebApplicationFactory<ApiHost>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => owner.ConfigureApiHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder) => ConfigureApiHost(builder);

    private void ConfigureApiHost(IWebHostBuilder builder)
    {
        ConfigureSharedHost(builder, ConnectionString, Time, _settings);
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpContextAccessor();
            services.RemoveAll<IAssignedTenants>();
            services.AddScoped<IAssignedTenants>(sp =>
                new FixtureAssignedTenants(sp.GetRequiredService<IHttpContextAccessor>(), sp.GetRequiredService<CallerAccess>(), this));
        });
    }

    private readonly ConcurrentDictionary<string, Guid[]> _assignments = new();

    /// <summary>
    /// A client for <c>api</c> standing in for a caller with role assignments at exactly <paramref name="assignedTenants"/>
    /// (none: a caller who sees nothing) for every scope, with a token holding these scopes: an application standing in
    /// for a person, for tests of the tenant filter alone.
    /// </summary>
    public async Task<HttpClient> CreateAssignedClientAsync(Guid[] assignedTenants, params string[] scopes)
    {
        var clientId = $"assigned-client-{Interlocked.Increment(ref _clients)}";
        _assignments[clientId] = assignedTenants;
        await SeedClientAsync(clientId, scopes);
        var token = await RequestTokenAsync(clientId, scopes);
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    /// <summary>Every catalogue scope a person can be issued through the dashboard or a delegated client.</summary>
    public static string[] PersonScopes => [.. GrantableScopes.Where(s => s != OSI.Signal9.Server.Identity.Scopes.Agent)];

    private const string PersonClientId = "person-test-app";
    private readonly SemaphoreSlim _personClientSeeding = new(1, 1);
    private bool _personClientSeeded;

    /// <summary>
    /// A client for <c>api</c> with an access token for <paramref name="user"/>, who signs in at Entra through
    /// <c>auth</c> with a client using authorization code, asking for these scopes (every person scope if none).
    /// </summary>
    public async Task<HttpClient> CreatePersonClientAsync(EntraUser user, params string[] scopes)
    {
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await PersonTokenAsync(user, scopes));
        return client;
    }

    /// <summary>A Superadministrator who just signed in with MFA, as sensitive actions need (#120).</summary>
    public EntraUser Superadministrator() =>
        EntraUser.WithMfa() with { TenantId = Entra.DeployingTenantId, Roles = [OSI.Signal9.Server.Identity.Superadministrator.AppRole] };

    /// <summary>
    /// An access token for <paramref name="user"/>, who signs in at Entra through <c>auth</c> with a client using
    /// authorization code, asking for these scopes (every person scope if none).
    /// </summary>
    public async Task<string> PersonTokenAsync(EntraUser user, params string[] scopes)
    {
        await _personClientSeeding.WaitAsync();
        try
        {
            if (!_personClientSeeded)
                await SeedCodeClientAsync(PersonClientId, PersonScopes);
            _personClientSeeded = true;
        }
        finally
        {
            _personClientSeeding.Release();
        }

        using var browser = new Browser(Auth.Server);
        var flow = new CodeFlow(PersonClientId);
        var scope = string.Join(' ', ["openid", .. scopes.Length > 0 ? scopes : PersonScopes]);
        var code = flow.CodeFrom(await browser.AuthorizeAsync(flow.AuthorizeRequest(scope), Entra, user));
        using var auth = Auth.CreateClient();
        var response = await auth.PostAsync("connect/token", flow.Redemption(code));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessToken>())!.Value;
    }

    /// <summary>The applications the fixture seeds to act as themselves, with client credentials.</summary>
    private readonly ConcurrentDictionary<string, bool> _applications = new();

    /// <summary>
    /// The fixture's assigned tenants: those given to <see cref="CreateAssignedClientAsync"/>'s client; every tenant
    /// for the fixture's other client-credentials clients (and work outside a request), standing in for a
    /// Superadministrator so tests can work across tenant trees; and api's own for everyone else, such as people.
    /// </summary>
    private sealed class FixtureAssignedTenants(IHttpContextAccessor http, CallerAccess access, Signal9ApiFactory factory)
        : IAssignedTenants
    {
        private readonly CallerAssignedTenants _api = new(http, access);

        private IAssignedTenants Current =>
            http.HttpContext?.User.FindFirst(Claims.Subject)?.Value is not { } subject ? AllTenants.Instance
            : factory._assignments.TryGetValue(subject, out var assigned) ? new Fixed(assigned)
            : factory._applications.ContainsKey(subject) ? AllTenants.Instance
            : _api;

        public bool IsSuperadministrator => Current.IsSuperadministrator;

        public IReadOnlyCollection<Guid> TenantIds => Current.TenantIds;

        private sealed record Fixed(IReadOnlyCollection<Guid> TenantIds) : IAssignedTenants
        {
            public bool IsSuperadministrator => false;
        }

        private sealed class AllTenants : IAssignedTenants
        {
            public static readonly AllTenants Instance = new();

            public bool IsSuperadministrator => true;

            public IReadOnlyCollection<Guid> TenantIds => [];
        }
    }

    /// <summary>The internal port both hosts serve their health probes on.</summary>
    public const int ProbePort = 8081;

    /// <summary>
    /// A request header the fixture turns into the connection's remote IP address, standing in for distinct callers.
    /// </summary>
    public const string ClientIpHeader = "X-Test-Client-IP";

    /// <summary>
    /// What <c>auth</c> and <c>api</c> share: the database, clock, issuer, settings and the key-encryption key that
    /// wraps the stored keys.
    /// </summary>
    internal static void ConfigureSharedHost(IWebHostBuilder builder, string connectionString, FakeTimeProvider time,
        IReadOnlyDictionary<string, string?> settings, string? kekPath = null)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:signal9", connectionString);
        builder.UseSetting("Identity:Issuer", Signal9AuthFactory.Issuer);
        builder.UseSetting("Identity:ApiUri", Signal9AuthFactory.ApiUri);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("KeyEncryption:Provider", "Certificate");
        builder.UseSetting("KeyEncryption:Certificate:Path", kekPath ?? TestKeys.KekPath);
        builder.UseSetting("HealthProbes:Port", ProbePort.ToString(CultureInfo.InvariantCulture));
        // Container Apps' ingress is the proxy in front of every host; api takes client certificates from it. A test
        // that sets NoProxyNetwork runs without one.
        if (!settings.ContainsKey(NoProxyNetwork))
            builder.UseSetting("ForwardedHeaders:KnownNetworks:0", TrustedProxyNetwork);
        builder.UseSetting("ForwardedHeaders:ForwardClientCertificate", "true");
        builder.UseSetting("Identity:TlsClientCertificateAuthorities:0", TestCertificates.AuthorityPem);
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, ClientIpFromHeader>();
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(time);
        });
    }

    /// <summary>The network the fixture trusts forwarded headers from, standing in for Container Apps' ingress.</summary>
    public const string TrustedProxyNetwork = "10.0.0.0/24";

    /// <summary>A setting that leaves the hosts with no trusted proxy network, as self-hosted by default.</summary>
    public const string NoProxyNetwork = "ForwardedHeaders:KnownNetworks";

    /// <summary>A peer inside <see cref="TrustedProxyNetwork"/>.</summary>
    public static readonly IPAddress TrustedProxy = IPAddress.Parse("10.0.0.4");

    /// <summary>A peer outside <see cref="TrustedProxyNetwork"/>.</summary>
    public static readonly IPAddress UntrustedPeer = IPAddress.Parse("203.0.113.9");

    /// <summary>The base address of every test client. OpenIddict accepts only HTTPS requests.</summary>
    public static readonly Uri HttpsLocalhost = new("https://localhost");

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        if (!_migrated)
            return;

        _defaultToken ??= Task.Run(async () =>
        {
            await SeedClientAsync(DefaultClientId, GrantableScopes);
            return await RequestTokenAsync(DefaultClientId, GrantableScopes);
        }).GetAwaiter().GetResult();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _defaultToken.Value);
    }

    /// <summary>
    /// Every catalogue scope a client can obtain with client credentials: the OpenID Connect scopes need a person.
    /// </summary>
    public static readonly string[] GrantableScopes = OSI.Signal9.Server.Identity.Scopes.All.Keys
        .Except([OpenIddictConstants.Scopes.OpenId, OpenIddictConstants.Scopes.Profile, OpenIddictConstants.Scopes.OfflineAccess])
        .ToArray();

    private int _clients;

    /// <summary>A client for <c>api</c> with a token for a newly seeded client holding exactly these scopes.</summary>
    public async Task<HttpClient> CreateClientAsync(params string[] scopes)
    {
        var clientId = $"scoped-client-{Interlocked.Increment(ref _clients)}";
        await SeedClientAsync(clientId, scopes);
        var token = await RequestTokenAsync(clientId, scopes);
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    /// <summary>A client for <c>api</c> that sends no access token.</summary>
    public HttpClient CreateAnonymousClient()
    {
        var client = Server.CreateClient();
        client.BaseAddress = HttpsLocalhost;
        return client;
    }

    /// <summary>
    /// A client for <c>api</c> whose requests arrive from <paramref name="peer"/>, optionally over a TLS connection
    /// that presented <paramref name="tlsCertificate"/> (the self-hosted path, where Kestrel reads the handshake).
    /// </summary>
    public HttpClient CreatePeerClient(IPAddress peer, X509Certificate2? tlsCertificate = null) =>
        CreatePeerClient(Server, peer, tlsCertificate);

    internal static HttpClient CreatePeerClient(TestServer server, IPAddress peer, X509Certificate2? tlsCertificate) =>
        new(server.CreateHandler(context =>
        {
            context.Connection.RemoteIpAddress = peer;
            if (tlsCertificate is not null)
                context.Connection.ClientCertificate = tlsCertificate;
        }))
        { BaseAddress = HttpsLocalhost };

    /// <summary>A client for <c>api</c> behind Container Apps' ingress, which forwards <paramref name="certificate"/>.</summary>
    public HttpClient CreateForwardedCertificateClient(X509Certificate2? certificate)
    {
        var client = CreatePeerClient(TrustedProxy);
        if (certificate is not null)
            client.DefaultRequestHeaders.Add(ClientCertificateHeader, TestCertificates.ContainerAppsHeader(certificate));
        return client;
    }

    public const string ClientCertificateHeader = "X-Forwarded-Client-Cert";

    /// <summary>
    /// Seeds a confidential client that authenticates with <c>self_signed_tls_client_auth</c>: its certificate is
    /// registered in its key set (RFC 8705 §2.2).
    /// </summary>
    public Task SeedSelfSignedTlsClientAsync(string clientId, X509Certificate2 certificate, params string[] scopes) =>
        SeedSelfSignedTlsClientAsync(clientId, JsonWebKeyConverter.ConvertFromX509SecurityKey(new X509SecurityKey(certificate)), scopes);

    /// <summary>Seeds a <c>self_signed_tls_client_auth</c> client whose key set holds exactly <paramref name="key"/>.</summary>
    public async Task SeedSelfSignedTlsClientAsync(string clientId, JsonWebKey key, params string[] scopes)
    {
        var descriptor = MtlsClient(clientId, scopes);
        descriptor.JsonWebKeySet = new JsonWebKeySet { Keys = { key } };
        await CreateApplicationAsync(descriptor);
    }

    /// <summary>
    /// Seeds a confidential client that authenticates with <c>tls_client_auth</c>, registered with one RFC 8705 §2.1.2
    /// subject parameter (for example <c>tls_client_auth_subject_dn</c>), or none.
    /// </summary>
    public async Task SeedTlsClientAsync(string clientId, IReadOnlyDictionary<string, string> subject, params string[] scopes)
    {
        var descriptor = MtlsClient(clientId, scopes);
        foreach (var (name, value) in subject)
            descriptor.Properties[name] = JsonSerializer.SerializeToElement(value);
        await CreateApplicationAsync(descriptor);
    }

    private static OpenIddictApplicationDescriptor MtlsClient(string clientId, string[] scopes)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Confidential,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.Endpoints.Introspection,
                Permissions.GrantTypes.ClientCredentials,
            },
        };
        foreach (var name in scopes)
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + name);
        return descriptor;
    }

    /// <summary>Client credentials for a client that authenticates with its certificate alone.</summary>
    public static FormUrlEncodedContent MtlsClientCredentials(string clientId, params string[] scopes)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.ClientCredentials,
            ["client_id"] = clientId,
        };
        if (scopes.Length > 0)
            form["scope"] = string.Join(' ', scopes);
        return new FormUrlEncodedContent(form);
    }

    /// <summary>The secret of every client seeded by <see cref="SeedClientAsync"/>.</summary>
    public const string ClientSecret = "test-secret-that-is-long-enough";

    /// <summary>
    /// Seeds a confidential client that authenticates with <see cref="ClientSecret"/> and may use client credentials,
    /// revocation and introspection, and may request the given scopes. Only scopes in the catalogue can be issued.
    /// </summary>
    public async Task SeedClientAsync(string clientId, params string[] scopes)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = ClientSecret,
            ClientType = ClientTypes.Confidential,
            Permissions =
            {
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.Endpoints.Introspection,
                Permissions.GrantTypes.ClientCredentials,
            },
        };
        foreach (var name in scopes)
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + name);
        await CreateApplicationAsync(descriptor);
    }

    /// <summary>
    /// Seeds a confidential client that signs people in with authorization code at <see cref="CodeFlow.RedirectUri"/>,
    /// requires PKCE, authenticates with <see cref="ClientSecret"/> and may request the given scopes. Like Signal9's own
    /// web client, it never asks people for consent.
    /// </summary>
    public async Task SeedCodeClientAsync(string clientId, params string[] scopes)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = ClientSecret,
            ClientType = ClientTypes.Confidential,
            ConsentType = ConsentTypes.Implicit,
            RedirectUris = { new Uri(CodeFlow.RedirectUri) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Introspection,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.ResponseTypes.Code,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };
        foreach (var name in scopes)
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + name);
        await CreateApplicationAsync(descriptor);
    }

    private async Task CreateApplicationAsync(OpenIddictApplicationDescriptor descriptor)
    {
        if (descriptor.Permissions.Contains(Permissions.GrantTypes.ClientCredentials))
            _applications[descriptor.ClientId!] = true;
        await using var scope = Auth.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(descriptor);
    }

    /// <summary>Gets an access token for a seeded client by client credentials at <c>auth</c>.</summary>
    public async Task<AccessToken> RequestTokenAsync(string clientId, params string[] scopes)
    {
        using var client = Auth.CreateClient();
        var response = await client.PostAsync("connect/token", ClientCredentials(clientId, scopes));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AccessToken>())!;
    }

    public static FormUrlEncodedContent ClientCredentials(string clientId, params string[] scopes)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.ClientCredentials,
            ["client_id"] = clientId,
            ["client_secret"] = ClientSecret,
        };
        if (scopes.Length > 0)
            form["scope"] = string.Join(' ', scopes);
        return new FormUrlEncodedContent(form);
    }

    /// <summary>
    /// A person with a user record at a new tenant holding one <paramref name="role"/> assignment there, ready to sign
    /// in: a session needs a role assignment to survive its first refresh (#119).
    /// </summary>
    public async Task<AssignedPerson> AssignedPersonAsync(EntraUser? user = null, string role = "Viewer")
    {
        user ??= EntraUser.WithMfa();
        // A token from now: the default client's may have expired on the fixture's clock.
        using var client = await CreateClientAsync(GrantableScopes);
        var tenant = await CreateTenantAsync(client, $"Org{Interlocked.Increment(ref _clients)}");
        var created = await client.PostAsJsonAsync("api/v1/users", new OSI.Signal9.Contracts.Users.CreateUserRequest
        {
            TenantId = tenant.Id,
            EntraTenantId = user.TenantId,
            EntraObjectId = user.ObjectId,
            DisplayName = "Alex Wilber",
        }, Signal9Json.Options);
        created.EnsureSuccessStatusCode();
        var userId = (await created.Content.ReadFromJsonAsync<OSI.Signal9.Contracts.Users.UserDto>(Signal9Json.Options))!.Id;
        var roleId = OSI.Signal9.Server.Identity.BuiltInRoles.All.Single(r => r.Name == role).Id;
        var assigned = await client.PostAsJsonAsync($"api/v1/tenants/{tenant.Id}/role-assignments",
            new OSI.Signal9.Contracts.Roles.CreateRoleAssignmentRequest { UserId = userId, RoleId = roleId }, Signal9Json.Options);
        assigned.EnsureSuccessStatusCode();
        return new AssignedPerson(user, userId, tenant.Id, assigned.Headers.Location!);
    }

    public async Task<TenantDto> CreateTenantAsync(HttpClient client, string name, int maxDevices = 25, Guid? parentTenantId = null)
    {
        var response = await client.PostAsJsonAsync("api/v1/tenants",
            new CreateTenantRequest
            {
                Name = name,
                ContactEmail = $"it@{name.ToLowerInvariant()}.example",
                MaxDevices = maxDevices,
                ParentTenantId = parentTenantId,
            },
            Signal9Json.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options))!;
    }

    /// <summary>
    /// Enrolls a new agent into <paramref name="tenantId"/> as an installer would: the fixture's client issues an
    /// enrollment token, and the agent enrolls with it and a new self-signed certificate. The returned agent's
    /// <see cref="TestAgent.Client"/> calls api as the agent, with a certificate-bound token from api's mTLS alias.
    /// </summary>
    /// <param name="hardware">Shapes the enrollment request, such as to report hardware identifiers.</param>
    public async Task<TestAgent> EnrollAgentAsync(Guid tenantId, string machineName = "PC01",
        Func<EnrollAgentRequest, EnrollAgentRequest>? hardware = null)
    {
        var token = await IssueEnrollmentTokenAsync(tenantId);
        var certificate = TestCertificates.AgentCertificate();
        var response = await EnrollAsync(token.Secret!, certificate, machineName, hardware: hardware);
        if (response.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException($"Enrollment failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var agent = (await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options))!;
        return new TestAgent(agent, certificate, await CreateAgentClientAsync(agent.Id, certificate));
    }

    /// <summary>Issues an enrollment token for <paramref name="tenantId"/> with the fixture's client.</summary>
    public async Task<EnrollmentTokenDto> IssueEnrollmentTokenAsync(Guid tenantId, int? maxUses = null,
        DateTimeOffset? expiresAt = null)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync($"api/v1/tenants/{tenantId}/enrollment-tokens",
            new CreateEnrollmentTokenRequest { Name = "Installer", MaxUses = maxUses, ExpiresAt = expiresAt },
            Signal9Json.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<EnrollmentTokenDto>(Signal9Json.Options))!;
    }

    /// <summary>
    /// An agent's enrollment request (POST /api/v1/agents), sent with no access token, from <paramref name="from"/> if
    /// given.
    /// </summary>
    public async Task<HttpResponseMessage> EnrollAsync(string enrollmentToken, X509Certificate2 certificate,
        string machineName = "PC01", IPAddress? from = null, Func<EnrollAgentRequest, EnrollAgentRequest>? hardware = null)
    {
        using var client = CreateAnonymousClient();
        if (from is not null)
            client.DefaultRequestHeaders.Add(ClientIpHeader, from.ToString());
        var request = Enrollment(enrollmentToken, certificate, machineName);
        return await client.PostAsJsonAsync("api/v1/agents", hardware is null ? request : hardware(request),
            Signal9Json.Options);
    }

    public static EnrollAgentRequest Enrollment(string enrollmentToken, X509Certificate2 certificate,
        string machineName = "PC01") => new()
        {
            EnrollmentToken = enrollmentToken,
            Certificate = certificate.ExportCertificatePem(),
            MachineName = machineName,
            OperatingSystem = "Windows 11 Pro",
            ProcessorCores = 8,
        };

    /// <summary>The agent's token request at api's mTLS alias: client credentials, authenticated by its certificate.</summary>
    public async Task<HttpResponseMessage> RequestAgentTokenAsync(Guid agentId, X509Certificate2 certificate,
        string scope = OSI.Signal9.Server.Identity.Scopes.Agent)
    {
        using var api = CreateForwardedCertificateClient(certificate);
        return await api.PostAsync("connect/token", MtlsClientCredentials(agentId.ToString(), scope));
    }

    /// <summary>A client for <paramref name="server"/> acting as <paramref name="agent"/>, with its certificate and token.</summary>
    public static HttpClient AsAgent(TestServer server, TestAgent agent)
    {
        var client = CreatePeerClient(server, TrustedProxy, null);
        client.DefaultRequestHeaders.Add(ClientCertificateHeader, TestCertificates.ContainerAppsHeader(agent.Certificate));
        client.DefaultRequestHeaders.Authorization = agent.Client.DefaultRequestHeaders.Authorization;
        return client;
    }

    /// <summary>A client for <c>api</c> acting as the agent: it presents the certificate and a token bound to it.</summary>
    public async Task<HttpClient> CreateAgentClientAsync(Guid agentId, X509Certificate2 certificate)
    {
        var response = await RequestAgentTokenAsync(agentId, certificate);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Agent token refused: {await response.Content.ReadAsStringAsync()}");
        var token = (await response.Content.ReadFromJsonAsync<AccessToken>())!;
        var client = CreateForwardedCertificateClient(certificate);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    // Dispose() goes through DisposeAsync() too.
    public override async ValueTask DisposeAsync()
    {
        foreach (var replica in _webReplicas)
            await replica.DisposeAsync();
        foreach (var replica in _apiReplicas)
            await replica.DisposeAsync();
        if (_web is not null)
            await _web.DisposeAsync();
        if (_auth is not null)
            await _auth.DisposeAsync();
        await base.DisposeAsync();
        await PostgresContainer.DropDatabaseAsync(ConnectionString);
    }
}

/// <summary>Sets the connection's remote IP from <see cref="Signal9ApiFactory.ClientIpHeader"/>, ahead of the host's pipeline.</summary>
internal sealed class ClientIpFromHeader : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (IPAddress.TryParse(context.Request.Headers[Signal9ApiFactory.ClientIpHeader], out var address))
                context.Connection.RemoteIpAddress = address;
            return nextMiddleware(context);
        });
        next(app);
    };
}

/// <summary>A token endpoint's successful response.</summary>
public sealed record AccessToken(
    [property: JsonPropertyName("access_token")] string Value,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>An agent the fixture enrolled, with its certificate and a client calling <c>api</c> as it.</summary>
public sealed class TestAgent(AgentDto agent, X509Certificate2 certificate, HttpClient client) : IDisposable
{
    public AgentDto Agent { get; } = agent;

    public Guid Id => Agent.Id;

    /// <summary>Its device; it has none while it awaits an identification decision.</summary>
    public Guid DeviceId => Agent.DeviceId ?? throw new InvalidOperationException("The agent awaits identification.");

    public Guid TenantId => Agent.TenantId;

    public X509Certificate2 Certificate { get; } = certificate;

    /// <summary>Calls <c>api</c> as the agent, with its certificate and a certificate-bound access token.</summary>
    public HttpClient Client { get; } = client;

    public void Dispose()
    {
        Client.Dispose();
        Certificate.Dispose();
    }
}

/// <summary>A person with a user record and one role assignment, at <see cref="Assignment"/>.</summary>
public sealed record AssignedPerson(EntraUser User, Guid UserId, Guid TenantId, Uri Assignment);
