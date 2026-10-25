using System.Net;
using System.Net.Http.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.IntegrationTests;

[TestClass]
public sealed class DevicesTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;

    private readonly TestContext _testContext;

    public DevicesTests(TestContext testContext)
    {
        _testContext = testContext;
        _client = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Enrolling_an_agent_creates_an_online_device_with_that_agent_active()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");

        using var agent = await RegisterAsync(tenant.Id, "PC01");

        var devices = await ListAsync($"tenantId={tenant.Id}");
        var device = Assert.ContainsSingle(devices.Items);
        Assert.AreEqual(agent.DeviceId, device.Id);
        Assert.AreEqual(agent.Id, device.ActiveAgentId);
        Assert.AreEqual("PC01", device.MachineName);
        Assert.AreEqual(DeviceStatus.Online, device.Status);
        Assert.AreEqual(agent.Id, (await GetDeviceAsync(device.Id)).ActiveAgentId);
    }

    [TestMethod]
    public async Task Status_goes_offline_without_check_ins_and_back_online_with_a_heartbeat()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await RegisterAsync(tenant.Id, "PC01");

        _factory.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(DeviceStatus.Offline, (await GetDeviceAsync(agent.DeviceId)).Status);
        Assert.AreEqual(1, (await ListAsync("status=Offline")).TotalCount);

        var heartbeat = await agent.Client.PutAsJsonAsync($"api/v1/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode);
        Assert.AreEqual(DeviceStatus.Online, (await GetDeviceAsync(agent.DeviceId)).Status);
        Assert.AreEqual(1, (await ListAsync("status=Online")).TotalCount);
    }

    [TestMethod]
    public async Task Patch_merges_group_tags_and_maintenance_mode_and_leaves_status_to_check_ins()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await RegisterAsync(tenant.Id, "PC01");

        var first = await PatchAsync(agent.DeviceId, """{ "groupName": "Finance", "tags": ["laptop", " vip ", "LAPTOP"] }""");
        var second = await PatchAsync(agent.DeviceId, """{ "maintenanceMode": true }""");

        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        var device = await second.Content.ReadFromJsonAsync<DeviceDto>(Signal9Json.Options, Ct);
        Assert.AreEqual("Finance", device!.GroupName);
        CollectionAssert.AreEqual(new[] { "laptop", "vip" }, device.Tags.ToArray());
        Assert.IsTrue(device.MaintenanceMode);
        Assert.AreEqual(DeviceStatus.Online, device.Status);
    }

    [TestMethod]
    public async Task Patch_with_null_clears_a_field()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await RegisterAsync(tenant.Id, "PC01");
        await PatchAsync(agent.DeviceId, """{ "groupName": "Finance" }""");

        var response = await PatchAsync(agent.DeviceId, """{ "groupName": null }""");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNull((await response.Content.ReadFromJsonAsync<DeviceDto>(Signal9Json.Options, Ct))!.GroupName);
    }

    [TestMethod]
    public async Task Patch_rejects_invalid_values_unknown_devices_and_other_media_types()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await RegisterAsync(tenant.Id, "PC01");

        var invalid = await PatchAsync(agent.DeviceId, $$"""{ "groupName": "{{new string('x', 101)}}" }""");
        var notAnObject = await PatchAsync(agent.DeviceId, "[]");
        var missing = await PatchAsync(Guid.NewGuid(), """{ "maintenanceMode": true }""");
        var plainJson = await _client.PatchAsJsonAsync($"api/v1/devices/{agent.DeviceId}", new { maintenanceMode = true }, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, notAnObject.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, plainJson.StatusCode);
    }

    [TestMethod]
    public async Task Deleting_a_device_removes_it_with_its_agents_and_history()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await RegisterAsync(tenant.Id, "PC01");
        await SendTelemetryAsync(agent, cpu: 10);
        await QueueCommandAsync(agent.DeviceId);

        var deleted = await _client.DeleteAsync($"api/v1/devices/{agent.DeviceId}", Ct);
        var again = await _client.DeleteAsync($"api/v1/devices/{agent.DeviceId}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, again.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync($"api/v1/devices/{agent.DeviceId}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync($"api/v1/devices/{agent.DeviceId}/telemetry", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync($"api/v1/devices/{agent.DeviceId}/commands", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync($"api/v1/agents/{agent.Id}", Ct)).StatusCode);
        Assert.AreEqual(0, (await ListAsync($"tenantId={tenant.Id}")).TotalCount);
    }

    [TestMethod]
    public async Task Device_resources_return_404_for_an_unknown_device()
    {
        var unknown = Guid.NewGuid();

        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync($"api/v1/devices/{unknown}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.GetAsync($"api/v1/agents/{unknown}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _client.PostAsJsonAsync($"api/v1/devices/{unknown}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Telemetry_is_listed_newest_first_at_its_canonical_device_url()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await RegisterAsync(tenant.Id, "PC01");

        var response = await agent.Client.PostAsJsonAsync($"api/v1/agents/{agent.Id}/telemetry", new CreateTelemetrySampleRequest
        {
            CollectedAt = _factory.Time.GetUtcNow(),
            CpuPercent = 10,
            Disks = [new DiskUsageDto("C:\\", 1000, 250)],
        }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var sample = await response.Content.ReadFromJsonAsync<TelemetrySampleDto>(Signal9Json.Options, Ct);
        Assert.AreEqual($"/api/v1/devices/{agent.DeviceId}/telemetry/{sample!.Id}", response.Headers.Location?.AbsolutePath);
        var fetched = await _client.GetFromJsonAsync<TelemetrySampleDto>(response.Headers.Location, Signal9Json.Options, Ct);
        Assert.AreEqual(agent.DeviceId, fetched!.DeviceId);
        Assert.AreEqual("C:\\", fetched.Disks.Single().Name);
    }

    [TestMethod]
    public async Task Telemetry_counts_as_a_check_in()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await RegisterAsync(tenant.Id, "PC01");
        _factory.Time.Advance(TimeSpan.FromMinutes(5));

        await SendTelemetryAsync(agent, cpu: 10);

        Assert.AreEqual(DeviceStatus.Online, (await GetDeviceAsync(agent.DeviceId)).Status);
    }

    [TestMethod]
    public async Task Command_lifecycle_at_its_canonical_device_url()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await RegisterAsync(tenant.Id, "PC01");

        var created = await _client.PostAsJsonAsync($"api/v1/devices/{agent.DeviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        var command = await created.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        Assert.AreEqual($"/api/v1/devices/{agent.DeviceId}/commands/{command!.Id}", created.Headers.Location?.AbsolutePath);
        Assert.AreEqual(CommandStatus.Pending, command.Status);

        var pending = await _client.GetFromJsonAsync<PagedResult<CommandDto>>(
            $"api/v1/devices/{agent.DeviceId}/commands?status=Pending", Signal9Json.Options, Ct);
        Assert.AreEqual(command.Id, Assert.ContainsSingle(pending!.Items).Id);

        var result = await agent.Client.PutAsJsonAsync($"api/v1/devices/{agent.DeviceId}/commands/{command.Id}/result",
            new CommandResultRequest { Succeeded = true, Output = "{}" }, Signal9Json.Options, Ct);
        var completed = await result.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        Assert.AreEqual(CommandStatus.Completed, completed!.Status);
        Assert.IsNotNull(completed.CompletedAt);

        var cancel = await _client.PostAsync($"api/v1/devices/{agent.DeviceId}/commands/{command.Id}:cancel", null, Ct);
        Assert.AreEqual(HttpStatusCode.Conflict, cancel.StatusCode);
    }

    [TestMethod]
    public async Task Pending_command_can_be_cancelled()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var agent = await RegisterAsync(tenant.Id, "PC01");
        var command = await QueueCommandAsync(agent.DeviceId);

        var cancel = await _client.PostAsync($"api/v1/devices/{agent.DeviceId}/commands/{command.Id}:cancel", null, Ct);
        var lateResult = await agent.Client.PutAsJsonAsync($"api/v1/devices/{agent.DeviceId}/commands/{command.Id}/result",
            new CommandResultRequest { Succeeded = true }, Signal9Json.Options, Ct);
        var unknown = await _client.PostAsync($"api/v1/devices/{agent.DeviceId}/commands/{Guid.NewGuid()}:cancel", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
        Assert.AreEqual(CommandStatus.Cancelled, (await cancel.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!.Status);
        Assert.AreEqual(HttpStatusCode.Conflict, lateResult.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    private Task<HttpResponseMessage> PatchAsync(Guid deviceId, string mergePatch) =>
        _client.PatchAsync($"api/v1/devices/{deviceId}",
            new StringContent(mergePatch, System.Text.Encoding.UTF8, "application/merge-patch+json"), Ct);

    private Task<TestAgent> RegisterAsync(Guid tenantId, string machineName) => _factory.EnrollAgentAsync(tenantId, machineName);

    private async Task SendTelemetryAsync(TestAgent agent, double cpu)
    {
        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        var response = await agent.Client.PostAsJsonAsync($"api/v1/agents/{agent.Id}/telemetry",
            new CreateTelemetrySampleRequest { CollectedAt = _factory.Time.GetUtcNow(), CpuPercent = cpu }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<CommandDto> QueueCommandAsync(Guid deviceId)
    {
        var response = await _client.PostAsJsonAsync($"api/v1/devices/{deviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<DeviceDto> GetDeviceAsync(Guid deviceId) =>
        (await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{deviceId}", Signal9Json.Options, Ct))!;

    private async Task<PagedResult<DeviceDto>> ListAsync(string query) =>
        (await _client.GetFromJsonAsync<PagedResult<DeviceDto>>($"api/v1/devices?{query}", Signal9Json.Options, Ct))!;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
