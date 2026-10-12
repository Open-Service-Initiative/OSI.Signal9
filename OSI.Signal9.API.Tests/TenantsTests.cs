using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.API.Tests;

public sealed class TenantsTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;

    public TenantsTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task Create_returns_201_with_location_of_the_new_tenant()
    {
        var response = await _client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Code = "contoso", Name = "Contoso", ContactEmail = "it@contoso.example" }, Signal9Json.Options,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(response.Headers.Location);

        var fetched = await _client.GetFromJsonAsync<TenantDto>(response.Headers.Location, Signal9Json.Options, TestContext.Current.CancellationToken);
        Assert.Equal(created, fetched);
        Assert.Equal(TenantStatus.Active, fetched!.Status);
    }

    [Fact]
    public async Task Create_with_duplicate_code_returns_409()
    {
        await _factory.CreateTenantAsync(_client, "contoso");

        var response = await _client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Code = "contoso", Name = "Other", ContactEmail = "a@b.example" }, Signal9Json.Options,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Create_with_invalid_body_returns_400_validation_problem()
    {
        var response = await _client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Code = "Not Valid!", Name = "", ContactEmail = "nope" }, Signal9Json.Options,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>(TestContext.Current.CancellationToken);
        Assert.Contains("Code", problem!.Errors.Keys);
        Assert.Contains("ContactEmail", problem.Errors.Keys);
    }

    [Fact]
    public async Task Clients_can_only_nest_one_level()
    {
        var msp = await _factory.CreateTenantAsync(_client, "msp");
        var client = await _client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Code = "client", Name = "Client", ContactEmail = "a@b.example", ParentTenantId = msp.Id },
            Signal9Json.Options, TestContext.Current.CancellationToken);
        var clientTenant = await client.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, TestContext.Current.CancellationToken);

        var grandchild = await _client.PostAsJsonAsync("api/tenants",
            new CreateTenantRequest { Code = "grandchild", Name = "Grandchild", ContactEmail = "a@b.example", ParentTenantId = clientTenant!.Id },
            Signal9Json.Options, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, client.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, grandchild.StatusCode);
    }

    [Fact]
    public async Task Update_replaces_mutable_fields()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "contoso");

        var response = await _client.PutAsJsonAsync($"api/tenants/{tenant.Id}",
            new UpdateTenantRequest { Name = "Contoso Ltd", ContactEmail = "ops@contoso.example", Plan = SubscriptionPlan.Enterprise, MaxAgents = 500, Status = TenantStatus.Suspended },
            Signal9Json.Options, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, TestContext.Current.CancellationToken);
        Assert.Equal(("Contoso Ltd", "contoso", SubscriptionPlan.Enterprise, TenantStatus.Suspended), (updated!.Name, updated.Code, updated.Plan, updated.Status));
    }

    [Fact]
    public async Task Delete_returns_204_then_404()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "contoso");

        var deleted = await _client.DeleteAsync($"api/tenants/{tenant.Id}", TestContext.Current.CancellationToken);
        var fetched = await _client.GetAsync($"api/tenants/{tenant.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, fetched.StatusCode);
    }

    [Fact]
    public async Task Delete_tenant_with_agents_returns_409()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "contoso");
        await Signal9ApiFactory.RegisterAsync(_client, "contoso", "PC01");

        var response = await _client.DeleteAsync($"api/tenants/{tenant.Id}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task List_is_paged_and_includes_agent_counts()
    {
        await _factory.CreateTenantAsync(_client, "alpha");
        await _factory.CreateTenantAsync(_client, "bravo");
        await Signal9ApiFactory.RegisterAsync(_client, "bravo", "PC01");

        var page = await _client.GetFromJsonAsync<PagedResult<TenantDto>>("api/tenants?page=2&pageSize=1", Signal9Json.Options,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, page!.TotalCount);
        Assert.Equal(2, page.TotalPages);
        var only = Assert.Single(page.Items);
        Assert.Equal(("bravo", 1), (only.Code, only.AgentCount));
    }

    [Fact]
    public async Task Invalid_paging_returns_400()
    {
        var response = await _client.GetAsync("api/tenants?pageSize=1000", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
