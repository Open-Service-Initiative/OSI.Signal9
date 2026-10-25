using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.API;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Optimistic concurrency (RFC 9110 §8.8.3, §13): strong ETags on single resources and write responses,
/// If-Match on writes (412 when stale) and If-None-Match on reads (304 when unchanged).
/// </summary>
[TestClass]
public sealed class ConditionalRequestsTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly TestContext _testContext;

    public ConditionalRequestsTests(TestContext testContext)
    {
        _testContext = testContext;
        _client = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    // Tenants

    [TestMethod]
    public async Task Tenant_get_returns_a_strong_etag_and_304_when_unchanged()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");

        var first = await _client.GetAsync($"api/v1/tenants/{tenant.Id}", Ct);
        var etag = AssertStrongETag(first);
        var unchanged = await SendAsync(HttpMethod.Get, $"api/v1/tenants/{tenant.Id}", ifNoneMatch: etag);
        var star = await SendAsync(HttpMethod.Get, $"api/v1/tenants/{tenant.Id}", ifNoneMatch: "*");
        var other = await SendAsync(HttpMethod.Get, $"api/v1/tenants/{tenant.Id}", ifNoneMatch: "\"other\"");

        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.AreEqual(etag, unchanged.Headers.ETag?.ToString());
        Assert.AreEqual(HttpStatusCode.NotModified, star.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, other.StatusCode);
    }

    [TestMethod]
    public async Task Tenant_create_returns_the_etag_of_the_new_tenant()
    {
        var response = await _client.PostAsJsonAsync("api/v1/tenants",
            new CreateTenantRequest { Name = "Contoso", ContactEmail = "it@contoso.example" }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var etag = AssertStrongETag(response);
        Assert.AreEqual(etag, AssertStrongETag(await _client.GetAsync(response.Headers.Location, Ct)));
    }

    [TestMethod]
    public async Task Tenant_patch_with_a_matching_etag_succeeds_and_returns_the_new_etag()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var etag = await GetETagAsync($"api/v1/tenants/{tenant.Id}");

        var response = await PatchTenantAsync(tenant.Id, "Contoso Ltd", ifMatch: etag);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var newETag = AssertStrongETag(response);
        Assert.AreNotEqual(etag, newETag);
        Assert.AreEqual(newETag, await GetETagAsync($"api/v1/tenants/{tenant.Id}"));
    }

    [TestMethod]
    public async Task Tenant_patch_with_a_stale_etag_returns_412_problem_and_changes_nothing()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var stale = await GetETagAsync($"api/v1/tenants/{tenant.Id}");
        await PatchTenantAsync(tenant.Id, "Contoso Ltd", ifMatch: stale);

        var response = await PatchTenantAsync(tenant.Id, "Lost update", ifMatch: stale);

        await AssertPreconditionFailedAsync(response);
        var current = await _client.GetFromJsonAsync<TenantDto>($"api/v1/tenants/{tenant.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual("Contoso Ltd", current!.Name);
    }

    [TestMethod]
    public async Task Tenant_patch_honours_if_match_star_and_rejects_weak_etags()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var etag = await GetETagAsync($"api/v1/tenants/{tenant.Id}");

        var weak = await PatchTenantAsync(tenant.Id, "Weak", ifMatch: "W/" + etag);
        var star = await PatchTenantAsync(tenant.Id, "Star", ifMatch: "*");
        var unconditional = await PatchTenantAsync(tenant.Id, "Unconditional", ifMatch: null);

        await AssertPreconditionFailedAsync(weak);
        Assert.AreEqual(HttpStatusCode.OK, star.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, unconditional.StatusCode);
    }

    [TestMethod]
    public async Task Tenant_transitions_honour_if_match()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var stale = await GetETagAsync($"api/v1/tenants/{tenant.Id}");
        await PatchTenantAsync(tenant.Id, "Contoso Ltd", ifMatch: null);
        var current = await GetETagAsync($"api/v1/tenants/{tenant.Id}");

        var staleSuspend = await SendAsync(HttpMethod.Post, $"api/v1/tenants/{tenant.Id}:suspend", ifMatch: stale);
        var suspend = await SendAsync(HttpMethod.Post, $"api/v1/tenants/{tenant.Id}:suspend", ifMatch: current);

        await AssertPreconditionFailedAsync(staleSuspend);
        Assert.AreEqual(HttpStatusCode.OK, suspend.StatusCode);
        Assert.AreEqual(AssertStrongETag(suspend), await GetETagAsync($"api/v1/tenants/{tenant.Id}"));
    }

    [TestMethod]
    public async Task Tenant_delete_honours_if_match()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var stale = await GetETagAsync($"api/v1/tenants/{tenant.Id}");
        await PatchTenantAsync(tenant.Id, "Contoso Ltd", ifMatch: null);
        var current = await GetETagAsync($"api/v1/tenants/{tenant.Id}");

        // Deleting a tenant needs a person with a recent MFA (#120).
        using var superadministrator = await _factory.CreatePersonClientAsync(_factory.Superadministrator());

        var staleDelete = await SendAsync(HttpMethod.Delete, $"api/v1/tenants/{tenant.Id}", ifMatch: stale, client: superadministrator);
        var delete = await SendAsync(HttpMethod.Delete, $"api/v1/tenants/{tenant.Id}", ifMatch: current, client: superadministrator);
        var missing = await SendAsync(HttpMethod.Delete, $"api/v1/tenants/{tenant.Id}", ifMatch: current, client: superadministrator);

        await AssertPreconditionFailedAsync(staleDelete);
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task Tenant_etag_changes_when_its_device_count_changes()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var before = await GetETagAsync($"api/v1/tenants/{tenant.Id}");

        await Signal9ApiFactory.RegisterAsync(_client, tenant.Id, "PC01");

        var response = await SendAsync(HttpMethod.Get, $"api/v1/tenants/{tenant.Id}", ifNoneMatch: before);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task Malformed_preconditions_return_400()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");

        var ifMatch = await PatchTenantAsync(tenant.Id, "Contoso Ltd", ifMatch: "not-a-tag");
        var ifNoneMatch = await SendAsync(HttpMethod.Get, $"api/v1/tenants/{tenant.Id}", ifNoneMatch: "not-a-tag");

        Assert.AreEqual(HttpStatusCode.BadRequest, ifMatch.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, ifNoneMatch.StatusCode);
    }

    [TestMethod]
    public async Task If_match_on_a_create_fails_because_the_new_resource_has_no_current_representation()
    {
        var response = await SendAsync(HttpMethod.Post, "api/v1/tenants", ifMatch: "*",
            content: JsonContent.Create(new CreateTenantRequest { Name = "Contoso", ContactEmail = "it@contoso.example" }, options: Signal9Json.Options));

        await AssertPreconditionFailedAsync(response);
    }

    // Devices

    [TestMethod]
    public async Task Device_get_returns_a_strong_etag_and_304_when_unchanged()
    {
        var agent = await RegisterAsync("PC01");

        var etag = await GetETagAsync($"api/v1/devices/{agent.DeviceId}");
        var unchanged = await SendAsync(HttpMethod.Get, $"api/v1/devices/{agent.DeviceId}", ifNoneMatch: etag);

        Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.AreEqual(etag, unchanged.Headers.ETag?.ToString());
    }

    [TestMethod]
    public async Task Device_etag_changes_with_check_ins_and_status()
    {
        var agent = await RegisterAsync("PC01");
        var online = await GetETagAsync($"api/v1/devices/{agent.DeviceId}");

        _factory.Time.Advance(TimeSpan.FromMinutes(5));
        var offline = await GetETagAsync($"api/v1/devices/{agent.DeviceId}");
        await _client.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        var checkedIn = await GetETagAsync($"api/v1/devices/{agent.DeviceId}");

        Assert.AreNotEqual(online, offline);
        Assert.AreNotEqual(online, checkedIn);
        Assert.AreNotEqual(offline, checkedIn);
    }

    [TestMethod]
    public async Task Device_patch_honours_if_match()
    {
        var agent = await RegisterAsync("PC01");
        var etag = await GetETagAsync($"api/v1/devices/{agent.DeviceId}");

        var patched = await PatchDeviceAsync(agent.DeviceId, """{ "groupName": "Finance" }""", ifMatch: etag);
        var stale = await PatchDeviceAsync(agent.DeviceId, """{ "groupName": "Lost update" }""", ifMatch: etag);

        Assert.AreEqual(HttpStatusCode.OK, patched.StatusCode);
        var newETag = AssertStrongETag(patched);
        Assert.AreNotEqual(etag, newETag);
        Assert.AreEqual(newETag, await GetETagAsync($"api/v1/devices/{agent.DeviceId}"));
        await AssertPreconditionFailedAsync(stale);
        var device = await _client.GetFromJsonAsync<Contracts.Devices.DeviceDto>($"api/v1/devices/{agent.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual("Finance", device!.GroupName);
    }

    [TestMethod]
    public async Task Device_delete_honours_if_match()
    {
        var agent = await RegisterAsync("PC01");
        var stale = await GetETagAsync($"api/v1/devices/{agent.DeviceId}");
        await PatchDeviceAsync(agent.DeviceId, """{ "groupName": "Finance" }""", ifMatch: null);
        var current = await GetETagAsync($"api/v1/devices/{agent.DeviceId}");

        var staleDelete = await SendAsync(HttpMethod.Delete, $"api/v1/devices/{agent.DeviceId}", ifMatch: stale);
        var delete = await SendAsync(HttpMethod.Delete, $"api/v1/devices/{agent.DeviceId}", ifMatch: current);
        var missing = await SendAsync(HttpMethod.Delete, $"api/v1/devices/{agent.DeviceId}", ifMatch: current);

        await AssertPreconditionFailedAsync(staleDelete);
        Assert.AreEqual(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    // Commands

    [TestMethod]
    public async Task Command_create_and_get_return_the_same_etag_and_304_when_unchanged()
    {
        var agent = await RegisterAsync("PC01");

        var created = await QueueCommandAsync(agent.DeviceId);
        var etag = AssertStrongETag(created);
        var unchanged = await SendAsync(HttpMethod.Get, created.Headers.Location!.ToString(), ifNoneMatch: etag);

        Assert.AreEqual(etag, await GetETagAsync(created.Headers.Location.ToString()));
        Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
    }

    [TestMethod]
    public async Task Command_cancel_honours_if_match()
    {
        var agent = await RegisterAsync("PC01");
        var created = await QueueCommandAsync(agent.DeviceId);
        var url = created.Headers.Location!.ToString();
        var etag = AssertStrongETag(created);

        var stale = await SendAsync(HttpMethod.Post, $"{url}:cancel", ifMatch: "\"stale\"");
        var cancelled = await SendAsync(HttpMethod.Post, $"{url}:cancel", ifMatch: etag);
        var again = await SendAsync(HttpMethod.Post, $"{url}:cancel", ifMatch: etag);

        await AssertPreconditionFailedAsync(stale);
        Assert.AreEqual(HttpStatusCode.OK, cancelled.StatusCode);
        Assert.AreEqual(await GetETagAsync(url), AssertStrongETag(cancelled));
        await AssertPreconditionFailedAsync(again);
    }

    [TestMethod]
    public async Task Command_result_honours_if_match()
    {
        var agent = await RegisterAsync("PC01");
        var created = await QueueCommandAsync(agent.DeviceId);
        var url = created.Headers.Location!.ToString();
        var etag = AssertStrongETag(created);

        var stale = await SendAsync(HttpMethod.Put, $"{url}/result", ifMatch: "\"stale\"", content: ResultContent());
        var reported = await SendAsync(HttpMethod.Put, $"{url}/result", ifMatch: etag, content: ResultContent());

        await AssertPreconditionFailedAsync(stale);
        Assert.AreEqual(HttpStatusCode.OK, reported.StatusCode);
        Assert.AreEqual(await GetETagAsync(url), AssertStrongETag(reported));
    }

    // Agents and telemetry

    [TestMethod]
    public async Task Agent_and_telemetry_resources_carry_etags()
    {
        var registered = await Signal9ApiFactory.RegisterAsync(_client, (await _factory.CreateTenantAsync(_client, "Contoso")).Id, "PC01");
        var agent = await registered.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct);
        var sample = await _client.PostAsJsonAsync($"api/v1/agents/{agent!.Id}/telemetry",
            new CreateTelemetrySampleRequest { CollectedAt = _factory.Time.GetUtcNow() }, Signal9Json.Options, Ct);
        var agentETag = await GetETagAsync($"api/v1/agents/{agent.Id}");
        var heartbeat = await SendAsync(HttpMethod.Put, $"api/v1/agents/{agent.Id}/heartbeat", ifMatch: agentETag,
            content: JsonContent.Create(new HeartbeatRequest(), options: Signal9Json.Options));

        Assert.AreEqual(HttpStatusCode.Created, registered.StatusCode);
        AssertStrongETag(registered);
        Assert.AreEqual(HttpStatusCode.Created, sample.StatusCode);
        Assert.AreEqual(AssertStrongETag(sample), await GetETagAsync(sample.Headers.Location!.ToString()));
        // The heartbeat is a write-only sub-resource with no representation, so no entity tag can match it.
        await AssertPreconditionFailedAsync(heartbeat);
    }

    // Races: a write that passes its If-Match check but loses the race to save must not silently overwrite.

    [TestMethod]
    public async Task Tenant_patch_that_loses_a_race_returns_412_or_409_and_keeps_the_concurrent_change()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var etag = await GetETagAsync($"api/v1/tenants/{tenant.Id}");

        using var racing = RaceWith($"""UPDATE "Tenants" SET "Name" = 'Concurrent' WHERE "Id" = '{tenant.Id}'""");
        using var client = racing.CreateClient();
        var conditional = await PatchTenantAsync(client, tenant.Id, "Lost update", ifMatch: etag);
        var unconditional = await PatchTenantAsync(client, tenant.Id, "Lost update", ifMatch: null);

        await AssertPreconditionFailedAsync(conditional);
        Assert.AreEqual(HttpStatusCode.Conflict, unconditional.StatusCode);
        var current = await _client.GetFromJsonAsync<TenantDto>($"api/v1/tenants/{tenant.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual("Concurrent", current!.Name);
    }

    [TestMethod]
    public async Task Tenant_delete_that_loses_a_race_returns_412()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var etag = await GetETagAsync($"api/v1/tenants/{tenant.Id}");

        using var racing = RaceWith($"""UPDATE "Tenants" SET "Name" = 'Concurrent' WHERE "Id" = '{tenant.Id}'""");
        using var client = racing.CreateClient();
        // Deleting a tenant needs a person with a recent MFA (#120).
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", await _factory.PersonTokenAsync(_factory.Superadministrator()));
        var request = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/tenants/{tenant.Id}");
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        var response = await client.SendAsync(request, Ct);

        await AssertPreconditionFailedAsync(response);
        Assert.AreEqual(HttpStatusCode.OK, (await _client.GetAsync($"api/v1/tenants/{tenant.Id}", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Device_patch_and_delete_that_lose_a_race_return_412()
    {
        var agent = await RegisterAsync("PC01");
        var etag = await GetETagAsync($"api/v1/devices/{agent.DeviceId}");

        using var racing = RaceWith($"""UPDATE "Devices" SET "GroupName" = 'Concurrent' WHERE "Id" = '{agent.DeviceId}'""");
        using var client = racing.CreateClient();
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/devices/{agent.DeviceId}")
        {
            Content = new StringContent("""{ "groupName": "Lost update" }""", System.Text.Encoding.UTF8, "application/merge-patch+json"),
        };
        patch.Headers.TryAddWithoutValidation("If-Match", etag);
        var patched = await client.SendAsync(patch, Ct);
        var delete = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/devices/{agent.DeviceId}");
        delete.Headers.TryAddWithoutValidation("If-Match", etag);
        var deleted = await client.SendAsync(delete, Ct);

        await AssertPreconditionFailedAsync(patched);
        await AssertPreconditionFailedAsync(deleted);
        var device = await _client.GetFromJsonAsync<Contracts.Devices.DeviceDto>($"api/v1/devices/{agent.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual("Concurrent", device!.GroupName);
    }

    [TestMethod]
    public async Task Command_cancel_that_loses_a_race_to_its_result_returns_412()
    {
        var agent = await RegisterAsync("PC01");
        var created = await QueueCommandAsync(agent.DeviceId);
        var url = created.Headers.Location!.ToString();
        var command = await created.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct);

        using var racing = RaceWith($"""UPDATE "Commands" SET "Status" = 'Completed' WHERE "Id" = '{command!.Id}'""");
        using var client = racing.CreateClient();
        var cancel = new HttpRequestMessage(HttpMethod.Post, $"{url}:cancel");
        cancel.Headers.TryAddWithoutValidation("If-Match", AssertStrongETag(created));
        var response = await client.SendAsync(cancel, Ct);

        await AssertPreconditionFailedAsync(response);
        var current = await _client.GetFromJsonAsync<CommandDto>(url, Signal9Json.Options, Ct);
        Assert.AreEqual(CommandStatus.Completed, current!.Status);
    }

    [TestMethod]
    public async Task Agent_check_ins_are_last_write_wins_and_never_lose_a_race()
    {
        var agent = await RegisterAsync("PC01");

        using var racing = RaceWith($"""UPDATE "Agents" SET "AgentVersion" = 'Concurrent' WHERE "Id" = '{agent.Id}'; UPDATE "Devices" SET "GroupName" = 'Concurrent' WHERE "Id" = '{agent.DeviceId}'""");
        using var client = racing.CreateClient();
        var heartbeat = await client.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat",
            new HeartbeatRequest { AgentVersion = "2.0.0", IpAddress = "10.0.0.2" }, Signal9Json.Options, Ct);
        var telemetry = await client.PostAsJsonAsync($"api/v1/agents/{agent.Id}/telemetry",
            new CreateTelemetrySampleRequest { CollectedAt = _factory.Time.GetUtcNow() }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, telemetry.StatusCode);
        var device = await _client.GetFromJsonAsync<Contracts.Devices.DeviceDto>($"api/v1/devices/{agent.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual(("10.0.0.2", "Concurrent"), (device!.IpAddress, device.GroupName));
    }

    // Helpers

    /// <summary>A host where each request's first UPDATE or DELETE is preceded by <paramref name="sql"/>, committed on another connection.</summary>
    private WebApplicationFactory<ApiHost> RaceWith(string sql) =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<Signal9DbContext>(options =>
                options.AddInterceptors(new ConcurrentWriter(_factory.ConnectionString, sql)))));

    private async Task<AgentDto> RegisterAsync(string machineName)
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var response = await Signal9ApiFactory.RegisterAsync(_client, tenant.Id, machineName);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<HttpResponseMessage> QueueCommandAsync(Guid deviceId)
    {
        var response = await _client.PostAsJsonAsync($"api/v1/devices/{deviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return response;
    }

    private static JsonContent ResultContent() =>
        JsonContent.Create(new CommandResultRequest { Succeeded = true, Output = "{}" }, options: Signal9Json.Options);

    private Task<HttpResponseMessage> PatchDeviceAsync(Guid deviceId, string mergePatch, string? ifMatch) =>
        SendAsync(HttpMethod.Patch, $"api/v1/devices/{deviceId}", ifMatch: ifMatch,
            content: new StringContent(mergePatch, System.Text.Encoding.UTF8, "application/merge-patch+json"));

    private Task<HttpResponseMessage> PatchTenantAsync(Guid tenantId, string name, string? ifMatch) =>
        PatchTenantAsync(_client, tenantId, name, ifMatch);

    private Task<HttpResponseMessage> PatchTenantAsync(HttpClient client, Guid tenantId, string name, string? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/tenants/{tenantId}")
        {
            Content = JsonContent.Create(new { name }, new MediaTypeHeaderValue("application/merge-patch+json"), Signal9Json.Options),
        };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string uri, string? ifMatch = null, string? ifNoneMatch = null, HttpContent? content = null,
        HttpClient? client = null)
    {
        var request = new HttpRequestMessage(method, uri) { Content = content };
        if (ifMatch is not null)
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return (client ?? _client).SendAsync(request, Ct);
    }

    private async Task<string> GetETagAsync(string uri)
    {
        var response = await _client.GetAsync(uri, Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return AssertStrongETag(response);
    }

    private static string AssertStrongETag(HttpResponseMessage response)
    {
        var etag = response.Headers.ETag;
        Assert.IsNotNull(etag, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} has no ETag.");
        Assert.IsFalse(etag.IsWeak, "The ETag must be strong.");
        return etag.ToString();
    }

    private async Task AssertPreconditionFailedAsync(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.AreEqual(412, problem!.Status);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
