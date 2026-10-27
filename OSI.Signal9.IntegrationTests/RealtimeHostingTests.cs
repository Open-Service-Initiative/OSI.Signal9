using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Server.Identity;
using Scopes = OSI.Signal9.Server.Identity.Scopes;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Real-time hosting chosen by configuration (ADR-0009, stories 116-117): Azure SignalR Service for both hubs, or the
/// hubs inside api, which a single replica runs with no Redis.
/// </summary>
[TestClass]
public sealed class RealtimeHostingTests
{
    // A well-formed connection string for a service that doesn't exist: api never reaches it in these tests.
    private const string AzureSignalR =
        "Endpoint=https://signal9-test.service.signalr.net;AccessKey=MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=;Version=1.0;";

    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    private static Signal9ApiFactory OnAzureSignalR() => new(settings: new Dictionary<string, string?>
    {
        ["Realtime:Hosting"] = "AzureSignalR",
        ["ConnectionStrings:signalr"] = AzureSignalR,
    });

    [TestMethod]
    public async Task A_single_self_hosted_replica_runs_both_hubs_in_api_with_no_backplane()
    {
        await using var factory = new Signal9ApiFactory();
        using var client = factory.CreateSuperadministratorClient();

        Assert.IsInstanceOfType<DefaultHubLifetimeManager<AgentHub>>(factory.Services.GetRequiredService<HubLifetimeManager<AgentHub>>());
        Assert.IsInstanceOfType<DefaultHubLifetimeManager<DashboardHub>>(factory.Services.GetRequiredService<HubLifetimeManager<DashboardHub>>());
        var negotiate = await client.PostAsync($"{HubRoutes.Dashboard.TrimStart('/')}/negotiate?negotiateVersion=1", null, Ct);
        Assert.AreEqual(HttpStatusCode.OK, negotiate.StatusCode);
        Assert.IsTrue((await negotiate.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("availableTransports").GetArrayLength() > 1,
            "A single replica needs neither sticky sessions nor WebSockets only.");
    }

    [TestMethod]
    public async Task Configuration_puts_both_hubs_on_Azure_SignalR_with_short_lived_service_tokens()
    {
        await using var factory = OnAzureSignalR();
        using var client = factory.CreateSuperadministratorClient();

        Assert.IsNotInstanceOfType<DefaultHubLifetimeManager<AgentHub>>(factory.Services.GetRequiredService<HubLifetimeManager<AgentHub>>());
        Assert.IsNotInstanceOfType<DefaultHubLifetimeManager<DashboardHub>>(factory.Services.GetRequiredService<HubLifetimeManager<DashboardHub>>());
        var service = factory.Services.GetRequiredService<IOptions<ServiceOptions>>().Value;
        Assert.IsLessThanOrEqualTo(TimeSpan.FromMinutes(2), service.AccessTokenLifetime);
        Assert.IsNotNull(service.ClaimsProvider);
    }

    [TestMethod]
    [DataRow("/hubs/agents")]
    [DataRow("/hubs/dashboard")]
    public async Task On_Azure_SignalR_clients_cannot_connect_to_api_directly(string hub)
    {
        await using var factory = OnAzureSignalR();
        // A token with every scope, so only the missing endpoint can refuse it.
        using var client = await factory.CreateUnregisteredClientAsync(Signal9ApiFactory.GrantableScopes);

        var direct = await client.GetAsync($"{hub.TrimStart('/')}?id=anything", Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, direct.StatusCode);
    }

    [TestMethod]
    public void Azure_SignalR_gets_only_the_claims_the_hubs_read()
    {
        var caller = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(Claims.Subject, "agent-or-person"),
            new Claim(Claims.ClientId, "client"),
            new Claim(Claims.Confirmation, """{"x5t#S256":"abc"}""", "JSON"),
            new Claim(Claims.Private.TokenId, "token-id"),
            new Claim(Claims.Private.AuthorizationId, "authorization-id"),
            new Claim(Claims.Private.Scope, Scopes.Agent),
            new Claim(Claims.Private.Scope, Scopes.Devices.Read),
            new Claim(Claims.Private.Scope, Scopes.Telemetry.Read),
            new Claim(Claims.Private.Scope, Scopes.Devices.Write),
            new Claim(Claims.Role, Superadministrator.Role),
            new Claim(Claims.Role, "other"),
            new Claim(Claims.Email, "alex@contoso.example"),
            new Claim(Claims.Name, "Alex Wilber"),
            new Claim(Claims.Audience, "api"),
        ], "Bearer"));

        var passed = RealtimeHosting.ServiceClaims(caller).Select(c => $"{c.Type}={c.Value}").ToArray();

        CollectionAssert.AreEquivalent(new[]
        {
            "sub=agent-or-person",
            "client_id=client",
            """cnf={"x5t#S256":"abc"}""",
            "oi_tkn_id=token-id",
            $"oi_scp={Scopes.Agent}",
            $"oi_scp={Scopes.Devices.Read}",
            $"oi_scp={Scopes.Telemetry.Read}",
            $"role={Superadministrator.Role}",
        }, passed);
    }
}
