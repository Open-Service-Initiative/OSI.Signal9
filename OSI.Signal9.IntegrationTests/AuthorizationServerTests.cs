using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Quartz;
using OpenIddict.Validation;
using Quartz;

namespace OSI.Signal9.IntegrationTests;

/// <summary>The OpenIddict authorization server, split across <c>auth</c> and <c>api</c> (ADR-0002, ADR-0003).</summary>
[TestClass]
public sealed class AuthorizationServerTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public AuthorizationServerTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Client_credentials_token_from_auth_is_accepted_by_api()
    {
        await _factory.SeedClientAsync("reporting-app", "tenants.read");
        var token = await _factory.RequestTokenAsync("reporting-app", "tenants.read");

        using var api = _factory.CreateAnonymousClient();
        var anonymous = await api.GetAsync("api/v1/tenants", Ct);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        var authenticated = await api.GetAsync("api/v1/tenants", Ct);

        Assert.AreEqual("Bearer", token.TokenType);
        Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, authenticated.StatusCode);
    }

    [TestMethod]
    [DataRow("password")]
    [DataRow("urn:ietf:params:oauth:grant-type:device_code")]
    public async Task Legacy_grants_are_refused_at_auth_and_api(string grantType)
    {
        await _factory.SeedClientAsync("legacy-app");
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = grantType,
            ["client_id"] = "legacy-app",
            ["client_secret"] = Signal9ApiFactory.ClientSecret,
            ["username"] = "alice",
            ["password"] = "hunter2",
            ["device_code"] = "abc",
        };

        foreach (var host in new[] { _factory.Auth.CreateClient(), _factory.CreateAnonymousClient() })
        {
            using var _ = host;
            var response = await host.PostAsync("connect/token", new FormUrlEncodedContent(form), Ct);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.AreEqual("unsupported_grant_type", error.GetProperty("error").GetString());
        }
    }

    [TestMethod]
    public async Task Discovery_offers_no_password_device_code_or_implicit_flow()
    {
        using var auth = _factory.Auth.CreateClient();

        var discovery = await auth.GetFromJsonAsync<JsonElement>(".well-known/openid-configuration", Ct);

        var grants = discovery.GetProperty("grant_types_supported").EnumerateArray().Select(g => g.GetString()).ToList();
        CollectionAssert.DoesNotContain(grants, "password");
        CollectionAssert.DoesNotContain(grants, "implicit");
        CollectionAssert.DoesNotContain(grants, "urn:ietf:params:oauth:grant-type:device_code");
        Assert.IsFalse(discovery.TryGetProperty("device_authorization_endpoint", out _));
        if (discovery.TryGetProperty("response_types_supported", out var responseTypes))
            Assert.IsFalse(responseTypes.EnumerateArray().Any(t => t.GetString()!.Split(' ').Contains("token")));
    }

    [TestMethod]
    public async Task Discovery_on_auth_advertises_the_mtls_aliases_on_api()
    {
        using var auth = _factory.Auth.CreateClient();
        using var api = _factory.CreateAnonymousClient();

        var discovery = await auth.GetFromJsonAsync<JsonElement>(".well-known/openid-configuration", Ct);
        var apiDiscovery = await api.GetAsync(".well-known/openid-configuration", Ct);
        var apiKeys = await api.GetAsync(".well-known/jwks", Ct);

        Assert.AreEqual(Signal9AuthFactory.Issuer, discovery.GetProperty("issuer").GetString());
        var aliases = discovery.GetProperty("mtls_endpoint_aliases");
        Assert.AreEqual("https://api.signal9.test/connect/token", aliases.GetProperty("token_endpoint").GetString());
        Assert.AreEqual("https://api.signal9.test/connect/revoke", aliases.GetProperty("revocation_endpoint").GetString());
        Assert.AreEqual("https://api.signal9.test/connect/introspect", aliases.GetProperty("introspection_endpoint").GetString());
        Assert.AreEqual(HttpStatusCode.Unauthorized, apiDiscovery.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, apiKeys.StatusCode);
    }

    [TestMethod]
    public async Task Access_tokens_last_10_minutes()
    {
        await _factory.SeedClientAsync("reporting-app", "tenants.read");
        var token = await _factory.RequestTokenAsync("reporting-app", "tenants.read");
        using var api = CreateApiClient(token);

        _factory.Time.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
        var beforeExpiry = await api.GetAsync("api/v1/tenants", Ct);
        _factory.Time.Advance(TimeSpan.FromSeconds(2));
        var afterExpiry = await api.GetAsync("api/v1/tenants", Ct);

        Assert.AreEqual(600, token.ExpiresIn);
        Assert.AreEqual(HttpStatusCode.OK, beforeExpiry.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, afterExpiry.StatusCode);
    }

    [TestMethod]
    public async Task Access_tokens_carry_identity_only_for_the_api_audience()
    {
        await _factory.SeedClientAsync("reporting-app", "tenants.read");
        var token = await _factory.RequestTokenAsync("reporting-app", "tenants.read");

        // Only the token's audience sees all of its claims, so introspect as the api resource server.
        await _factory.SeedClientAsync("signal9-api");
        var claims = await IntrospectAsync("signal9-api", token);

        Assert.IsTrue(claims.GetProperty("active").GetBoolean());
        Assert.AreEqual("reporting-app", claims.GetProperty("sub").GetString());
        Assert.AreEqual("reporting-app", claims.GetProperty("client_id").GetString());
        Assert.AreEqual("tenants.read", claims.GetProperty("scope").GetString());
        Assert.AreEqual("signal9-api", claims.GetProperty("aud").GetString());
        Assert.AreEqual(Signal9AuthFactory.Issuer, claims.GetProperty("iss").GetString());
        // Only what identifies the caller and the token itself: no roles, tenants or other access claims (ADR-0007).
        string[] allowed = ["active", "sub", "client_id", "scope", "aud", "iss", "exp", "iat", "nbf", "jti", "token_type", "token_usage"];
        var unexpected = claims.EnumerateObject().Select(p => p.Name).Except(allowed).ToList();
        Assert.IsEmpty(unexpected, string.Join(", ", unexpected));
    }

    [TestMethod]
    public async Task Api_refuses_a_revoked_token_on_the_next_request()
    {
        await _factory.SeedClientAsync("reporting-app", "tenants.read");
        var token = await _factory.RequestTokenAsync("reporting-app", "tenants.read");
        using var api = CreateApiClient(token);
        var beforeRevocation = await api.GetAsync("api/v1/tenants", Ct);

        using var revoker = _factory.CreateAnonymousClient();
        var revocation = await revoker.PostAsync("connect/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "reporting-app",
            ["client_secret"] = Signal9ApiFactory.ClientSecret,
            ["token"] = token.Value,
        }), Ct);
        var afterRevocation = await api.GetAsync("api/v1/tenants", Ct);

        Assert.AreEqual(HttpStatusCode.OK, beforeRevocation.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, revocation.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, afterRevocation.StatusCode);
    }

    [TestMethod]
    public void Api_checks_token_and_authorization_entries_in_the_database()
    {
        var validation = _factory.Services.GetRequiredService<IOptionsMonitor<OpenIddictValidationOptions>>().CurrentValue;

        Assert.IsTrue(validation.EnableTokenEntryValidation);
        Assert.IsTrue(validation.EnableAuthorizationEntryValidation);
        Assert.AreEqual(OpenIddictValidationType.Direct, validation.ValidationType);
    }

    [TestMethod]
    public async Task Quartz_prunes_expired_tokens_and_revoked_authorizations_hourly()
    {
        // OpenIddict's pruning job judges age on the real clock, so these entries are made a month ago.
        await using var factory = new Signal9ApiFactory(start: DateTimeOffset.UtcNow.AddDays(-30));
        await factory.SeedClientAsync("reporting-app");
        await factory.RequestTokenAsync("reporting-app");
        await using var services = factory.Auth.Services.CreateAsyncScope();
        var tokens = services.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var authorizations = services.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        await authorizations.CreateAsync(new OpenIddictAuthorizationDescriptor
        {
            Subject = "someone",
            Status = OpenIddictConstants.Statuses.Revoked,
            Type = OpenIddictConstants.AuthorizationTypes.AdHoc,
            CreationDate = factory.Time.GetUtcNow(),
        }, Ct);
        // OpenIddict keeps entries for at least 14 days before pruning them.
        factory.Time.Advance(TimeSpan.FromDays(15));
        var scheduler = await factory.Auth.Services.GetRequiredService<ISchedulerFactory>().GetScheduler(Ct);

        var trigger = Assert.ContainsSingle(await scheduler.GetTriggersOfJob(OpenIddictQuartzJob.Identity, Ct));
        Assert.AreEqual(TimeSpan.FromHours(1), Assert.IsInstanceOfType<ISimpleTrigger>(trigger).RepeatInterval);
        Assert.AreEqual(1, await tokens.CountAsync(Ct));
        Assert.AreEqual(1, await authorizations.CountAsync(Ct));

        var pruning = new JobRun();
        scheduler.ListenerManager.AddJobListener(pruning);
        await scheduler.Start(Ct);
        await scheduler.TriggerJob(OpenIddictQuartzJob.Identity, data: null, Ct);
        await pruning.Finished.Task.SignalledAsync("the pruning job's run");
        await scheduler.Shutdown(waitForJobsToComplete: true, Ct);

        Assert.AreEqual(0, await tokens.CountAsync(Ct));
        Assert.AreEqual(0, await authorizations.CountAsync(Ct));
    }

    /// <summary>Quartz's word that OpenIddict's pruning job has run, or why it failed.</summary>
    private sealed class JobRun : IJobListener
    {
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => nameof(JobRun);

        public ValueTask JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException,
            CancellationToken cancellationToken = default)
        {
            if (context.JobDetail.Key.Equals(OpenIddictQuartzJob.Identity))
            {
                if (jobException is null)
                    Finished.TrySetResult();
                else
                    Finished.TrySetException(jobException);
            }
            return ValueTask.CompletedTask;
        }
    }

    private HttpClient CreateApiClient(AccessToken token)
    {
        var api = _factory.CreateAnonymousClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return api;
    }

    /// <summary>Introspects a token at api's alias.</summary>
    private async Task<JsonElement> IntrospectAsync(string clientId, AccessToken token)
    {
        using var api = _factory.CreateAnonymousClient();
        var response = await api.PostAsync("connect/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = Signal9ApiFactory.ClientSecret,
            ["token"] = token.Value,
        }), Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    public void Dispose() => _factory.Dispose();
}
