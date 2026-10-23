using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Tests;

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
        var response = await _client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Code = "contoso", Name = "Contoso", ContactEmail = "it@contoso.example" }, Signal9Json.Options,
            Ct);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct);
        Assert.IsNotNull(response.Headers.Location);

        var fetched = await _client.GetFromJsonAsync<TenantDto>(response.Headers.Location, Signal9Json.Options, Ct);
        Assert.AreEqual(created, fetched);
        Assert.AreEqual(TenantStatus.Active, fetched!.Status);
    }

    [TestMethod]
    public async Task Create_with_duplicate_code_returns_409()
    {
        await _factory.CreateTenantAsync(_client, "contoso");

        var response = await _client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Code = "contoso", Name = "Other", ContactEmail = "a@b.example" }, Signal9Json.Options,
            Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task Create_with_invalid_body_returns_400_validation_problem()
    {
        var response = await _client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Code = "Not Valid!", Name = "", ContactEmail = "nope" }, Signal9Json.Options,
            Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(Ct);
        Assert.Contains("Code", problem!.Errors.Keys);
        Assert.Contains("ContactEmail", problem.Errors.Keys);
    }

    [TestMethod]
    public async Task Clients_can_only_nest_one_level()
    {
        var msp = await _factory.CreateTenantAsync(_client, "msp");
        var client = await _client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Code = "client", Name = "Client", ContactEmail = "a@b.example", ParentTenantId = msp.Id },
            Signal9Json.Options, Ct);
        var clientTenant = await client.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct);

        var grandchild = await _client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Code = "grandchild", Name = "Grandchild", ContactEmail = "a@b.example", ParentTenantId = clientTenant!.Id },
            Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, client.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, grandchild.StatusCode);
    }

    [TestMethod]
    public async Task Update_replaces_mutable_fields()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "contoso");

        var response = await _client.PutAsJsonAsync($"api/tenants/{tenant.Id}",
            new UpdateTenantRequest { Name = "Contoso Ltd", ContactEmail = "ops@contoso.example", Plan = SubscriptionPlan.Enterprise, MaxAgents = 500, Status = TenantStatus.Suspended },
            Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct);
        Assert.AreEqual(("Contoso Ltd", "contoso", SubscriptionPlan.Enterprise, TenantStatus.Suspended), (updated!.Name, updated.Code, updated.Plan, updated.Status));
    }

    [TestMethod]
    public async Task Delete_returns_204_then_404()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "contoso");

        var deleted = await _client.DeleteAsync($"api/tenants/{tenant.Id}", Ct);
        var fetched = await _client.GetAsync($"api/tenants/{tenant.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, fetched.StatusCode);
    }

    [TestMethod]
    public async Task Delete_tenant_with_agents_returns_409()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "contoso");
        await Signal9ApiFactory.RegisterAsync(_client, "contoso", "PC01");

        var response = await _client.DeleteAsync($"api/tenants/{tenant.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task List_is_paged_and_includes_agent_counts()
    {
        await _factory.CreateTenantAsync(_client, "alpha");
        await _factory.CreateTenantAsync(_client, "bravo");
        await Signal9ApiFactory.RegisterAsync(_client, "bravo", "PC01");

        var page = await _client.GetFromJsonAsync<PagedResult<TenantDto>>("api/tenants?page=2&pageSize=1", Signal9Json.Options,
            Ct);

        Assert.AreEqual(2, page!.TotalCount);
        Assert.AreEqual(2, page.TotalPages);
        var only = Assert.ContainsSingle(page.Items);
        Assert.AreEqual(("bravo", 1), (only.Code, only.AgentCount));
    }

    [TestMethod]
    public async Task Invalid_paging_returns_400()
    {
        var response = await _client.GetAsync("api/tenants?pageSize=1000", Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
