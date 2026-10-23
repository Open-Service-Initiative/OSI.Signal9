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
using Npgsql;
using OpenIddict.Abstractions;
using OSI.Signal9.API;
using OSI.Signal9.Contracts.Agents;
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
    public Signal9ApiFactory(bool migrated = true, IReadOnlyDictionary<string, string?>? settings = null)
    {
        _migrated = migrated;
        _settings = settings ?? new Dictionary<string, string?>();
        ConnectionString = PostgresContainer.CreateDatabase(migrated);
        ClientOptions.BaseAddress = HttpsLocalhost;
    }

    private const string DefaultClientId = "test-client";

    private AccessToken? _defaultToken;
    private Signal9AuthFactory? _auth;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Connection string of this factory's database.</summary>
    public string ConnectionString { get; }

    /// <summary>The <c>auth</c> host, sharing this factory's database and clock.</summary>
    public Signal9AuthFactory Auth => _auth ??= new Signal9AuthFactory(ConnectionString, Time, settings: _settings);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ConfigureSharedHost(builder, ConnectionString, Time, _settings);
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpContextAccessor();
            services.RemoveAll<IAssignedTenants>();
            services.AddScoped<IAssignedTenants>(sp =>
                new FixtureAssignedTenants(sp.GetRequiredService<IHttpContextAccessor>(), this));
        });
    }

    private readonly ConcurrentDictionary<string, Guid[]> _assignments = new();

    /// <summary>
    /// A client for <c>api</c> standing in for a caller with role assignments at exactly <paramref name="assignedTenants"/>
    /// (none: a caller who sees nothing), with a token holding these scopes. Role assignments arrive with RBAC (#114).
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

    /// <summary>
    /// The fixture's assigned tenants: those given to <see cref="CreateAssignedClientAsync"/>'s client, and for every
    /// other caller (and work outside a request) every root tenant, standing in for a Superadministrator so tests can
    /// work across tenant trees.
    /// </summary>
    private sealed class FixtureAssignedTenants(IHttpContextAccessor http, Signal9ApiFactory factory) : IAssignedTenants
    {
        public IReadOnlyCollection<Guid> TenantIds =>
            http.HttpContext?.User.FindFirst(Claims.Subject)?.Value is { } clientId
            && factory._assignments.TryGetValue(clientId, out var assigned)
                ? assigned
                : factory.RootTenantIds();
    }

    private Guid[] RootTenantIds()
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand("""SELECT "Id" FROM "Tenants" WHERE "ParentTenantId" IS NULL""", connection);
        using var reader = command.ExecuteReader();
        List<Guid> ids = [];
        while (reader.Read())
            ids.Add(reader.GetGuid(0));
        return [.. ids];
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
        // Container Apps' ingress is the proxy in front of every host; api takes client certificates from it.
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
    public async Task SeedSelfSignedTlsClientAsync(string clientId, X509Certificate2 certificate, params string[] scopes)
    {
        var descriptor = MtlsClient(clientId, scopes);
        descriptor.JsonWebKeySet = new JsonWebKeySet
        {
            Keys = { JsonWebKeyConverter.ConvertFromX509SecurityKey(new X509SecurityKey(certificate)) },
        };
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

    public static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, Guid tenantId, string machineName) =>
        await client.PostAsJsonAsync("api/v1/agents",
            new RegisterAgentRequest { TenantId = tenantId, MachineName = machineName, OperatingSystem = "Windows 11 Pro", ProcessorCores = 8 },
            Signal9Json.Options);

    // Dispose() goes through DisposeAsync() too.
    public override async ValueTask DisposeAsync()
    {
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
