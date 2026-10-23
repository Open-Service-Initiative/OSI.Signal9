using System.Net;
using System.Net.Http.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.API.Tests;

[TestClass]
public sealed class AgentsTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;

    private readonly TestContext _testContext;

    public AgentsTests(TestContext testContext)
    {
        _testContext = testContext;
        _client = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Register_creates_agent_then_reregistering_returns_the_same_agent()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");

        var first = await Signal9ApiFactory.RegisterAsync(_client, tenant.Id, "PC01");
        var second = await Signal9ApiFactory.RegisterAsync(_client, tenant.Id, "PC01");

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.IsNotNull(first.Headers.Location);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct);
        var b = await second.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct);
        Assert.AreEqual(a!.Id, b!.Id);
    }

    [TestMethod]
    public async Task Register_with_unknown_tenant_returns_400()
    {
        var response = await Signal9ApiFactory.RegisterAsync(_client, Guid.NewGuid(), "PC01");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task Register_beyond_tenant_limit_returns_409()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso", maxAgents: 1);
        await Signal9ApiFactory.RegisterAsync(_client, tenant.Id, "PC01");

        var response = await Signal9ApiFactory.RegisterAsync(_client, tenant.Id, "PC02");

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
    }

    [TestMethod]
    public async Task Status_goes_offline_without_heartbeats_and_back_online_with_one()
    {
        var agent = await RegisterAgentAsync("PC01");
        Assert.AreEqual(AgentStatus.Online, agent.Status);

        _factory.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.AreEqual(AgentStatus.Offline, (await GetAgentAsync(agent.Id)).Status);
        Assert.AreEqual(1, (await ListAsync("status=Offline")).TotalCount);

        var heartbeat = await _client.PutAsJsonAsync($"api/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
        Assert.AreEqual(AgentStatus.Online, (await GetAgentAsync(agent.Id)).Status);
        Assert.AreEqual(1, (await ListAsync("status=Online")).TotalCount);
    }

    [TestMethod]
    public async Task Heartbeat_for_unknown_agent_returns_404()
    {
        var response = await _client.PutAsJsonAsync($"api/agents/{Guid.NewGuid()}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Update_sets_group_tags_and_maintenance()
    {
        var agent = await RegisterAgentAsync("PC01");

        var response = await _client.PutAsJsonAsync($"api/agents/{agent.Id}",
            new UpdateAgentRequest { GroupName = "Finance", Tags = ["laptop", " vip ", "LAPTOP"], InMaintenance = true }, Signal9Json.Options, Ct);

        var updated = await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Finance", updated!.GroupName);
        CollectionAssert.AreEqual(new[] { "laptop", "vip" }, updated.Tags.ToArray());
        Assert.AreEqual(AgentStatus.Maintenance, updated.Status);
    }

    [TestMethod]
    public async Task Delete_removes_agent()
    {
        var agent = await RegisterAgentAsync("PC01");

        var deleted = await _client.DeleteAsync($"api/agents/{agent.Id}", Ct);
        var again = await _client.DeleteAsync($"api/agents/{agent.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, again.StatusCode);
    }

    [TestMethod]
    public async Task Command_lifecycle()
    {
        var agent = await RegisterAgentAsync("PC01");

        var created = await _client.PostAsJsonAsync($"api/agents/{agent.Id}/commands",
            new CreateAgentCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        var command = await created.Content.ReadFromJsonAsync<AgentCommandDto>(Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        Assert.AreEqual(CommandStatus.Pending, command!.Status);

        var pending = await _client.GetFromJsonAsync<PagedResult<AgentCommandDto>>(
            $"api/agents/{agent.Id}/commands?status=Pending", Signal9Json.Options, Ct);
        Assert.AreEqual(command.Id, Assert.ContainsSingle(pending!.Items).Id);

        var result = await _client.PutAsJsonAsync($"api/agents/{agent.Id}/commands/{command.Id}/result",
            new CommandResultRequest { Succeeded = true, Output = "{}" }, Signal9Json.Options, Ct);
        var completed = await result.Content.ReadFromJsonAsync<AgentCommandDto>(Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        Assert.AreEqual(CommandStatus.Completed, completed!.Status);
        Assert.IsNotNull(completed.CompletedAt);

        var cancel = await _client.PutAsync($"api/agents/{agent.Id}/commands/{command.Id}/cancellation", null, Ct);
        Assert.AreEqual(HttpStatusCode.Conflict, cancel.StatusCode);
    }

    [TestMethod]
    public async Task Pending_command_can_be_cancelled()
    {
        var agent = await RegisterAgentAsync("PC01");
        var created = await _client.PostAsJsonAsync($"api/agents/{agent.Id}/commands",
            new CreateAgentCommandRequest { Type = CommandType.RunScript }, Signal9Json.Options, Ct);
        var command = await created.Content.ReadFromJsonAsync<AgentCommandDto>(Signal9Json.Options, Ct);

        var cancel = await _client.PutAsync($"api/agents/{agent.Id}/commands/{command!.Id}/cancellation", null, Ct);
        var lateResult = await _client.PutAsJsonAsync($"api/agents/{agent.Id}/commands/{command.Id}/result",
            new CommandResultRequest { Succeeded = true }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.OK, cancel.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, lateResult.StatusCode);
    }

    [TestMethod]
    public async Task Telemetry_is_stored_newest_first_and_counts_as_a_heartbeat()
    {
        var agent = await RegisterAgentAsync("PC01");
        _factory.Time.Advance(TimeSpan.FromMinutes(5));

        foreach (var cpu in new[] { 10.0, 20.0 })
        {
            _factory.Time.Advance(TimeSpan.FromSeconds(1));
            var response = await _client.PostAsJsonAsync($"api/agents/{agent.Id}/telemetry", new CreateTelemetrySampleRequest
            {
                CollectedAt = _factory.Time.GetUtcNow(),
                CpuPercent = cpu,
                Disks = [new DiskUsageDto("C:\\", 1000, 250)],
            }, Signal9Json.Options, Ct);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
            Assert.IsNotNull(response.Headers.Location);
        }

        var samples = await _client.GetFromJsonAsync<PagedResult<TelemetrySampleDto>>(
            $"api/agents/{agent.Id}/telemetry", Signal9Json.Options, Ct);
        CollectionAssert.AreEqual(new[] { 20.0, 10.0 }, samples!.Items.Select(s => s.CpuPercent!.Value).ToArray());
        Assert.AreEqual("C:\\", samples.Items[0].Disks.Single().Name);
        Assert.AreEqual(AgentStatus.Online, (await GetAgentAsync(agent.Id)).Status);
    }

    private async Task<AgentDto> RegisterAgentAsync(string machineName)
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var response = await Signal9ApiFactory.RegisterAsync(_client, tenant.Id, machineName);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<AgentDto> GetAgentAsync(Guid agentId) =>
        (await _client.GetFromJsonAsync<AgentDto>($"api/agents/{agentId}", Signal9Json.Options, Ct))!;

    private async Task<PagedResult<AgentDto>> ListAsync(string query) =>
        (await _client.GetFromJsonAsync<PagedResult<AgentDto>>($"api/agents?{query}", Signal9Json.Options, Ct))!;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
