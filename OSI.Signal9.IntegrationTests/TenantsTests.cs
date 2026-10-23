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
    public async Task Patch_merges_editable_fields_and_leaves_the_rest()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");

        var response = await PatchAsync(tenant.Id, """{ "name": "Contoso Ltd", "plan": "Enterprise", "maxDevices": 500 }""");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct);
        Assert.AreEqual(("Contoso Ltd", SubscriptionPlan.Enterprise, 500), (updated!.Name, updated.Plan, updated.MaxDevices));
        Assert.AreEqual((tenant.ContactEmail, TenantStatus.Active), (updated.ContactEmail, updated.Status));
        Assert.AreEqual(updated, await _client.GetFromJsonAsync<TenantDto>($"api/v1/tenants/{tenant.Id}", Signal9Json.Options, Ct));
    }

    [TestMethod]
    public async Task Patch_with_null_clears_an_optional_field()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        await PatchAsync(tenant.Id, """{ "description": "Head office" }""");

        var response = await PatchAsync(tenant.Id, """{ "description": null }""");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNull((await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct))!.Description);
    }

    [TestMethod]
    public async Task Patch_rejects_invalid_values_read_only_fields_unknown_tenants_and_other_media_types()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");

        var invalid = await PatchAsync(tenant.Id, """{ "name": "", "contactEmail": "nope" }""");
        var removesRequired = await PatchAsync(tenant.Id, """{ "name": null }""");
        var notAnObject = await PatchAsync(tenant.Id, "[]");
        var status = await PatchAsync(tenant.Id, """{ "status": "Suspended" }""");
        var parent = await PatchAsync(tenant.Id, $$"""{ "parentTenantId": "{{Guid.NewGuid()}}" }""");
        var missing = await PatchAsync(Guid.NewGuid(), """{ "name": "Nobody" }""");
        var plainJson = await _client.PatchAsJsonAsync($"api/v1/tenants/{tenant.Id}", new { name = "Plain" }, Ct);
        var put = await _client.PutAsJsonAsync($"api/v1/tenants/{tenant.Id}", new { name = "Replaced" }, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        var problem = await invalid.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        Assert.Contains("Name", problem!.Errors.Keys);
        Assert.Contains("ContactEmail", problem.Errors.Keys);
        Assert.AreEqual(HttpStatusCode.BadRequest, removesRequired.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, notAnObject.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, status.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, parent.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, plainJson.StatusCode);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        var current = await _client.GetFromJsonAsync<TenantDto>($"api/v1/tenants/{tenant.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual(tenant, current);
    }

    [TestMethod]
    [DataRow("", "suspend", TenantStatus.Suspended)]
    [DataRow("", "cancel", TenantStatus.Cancelled)]
    [DataRow("suspend", "activate", TenantStatus.Active)]
    [DataRow("suspend", "cancel", TenantStatus.Cancelled)]
    [DataRow("cancel", "activate", TenantStatus.Active)]
    public async Task An_allowed_transition_returns_200_with_the_new_status(string from, string verb, TenantStatus expected)
    {
        var tenant = await TenantInStatusAsync(from);

        var response = await _client.PostAsync($"api/v1/tenants/{tenant.Id}:{verb}", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(response.Headers.ETag);
        var updated = await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct);
        Assert.AreEqual(expected, updated!.Status);
        Assert.AreEqual(updated, await _client.GetFromJsonAsync<TenantDto>($"api/v1/tenants/{tenant.Id}", Signal9Json.Options, Ct));
    }

    [TestMethod]
    [DataRow("", "activate", TenantStatus.Active)]
    [DataRow("suspend", "suspend", TenantStatus.Suspended)]
    [DataRow("cancel", "cancel", TenantStatus.Cancelled)]
    [DataRow("cancel", "suspend", TenantStatus.Cancelled)]
    public async Task A_transition_the_current_status_does_not_allow_returns_409(string from, string verb, TenantStatus current)
    {
        var tenant = await TenantInStatusAsync(from);

        var response = await _client.PostAsync($"api/v1/tenants/{tenant.Id}:{verb}", null, Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(current, (await _client.GetFromJsonAsync<TenantDto>($"api/v1/tenants/{tenant.Id}", Signal9Json.Options, Ct))!.Status);
    }

    [TestMethod]
    public async Task A_transition_on_an_unknown_tenant_returns_404()
    {
        var response = await _client.PostAsync($"api/v1/tenants/{Guid.NewGuid()}:suspend", null, Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
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

    private async Task<TenantDto> TenantInStatusAsync(string verb)
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        if (verb.Length > 0)
            (await _client.PostAsync($"api/v1/tenants/{tenant.Id}:{verb}", null, Ct)).EnsureSuccessStatusCode();
        return tenant;
    }

    private Task<HttpResponseMessage> PatchAsync(Guid tenantId, string mergePatch) =>
        _client.PatchAsync($"api/v1/tenants/{tenantId}",
            new StringContent(mergePatch, System.Text.Encoding.UTF8, "application/merge-patch+json"), Ct);

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
