using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.IntegrationTests;

[TestClass]
public sealed class TenantsTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;

    private readonly TestContext _testContext;

    public TenantsTests(TestContext testContext)
    {
        _testContext = testContext;
        _client = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Create_returns_201_with_location_of_the_new_tenant()
    {
        var response = await _client.PostAsJsonAsync("api/v1/tenants",
            new CreateTenantRequest { Name = "Contoso", ContactEmail = "it@contoso.example" }, Signal9Json.Options,
            Ct);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct);
        Assert.IsNotNull(response.Headers.Location);

        var fetched = await _client.GetFromJsonAsync<TenantDto>(response.Headers.Location, Signal9Json.Options, Ct);
        Assert.AreEqual(created, fetched);
        Assert.AreEqual(TenantStatus.Active, fetched!.Status);
    }

    [TestMethod]
    public async Task Create_with_invalid_body_returns_400_validation_problem()
    {
        var response = await _client.PostAsJsonAsync("api/v1/tenants",
            new CreateTenantRequest { Name = "", ContactEmail = "nope" }, Signal9Json.Options,
            Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        Assert.Contains("Name", problem!.Errors.Keys);
        Assert.Contains("ContactEmail", problem.Errors.Keys);
    }

    [TestMethod]
    public async Task Tenants_nest_to_the_default_maximum_depth_of_20_below_the_root()
    {
        // The root is at depth 0, so a full chain is 21 tenants deep.
        Guid? parentId = null;
        for (var depth = 0; depth <= 20; depth++)
        {
            var created = await CreateAsync(_client, $"Depth {depth}", parentId);
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, $"depth {depth}");
            parentId = (await created.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct))!.Id;
        }

        var tooDeep = await CreateAsync(_client, "Depth 21", parentId);

        Assert.AreEqual(HttpStatusCode.Conflict, tooDeep.StatusCode);
        Assert.AreEqual("application/problem+json", tooDeep.Content.Headers.ContentType?.MediaType);
        var problem = await tooDeep.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.AreEqual("Tenant tree too deep", problem!.Title);
    }

    [TestMethod]
    public async Task Maximum_depth_is_configurable()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.UseSetting("TenantTree:MaxDepth", "2"));
        using var client = host.CreateClient();
        var root = await CreateAsync(client, "Root", null);
        var rootId = (await root.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct))!.Id;
        var child = await CreateAsync(client, "Child", rootId);
        var childId = (await child.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct))!.Id;

        var grandchild = await CreateAsync(client, "Grandchild", childId);
        var grandchildId = (await grandchild.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct))!.Id;

        var greatGrandchild = await CreateAsync(client, "Great-grandchild", grandchildId);
        var secondChild = await CreateAsync(client, "Second child", rootId);

        Assert.AreEqual(HttpStatusCode.Created, child.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, grandchild.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, greatGrandchild.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, secondChild.StatusCode);
    }

    [TestMethod]
    public async Task A_maximum_depth_of_zero_allows_only_root_tenants()
    {
        using var host = _factory.WithWebHostBuilder(builder => builder.UseSetting("TenantTree:MaxDepth", "0"));
        using var client = host.CreateClient();
        var root = await CreateAsync(client, "Root", null);
        var rootId = (await root.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct))!.Id;
        var secondRoot = await CreateAsync(client, "Second root", null);

        var child = await CreateAsync(client, "Child", rootId);

        Assert.AreEqual(HttpStatusCode.Created, root.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, secondRoot.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, child.StatusCode);
        Assert.AreEqual("application/problem+json", child.Content.Headers.ContentType?.MediaType);
        var problem = await child.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.AreEqual("Tenant tree too deep", problem!.Title);
    }

    [TestMethod]
    public async Task Create_under_a_missing_parent_returns_400()
    {
        var response = await CreateAsync(_client, "Orphan", Guid.NewGuid());

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        Assert.Contains("ParentTenantId", problem!.Errors.Keys);
    }

    [TestMethod]
    public async Task Delete_tenant_with_child_tenants_returns_409()
    {
        var parent = await _factory.CreateTenantAsync(_client, "MSP");
        await CreateAsync(_client, "Client", parent.Id);

        var response = await _client.DeleteAsync($"api/v1/tenants/{parent.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task Update_replaces_mutable_fields()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");

        var response = await _client.PutAsJsonAsync($"api/v1/tenants/{tenant.Id}",
            new UpdateTenantRequest { Name = "Contoso Ltd", ContactEmail = "ops@contoso.example", Plan = SubscriptionPlan.Enterprise, MaxDevices = 500, Status = TenantStatus.Suspended },
            Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct);
        Assert.AreEqual(("Contoso Ltd", SubscriptionPlan.Enterprise, TenantStatus.Suspended), (updated!.Name, updated.Plan, updated.Status));
    }

    [TestMethod]
    public async Task Delete_returns_204_then_404()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");

        var deleted = await _client.DeleteAsync($"api/v1/tenants/{tenant.Id}", Ct);
        var fetched = await _client.GetAsync($"api/v1/tenants/{tenant.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, fetched.StatusCode);
    }

    [TestMethod]
    public async Task Delete_tenant_with_devices_returns_409()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        await Signal9ApiFactory.RegisterAsync(_client, tenant.Id, "PC01");

        var response = await _client.DeleteAsync($"api/v1/tenants/{tenant.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task List_is_paged_and_includes_device_counts()
    {
        await _factory.CreateTenantAsync(_client, "Alpha");
        var bravo = await _factory.CreateTenantAsync(_client, "Bravo");
        await Signal9ApiFactory.RegisterAsync(_client, bravo.Id, "PC01");

        var page = await _client.GetFromJsonAsync<PagedResult<TenantDto>>("api/v1/tenants?page=2&pageSize=1", Signal9Json.Options,
            Ct);

        Assert.AreEqual(2, page!.TotalCount);
        Assert.AreEqual(2, page.TotalPages);
        var only = Assert.ContainsSingle(page.Items);
        Assert.AreEqual(("Bravo", 1), (only.Name, only.DeviceCount));
    }

    [TestMethod]
    public async Task Invalid_paging_returns_400()
    {
        var response = await _client.GetAsync("api/v1/tenants?pageSize=1000", Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, Guid? parentTenantId) =>
        client.PostAsJsonAsync("api/v1/tenants",
            new CreateTenantRequest { Name = name, ContactEmail = "it@tenant.example", ParentTenantId = parentTenantId },
            Signal9Json.Options, Ct);

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
