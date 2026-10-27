using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Superadministrator is an app role on the operator's Entra app registration, honoured only for people from the
/// deploying directory and re-read at token issuance (ADR-0006, stories 56-59, #113).
/// </summary>
[TestClass]
public sealed class SuperadministratorTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public SuperadministratorTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    private const string ClientId = "superadministrator-test-app";
    private const string Scope = "openid tenants.read tenants.write deployment.read";

    private bool _seeded;

    /// <summary>A person from the deploying directory holding the Superadministrator app role.</summary>
    private EntraUser Superadministrator() =>
        EntraUser.WithMfa() with { TenantId = _factory.Entra.DeployingTenantId, Roles = ["Superadministrator"] };

    /// <summary>Signs <paramref name="user"/> in through <paramref name="browser"/> and returns an api client with their token.</summary>
    private async Task<HttpClient> SignInAsync(EntraUser user, Browser? browser = null)
    {
        if (!_seeded)
            await _factory.SeedCodeClientAsync(ClientId, [.. Scope.Split(' ').Skip(1)]);
        _seeded = true;

        using var ownBrowser = browser is null ? new Browser(_factory.Auth.Server) : null;
        var flow = new CodeFlow(ClientId);
        var code = flow.CodeFrom(await (browser ?? ownBrowser!).AuthorizeAsync(flow.AuthorizeRequest(Scope), _factory.Entra, user));
        using var auth = _factory.Auth.CreateClient();
        var response = await auth.PostAsync("connect/token", flow.Redemption(code), Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("access_token").GetString();

        var api = _factory.CreateAnonymousClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return api;
    }

    private async Task<Guid[]> VisibleTenantsAsync(HttpClient api)
    {
        var response = await api.GetAsync("api/v1/tenants?pageSize=100", Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<TenantDto>>(Signal9Json.Options, Ct);
        return [.. page!.Items.Select(t => t.Id)];
    }

    /// <summary>Two separate tenant trees, made by the fixture's Superadministrator.</summary>
    private async Task<Guid[]> TwoTreesAsync()
    {
        using var fixture = _factory.CreateSuperadministratorClient();
        var first = await _factory.CreateTenantAsync(fixture, "Contoso");
        var child = await _factory.CreateTenantAsync(fixture, "Contoso-Site", parentTenantId: first.Id);
        var second = await _factory.CreateTenantAsync(fixture, "Fabrikam");
        return [first.Id, child.Id, second.Id];
    }

    [TestMethod]
    public async Task A_superadministrator_from_the_deploying_directory_sees_every_tenant_tree()
    {
        var tenants = await TwoTreesAsync();
        using var api = await SignInAsync(Superadministrator());

        CollectionAssert.AreEquivalent(tenants, await VisibleTenantsAsync(api));
    }

    [TestMethod]
    public async Task The_app_role_from_any_other_directory_is_ignored()
    {
        await TwoTreesAsync();
        using var api = await SignInAsync(Superadministrator() with { TenantId = Guid.NewGuid() });

        Assert.IsEmpty(await VisibleTenantsAsync(api));
    }

    [TestMethod]
    public async Task Removing_the_app_role_takes_effect_at_the_next_token_issuance()
    {
        await TwoTreesAsync();
        var person = Superadministrator();
        using var browser = new Browser(_factory.Auth.Server);
        using var before = await SignInAsync(person, browser);
        var visibleBefore = await VisibleTenantsAsync(before);

        // The operator removes the assignment in Entra; the person's session at auth is still live.
        using var after = await SignInAsync(person with { Roles = [] }, browser);

        Assert.HasCount(3, visibleBefore);
        Assert.IsEmpty(await VisibleTenantsAsync(after));
    }

    [TestMethod]
    public async Task A_superadministrator_signed_in_at_auth_is_still_issued_tokens()
    {
        var person = Superadministrator();
        using var browser = new Browser(_factory.Auth.Server);
        using var first = await SignInAsync(person, browser);

        using var second = await SignInAsync(person, browser);

        Assert.AreEqual(HttpStatusCode.Created,
            (await second.PostAsJsonAsync("api/v1/tenants", NewRoot(), Signal9Json.Options, Ct)).StatusCode);
    }

    [TestMethod]
    public async Task A_superadministrator_can_create_root_tenants()
    {
        using var api = await SignInAsync(Superadministrator());

        var response = await api.PostAsJsonAsync("api/v1/tenants", NewRoot(), Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct);
        Assert.IsNull(created!.ParentTenantId);
        CollectionAssert.Contains(await VisibleTenantsAsync(api), created.Id);
    }

    [TestMethod]
    public async Task Nobody_else_can_create_root_tenants()
    {
        using var person = await SignInAsync(EntraUser.WithMfa());
        using var otherDirectory = await SignInAsync(Superadministrator() with { TenantId = Guid.NewGuid() });

        var byPerson = await person.PostAsJsonAsync("api/v1/tenants", NewRoot(), Signal9Json.Options, Ct);
        var byOtherDirectory = await otherDirectory.PostAsJsonAsync("api/v1/tenants", NewRoot(), Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, byPerson.StatusCode);
        Assert.AreEqual("application/problem+json", byPerson.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Forbidden, byOtherDirectory.StatusCode);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("organizations")]
    [DataRow("00000000-0000-0000-0000-000000000000")]
    public void Auth_needs_the_deploying_directory(string deployingTenantId)
    {
        using var factory = new Signal9ApiFactory(settings: new Dictionary<string, string?>
        {
            ["Entra:DeployingTenantId"] = deployingTenantId,
        });

        var exception = Assert.Throws<Exception>(() => factory.Auth.Server);

        Assert.Contains("DeployingTenantId", exception.ToString());
    }

    private static CreateTenantRequest NewRoot() =>
        new() { Name = "Northwind", ContactEmail = "it@northwind.example", MaxDevices = 10 };

    [TestMethod]
    public async Task Deployment_scopes_count_only_for_a_superadministrator()
    {
        using var api = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(DeploymentProbeController).Assembly)));
        async Task<HttpResponseMessage> ProbeAsync(HttpClient client)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "test/deployment-probe");
            request.Headers.Authorization = client.DefaultRequestHeaders.Authorization;
            using var server = api.CreateClient();
            return await server.SendAsync(request, Ct);
        }
        using var superadministrator = await SignInAsync(Superadministrator());
        using var otherDirectory = await SignInAsync(Superadministrator() with { TenantId = Guid.NewGuid() });
        using var application = await _factory.CreateAssignedClientAsync([], Scopes.Deployment.Read);

        var bySuperadministrator = await ProbeAsync(superadministrator);
        var byOtherDirectory = await ProbeAsync(otherDirectory);
        var byApplication = await ProbeAsync(application);

        Assert.AreEqual(HttpStatusCode.OK, bySuperadministrator.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, byOtherDirectory.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, byApplication.StatusCode);
        // Their tokens have the scope, so asking for it again wouldn't help (RFC 6750 §3.1).
        Assert.DoesNotContain("insufficient_scope", byOtherDirectory.Headers.WwwAuthenticate.ToString());
    }

    public void Dispose() => _factory.Dispose();
}

/// <summary>Stands in for the deployment endpoints #76 adds: any action requiring a <c>deployment.*</c> scope.</summary>
[ApiController]
[Route("test/deployment-probe")]
public sealed class DeploymentProbeController : ControllerBase
{
    [RequireScope(Scopes.Deployment.Read)]
    [HttpGet]
    public IActionResult Get() => Ok();
}
