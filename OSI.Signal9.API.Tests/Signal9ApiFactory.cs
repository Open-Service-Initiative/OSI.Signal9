using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using OpenIddict.Abstractions;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.API.Tests;

/// <summary>
/// Hosts <c>api</c> and <c>auth</c> in memory against their own database on the shared PostgreSQL container, with
/// one controllable clock. Clients from <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> call <c>api</c>
/// with an access token for a seeded test client.
/// </summary>
public sealed class Signal9ApiFactory : WebApplicationFactory<ApiHost>
{
    private readonly bool _migrated;

    /// <param name="migrated">Whether the database starts with the schema applied, or empty.</param>
    public Signal9ApiFactory(bool migrated = true)
    {
        _migrated = migrated;
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
    public Signal9AuthFactory Auth => _auth ??= new Signal9AuthFactory(ConnectionString, Time);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ConfigureSharedHost(builder, ConnectionString, Time);
    }

    /// <summary>What <c>auth</c> and <c>api</c> share: the database, clock, issuer and keys.</summary>
    internal static void ConfigureSharedHost(IWebHostBuilder builder, string connectionString, FakeTimeProvider time)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:signal9", connectionString);
        builder.UseSetting("Identity:Issuer", Signal9AuthFactory.Issuer);
        builder.UseSetting("Identity:ApiUri", Signal9AuthFactory.ApiUri);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(time);
            services.AddOpenIddict().AddServer(server => server
                .AddSigningKey(TestKeys.Signing)
                .AddEncryptionKey(TestKeys.Encryption));
        });
    }

    /// <summary>The base address of every test client. OpenIddict accepts only HTTPS requests.</summary>
    public static readonly Uri HttpsLocalhost = new("https://localhost");

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        if (!_migrated)
            return;

        _defaultToken ??= Task.Run(async () =>
        {
            await SeedClientAsync(DefaultClientId);
            return await RequestTokenAsync(DefaultClientId);
        }).GetAwaiter().GetResult();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _defaultToken.Value);
    }

    /// <summary>A client for <c>api</c> that sends no access token.</summary>
    public HttpClient CreateAnonymousClient()
    {
        var client = Server.CreateClient();
        client.BaseAddress = HttpsLocalhost;
        return client;
    }

    /// <summary>The secret of every client seeded by <see cref="SeedClientAsync"/>.</summary>
    public const string ClientSecret = "test-secret-that-is-long-enough";

    /// <summary>
    /// Seeds a confidential client that authenticates with <see cref="ClientSecret"/> and may use client credentials,
    /// revocation and introspection, with the given scopes (each created if it doesn't exist yet).
    /// </summary>
    public async Task SeedClientAsync(string clientId, params string[] scopes)
    {
        await using var scope = Auth.Services.CreateAsyncScope();
        var scopeManager = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
        foreach (var name in scopes)
        {
            if (await scopeManager.FindByNameAsync(name) is null)
                await scopeManager.CreateAsync(new OpenIddictScopeDescriptor { Name = name });
        }

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

    public async Task<TenantDto> CreateTenantAsync(HttpClient client, string name, int maxDevices = 25)
    {
        var response = await client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Name = name, ContactEmail = $"it@{name.ToLowerInvariant()}.example", MaxDevices = maxDevices },
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
    }
}

/// <summary>A token endpoint's successful response.</summary>
public sealed record AccessToken(
    [property: JsonPropertyName("access_token")] string Value,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);
