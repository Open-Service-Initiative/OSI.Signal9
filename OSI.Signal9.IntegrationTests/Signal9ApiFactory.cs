using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OSI.Signal9.API;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.ApiApplications;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Server.Data;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Hosts <c>api</c> and <c>auth</c> in memory against their own database on the shared PostgreSQL container, with
/// one controllable clock. Every caller is a real one, authorized as in production: a person signed in at the fake Entra
/// through <c>auth</c>, or an API application registered through the API, holding role assignments made through the API.
/// Pick the caller a test needs:
/// <list type="bullet">
/// <item><see cref="CreateAnonymousClient"/> (and <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/>): no
/// access token.</item>
/// <item><see cref="CreateSuperadministratorClient"/>: the deployment's Superadministrator, for what only one can do,
/// such as creating root tenants or working across tenant trees.</item>
/// <item><see cref="AssignedPersonAsync"/>, <see cref="CreateAssignedClientAsync"/>: people holding role
/// assignments.</item>
/// <item><see cref="CreateApplicationClientAsync"/>: an app-only API application at a tenant.</item>
/// <item><see cref="CreatePersonClientAsync"/>: any <see cref="EntraUser"/>, with what Signal9 knows of them.</item>
/// </list>
/// </summary>
public sealed class Signal9ApiFactory : WebApplicationFactory<ApiHost>
{
    private readonly IReadOnlyDictionary<string, string?> _settings;

    /// <param name="migrated">Whether the database starts with the schema applied, or empty.</param>
    /// <param name="settings">Configuration for both hosts, on top of the fixture's own.</param>
    /// <param name="start">When the shared clock starts, if not now.</param>
    public Signal9ApiFactory(bool migrated = true, IReadOnlyDictionary<string, string?>? settings = null,
        DateTimeOffset? start = null)
    {
        Time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds((start ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()));
        Entra = new FakeEntra(Time);
        _settings = settings ?? new Dictionary<string, string?>();
        ConnectionString = PostgresContainer.CreateDatabase(migrated);
        ClientOptions.BaseAddress = HttpsLocalhost;
    }

    private Signal9AuthFactory? _auth;
    private Signal9WebFactory? _web;
    private readonly List<Signal9WebFactory> _webReplicas = [];

    /// <summary>
    /// The clock every host shares. It starts at the real time unless a test says otherwise, in whole seconds as
    /// certificates hold it, because X.509 certificates made with the real clock (the tests', and Entra's tokens) must
    /// be valid on it too.
    /// </summary>
    public FakeTimeProvider Time { get; }

    /// <summary>Every host's background job runs, as their traces show them.</summary>
    public BackgroundJobRuns Jobs { get; } = new();

    /// <summary>
    /// What <c>api</c> logged, from Information up for the categories the configuration lets through (warnings and
    /// worse by default), with each exception's message.
    /// </summary>
    public ConcurrentQueue<string> Logs { get; } = new();

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
        builder.ConfigureTestServices(services => services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(
            new CapturingLoggerProvider(Logs.Enqueue, Microsoft.Extensions.Logging.LogLevel.Information)));
    }

    // ── Superadministrator ──────────────────────────────────────────────────

    private EntraUser? _superadministrator;

    /// <summary>
    /// The deployment's Superadministrator, whom <see cref="CreateSuperadministratorClient"/> signs in: a person in the
    /// deploying directory holding Entra's Superadministrator app role (ADR-0006).
    /// </summary>
    public EntraUser FixtureSuperadministrator => _superadministrator ??= Superadministrator() with { SessionId = $"sid-{Guid.NewGuid():N}" };

    /// <summary>
    /// A client for <c>api</c> (or <paramref name="host"/>, another api host on this database) acting as
    /// <see cref="FixtureSuperadministrator"/>, who signs in at Entra through <c>auth</c> with MFA and every person scope
    /// when first used, and again whenever the last access token has expired on the fixture's clock, so it stays
    /// usable however far a test moves the clock. For what only a Superadministrator can do, such as creating root
    /// tenants or working across tenant trees.
    /// </summary>
    public HttpClient CreateSuperadministratorClient(WebApplicationFactory<ApiHost>? host = null) =>
        new(new SignedInHandler(this, (host ?? this).Server.CreateHandler())) { BaseAddress = HttpsLocalhost };

    private readonly SemaphoreSlim _superadministratorSigningIn = new(1, 1);
    private (string Value, DateTimeOffset ExpiresAt)? _superadministratorToken;

    /// <summary>An access token for <see cref="FixtureSuperadministrator"/>, signing in again once the last one expires.</summary>
    public async Task<string> SuperadministratorTokenAsync()
    {
        await _superadministratorSigningIn.WaitAsync();
        try
        {
            var now = Time.GetUtcNow();
            if (_superadministratorToken is not { } token || token.ExpiresAt - now < TimeSpan.FromMinutes(1))
                _superadministratorToken = token = (await PersonTokenAsync(FixtureSuperadministrator),
                    now + OSI.Signal9.Server.Identity.AuthorizationServer.AccessTokenLifetime);
            return token.Value;
        }
        finally
        {
            _superadministratorSigningIn.Release();
        }
    }

    /// <summary>Sends each request with the Superadministrator's current access token, unless it names its own.</summary>
    private sealed class SignedInHandler(Signal9ApiFactory factory, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization ??= new AuthenticationHeaderValue("Bearer", await factory.SuperadministratorTokenAsync());
            return await base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>A Superadministrator who just signed in with MFA, as sensitive actions need (#120).</summary>
    public EntraUser Superadministrator() =>
        EntraUser.WithMfa() with { TenantId = Entra.DeployingTenantId, Roles = [OSI.Signal9.Server.Identity.Superadministrator.AppRole] };

    // ── People ──────────────────────────────────────────────────────────────

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

    /// <summary>
    /// An access token for <paramref name="user"/>, who signs in at Entra through <c>auth</c> with a client using
    /// authorization code, asking for these scopes (every person scope if none). The client stands in for a
    /// first-party one, like the dashboard's: it never asks for consent and doesn't narrow what the person can do.
    /// </summary>
    public async Task<string> PersonTokenAsync(EntraUser user, params string[] scopes) =>
        (await PersonTokensAsync(user, scopes)).AccessToken;

    /// <summary>
    /// What <c>auth</c>'s token endpoint answers when <paramref name="user"/> signs in as for
    /// <see cref="PersonTokenAsync"/>: an access token, an ID token, and a refresh token if the scopes include
    /// <c>offline_access</c>.
    /// </summary>
    public async Task<PersonTokens> PersonTokensAsync(EntraUser user, params string[] scopes)
    {
        await _personClientSeeding.WaitAsync();
        try
        {
            if (!_personClientSeeded)
                await SeedCodeClientAsync(PersonClientId, [.. PersonScopes, OpenIddictConstants.Scopes.OfflineAccess]);
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
        await EnsureAsync(response, HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<PersonTokens>())!;
    }

    /// <summary>Redeems a refresh token from <see cref="PersonTokensAsync"/> at <c>auth</c>.</summary>
    public async Task<HttpResponseMessage> RefreshPersonTokensAsync(string refreshToken)
    {
        using var auth = Auth.CreateClient();
        return await auth.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.RefreshToken,
            ["refresh_token"] = refreshToken,
            ["client_id"] = PersonClientId,
            ["client_secret"] = ClientSecret,
        }));
    }

    /// <summary>
    /// A person holding the Administrator role at exactly <paramref name="assignedTenants"/> (none: someone Signal9
    /// doesn't know, who sees nothing), assigned through the API by the Superadministrator, signed in with these scopes:
    /// for tests of what an assignment reaches.
    /// </summary>
    public async Task<HttpClient> CreateAssignedClientAsync(Guid[] assignedTenants, params string[] scopes)
    {
        var person = EntraUser.WithMfa();
        if (assignedTenants.Length > 0)
            await RegisterPersonAsync(person, assignedTenants[0], [.. assignedTenants.Select(t => ("Administrator", t))]);
        string[] personScopes = [.. scopes.Where(PersonScopes.Contains)];
        if (scopes.Length > 0 && personScopes.Length == 0)
            throw new ArgumentException("No person can hold these scopes.", nameof(scopes));
        return await CreatePersonClientAsync(person, personScopes);
    }

    /// <summary>
    /// A person with a user record owned by a new root tenant, holding one <paramref name="role"/> assignment there,
    /// made through the API by the Superadministrator, ready to sign in: a session needs a role assignment to survive
    /// its first refresh (#119).
    /// </summary>
    public async Task<AssignedPerson> AssignedPersonAsync(EntraUser? user = null, string role = "Viewer")
    {
        user ??= EntraUser.WithMfa();
        using var superadministrator = CreateSuperadministratorClient();
        var tenant = await CreateTenantAsync(superadministrator, $"Org{Interlocked.Increment(ref _clients)}");
        var (userId, assignments) = await RegisterPersonAsync(user, tenant.Id, (role, tenant.Id));
        return new AssignedPerson(user, userId, tenant.Id, assignments.Single());
    }

    /// <summary>
    /// Registers <paramref name="person"/> as a user owned by <paramref name="owner"/> holding these role assignments,
    /// through the API as the Superadministrator. Returns the user's ID and each assignment's URL.
    /// </summary>
    public async Task<(Guid UserId, Uri[] Assignments)> RegisterPersonAsync(EntraUser person, Guid owner,
        params (string Role, Guid Tenant)[] assignments)
    {
        using var superadministrator = CreateSuperadministratorClient();
        var created = await superadministrator.PostAsJsonAsync("api/v1/users", new OSI.Signal9.Contracts.Users.CreateUserRequest
        {
            TenantId = owner,
            EntraTenantId = person.TenantId,
            EntraObjectId = person.ObjectId,
            DisplayName = "Alex Wilber",
        }, Signal9Json.Options);
        await EnsureAsync(created, HttpStatusCode.Created);
        var userId = (await created.Content.ReadFromJsonAsync<OSI.Signal9.Contracts.Users.UserDto>(Signal9Json.Options))!.Id;
        var urls = new List<Uri>();
        foreach (var (role, tenant) in assignments)
        {
            var assigned = await superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenant}/role-assignments",
                new OSI.Signal9.Contracts.Roles.CreateRoleAssignmentRequest { UserId = userId, RoleId = RoleId(role) },
                Signal9Json.Options);
            await EnsureAsync(assigned, HttpStatusCode.Created);
            urls.Add(assigned.Headers.Location!);
        }
        return (userId, [.. urls]);
    }

    /// <summary>A built-in role's ID, by name.</summary>
    public static Guid RoleId(string role) => OSI.Signal9.Server.Identity.BuiltInRoles.All.Single(r => r.Name == role).Id;

    // ── API applications ────────────────────────────────────────────────────

    /// <summary>
    /// A client for <c>api</c> acting as a new app-only API application registered at <paramref name="tenantId"/>
    /// through the API by the Superadministrator (story 78): it holds exactly these scopes, with the narrowest built-in
    /// role that grants them, and authenticates with <c>private_key_jwt</c>, so it may hold any scope an application can.
    /// </summary>
    public async Task<HttpClient> CreateApplicationClientAsync(Guid tenantId, params string[] scopes)
    {
        var application = await RegisterApplicationAsync(tenantId, scopes);
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            (await application.RequestTokenAsync(scopes)).Value);
        return client;
    }

    /// <summary>
    /// Registers an app-only API application at <paramref name="tenantId"/> as <see cref="CreateApplicationClientAsync"/>
    /// does, with a public key credential whose private key the returned application signs its client assertions with.
    /// </summary>
    public async Task<TestApplication> RegisterApplicationAsync(Guid tenantId, params string[] scopes)
    {
        var role = OSI.Signal9.Server.Identity.BuiltInRoles.All.FirstOrDefault(r => scopes.All(r.Scopes.Contains))
            ?? OSI.Signal9.Server.Identity.BuiltInRoles.Administrator;
        using var superadministrator = CreateSuperadministratorClient();
        var registered = await superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenantId}/api-applications",
            new CreateApiApplicationRequest
            {
                DisplayName = $"Application {Interlocked.Increment(ref _clients)}",
                RoleId = role.Id,
                Scopes = scopes,
                TokenEndpointAuthMethod = TokenEndpointAuthMethods.PrivateKeyJwt,
            }, Signal9Json.Options);
        await EnsureAsync(registered, HttpStatusCode.Created);
        var application = (await registered.Content.ReadFromJsonAsync<ApiApplicationDto>(Signal9Json.Options))!;
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credential = await superadministrator.PostAsJsonAsync(
            $"api/v1/tenants/{tenantId}/api-applications/{application.Id}/credentials",
            new CreateApiApplicationCredentialRequest { PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() }, Signal9Json.Options);
        await EnsureAsync(credential, HttpStatusCode.Created);
        return new TestApplication(this, application, key);
    }

    /// <summary>
    /// A client for <c>api</c> with a token for a client registered at <c>auth</c> alone, not as an API application,
    /// holding exactly these scopes: <c>api</c> grants it no tenant. For what a token's scopes alone mustn't reach, such
    /// as the <c>agent</c> scope without an agent.
    /// </summary>
    public async Task<HttpClient> CreateUnregisteredClientAsync(params string[] scopes)
    {
        var clientId = $"unregistered-client-{Interlocked.Increment(ref _clients)}";
        await SeedClientAsync(clientId, scopes);
        var client = CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await RequestTokenAsync(clientId, scopes)).Value);
        return client;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            throw new InvalidOperationException(
                $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
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
        // The AppHost sets it in every mode; tests that need Azure SignalR say so.
        builder.UseSetting("Realtime:Hosting", "SelfHosted");
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
        // Tests request tokens and enroll agents far faster than anyone would; a test that sets DefaultRateLimits keeps
        // the production defaults.
        if (!settings.ContainsKey(DefaultRateLimits))
        {
            foreach (var policy in OSI.Signal9.Server.Hosting.RateLimiting.Defaults.Keys)
                builder.UseSetting($"RateLimiting:{policy}:PermitLimit", string.Empty);
        }
        foreach (var (key, value) in settings)
            builder.UseSetting(key, value);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, ClientIpFromHeader>();
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(time);
        });
    }

    /// <summary>A setting that keeps the default rate limits, which the fixture otherwise turns off.</summary>
    public const string DefaultRateLimits = "Testing:DefaultRateLimits";

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

    /// <summary>
    /// Every catalogue scope a client can obtain with client credentials: the OpenID Connect scopes need a person.
    /// </summary>
    public static readonly string[] GrantableScopes = OSI.Signal9.Server.Identity.Scopes.All.Keys
        .Except([OpenIddictConstants.Scopes.OpenId, OpenIddictConstants.Scopes.Profile, OpenIddictConstants.Scopes.OfflineAccess])
        .ToArray();

    private int _clients;

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
    /// revocation and introspection, and may request the given scopes. Only scopes in the catalogue can be issued. It's
    /// registered at <c>auth</c> alone, not as an API application, so <c>api</c> grants it no tenant: for tests of the
    /// authorization server itself.
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
        // offline_access is a scope people grant, so it's also what lets the client refresh.
        if (scopes.Contains(OpenIddictConstants.Scopes.OfflineAccess))
            descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
        await CreateApplicationAsync(descriptor);
    }

    private async Task CreateApplicationAsync(OpenIddictApplicationDescriptor descriptor)
    {
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
    /// Enrolls a new agent into <paramref name="tenantId"/> as an installer would: the Superadministrator issues an
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

    /// <summary>Issues an enrollment token for <paramref name="tenantId"/> as the Superadministrator.</summary>
    public async Task<EnrollmentTokenDto> IssueEnrollmentTokenAsync(Guid tenantId, int? maxUses = null,
        DateTimeOffset? expiresAt = null)
    {
        using var client = CreateSuperadministratorClient();
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
    /// <param name="version">The agent version it sends (<see cref="AgentProtocol.VersionHeader"/>); this build's by default.</param>
    public async Task<HttpResponseMessage> EnrollAsync(string enrollmentToken, X509Certificate2 certificate,
        string machineName = "PC01", IPAddress? from = null, Func<EnrollAgentRequest, EnrollAgentRequest>? hardware = null,
        string? version = null)
    {
        using var client = CreateAnonymousClient();
        if (from is not null)
            client.DefaultRequestHeaders.Add(ClientIpHeader, from.ToString());
        client.DefaultRequestHeaders.Add(AgentProtocol.VersionHeader, version ?? AgentVersion);
        var request = Enrollment(enrollmentToken, certificate, machineName);
        return await client.PostAsJsonAsync(AgentProtocol.Routes.Enroll, hardware is null ? request : hardware(request),
            Signal9Json.Options);
    }

    /// <summary>The version the real agent sends, which the deployment supports by default: both come from one build.</summary>
    public static string AgentVersion { get; } = OSI.Signal9.Agent.Services.AgentVersion.Current.ToString();

    public static EnrollAgentRequest Enrollment(string enrollmentToken, X509Certificate2 certificate,
        string machineName = "PC01") => new()
        {
            EnrollmentToken = enrollmentToken,
            Certificate = certificate.ExportCertificatePem(),
            MachineName = machineName,
            OsFamily = Contracts.Devices.OsFamily.Windows,
            OperatingSystem = "Windows 11 Pro",
            ProcessorCores = 8,
        };

    /// <summary>The agent's token request at api's mTLS alias: client credentials, authenticated by its certificate.</summary>
    public async Task<HttpResponseMessage> RequestAgentTokenAsync(Guid agentId, X509Certificate2 certificate,
        string scope = AgentProtocol.Scope, string? version = null)
    {
        using var api = CreateForwardedCertificateClient(certificate);
        api.DefaultRequestHeaders.Add(AgentProtocol.VersionHeader, version ?? AgentVersion);
        return await api.PostAsync(AgentProtocol.TokenEndpoint, MtlsClientCredentials(agentId.ToString(), scope));
    }

    /// <summary>A client for <paramref name="server"/> acting as <paramref name="agent"/>, with its certificate and token.</summary>
    public static HttpClient AsAgent(TestServer server, TestAgent agent)
    {
        var client = CreatePeerClient(server, TrustedProxy, null);
        client.DefaultRequestHeaders.Add(ClientCertificateHeader, TestCertificates.ContainerAppsHeader(agent.Certificate));
        client.DefaultRequestHeaders.Authorization = agent.Client.DefaultRequestHeaders.Authorization;
        client.DefaultRequestHeaders.Add(AgentProtocol.VersionHeader, AgentVersion);
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
        client.DefaultRequestHeaders.Add(AgentProtocol.VersionHeader, AgentVersion);
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
        Jobs.Dispose();
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

/// <summary>The token endpoint's answer to a person's sign-in.</summary>
public sealed record PersonTokens(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("id_token")] string IdToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken);

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

/// <summary>
/// An app-only API application the fixture registered through the API, with the private key of its
/// <c>private_key_jwt</c> credential.
/// </summary>
public sealed class TestApplication(Signal9ApiFactory factory, ApiApplicationDto application, ECDsa key)
{
    public ApiApplicationDto Application { get; } = application;

    public string ClientId => Application.ClientId;

    /// <summary>A client assertion (RFC 7523 §3) for <c>auth</c>'s token endpoint, on the fixture's clock.</summary>
    public string Assertion()
    {
        var now = factory.Time.GetUtcNow().UtcDateTime;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            // draft-ietf-oauth-rfc7523bis, which OpenIddict follows: an explicit type, and the issuer as the audience.
            TokenType = "client-authentication+jwt",
            Issuer = ClientId,
            Audience = Signal9AuthFactory.Issuer,
            Claims = new Dictionary<string, object> { ["sub"] = ClientId, ["jti"] = Guid.NewGuid().ToString("N") },
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(2),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256),
        });
    }

    /// <summary>The application's token request at <c>auth</c>: client credentials, with a client assertion.</summary>
    public async Task<HttpResponseMessage> TokenResponseAsync(params string[] scopes)
    {
        using var auth = factory.Auth.CreateClient();
        return await auth.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = GrantTypes.ClientCredentials,
            ["client_id"] = ClientId,
            ["client_assertion_type"] = ClientAssertionTypes.JwtBearer,
            ["client_assertion"] = Assertion(),
            ["scope"] = string.Join(' ', scopes),
        }));
    }

    /// <summary>An access token for the application, holding <paramref name="scopes"/>.</summary>
    public async Task<AccessToken> RequestTokenAsync(params string[] scopes)
    {
        var response = await TokenResponseAsync(scopes);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Token refused: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<AccessToken>())!;
    }
}
