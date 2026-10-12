using System.Net;
using System.Net.Http.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;

namespace OSI.Signal9.API.Tests;

public sealed class AgentsTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AgentsTests() => _client = _factory.CreateClient();

    [Fact]
    public async Task Register_creates_agent_then_reregistering_returns_the_same_agent()
    {
        await _factory.CreateTenantAsync(_client, "contoso");

        var first = await Signal9ApiFactory.RegisterAsync(_client, "contoso", "PC01");
        var second = await Signal9ApiFactory.RegisterAsync(_client, "contoso", "PC01");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.NotNull(first.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var a = await first.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct);
        var b = await second.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct);
        Assert.Equal(a!.Id, b!.Id);
    }

    [Fact]
    public async Task Register_with_unknown_tenant_returns_400()
    {
        var response = await Signal9ApiFactory.RegisterAsync(_client, "nobody", "PC01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_beyond_tenant_limit_returns_409()
    {
        await _factory.CreateTenantAsync(_client, "contoso", maxAgents: 1);
        await Signal9ApiFactory.RegisterAsync(_client, "contoso", "PC01");

        var response = await Signal9ApiFactory.RegisterAsync(_client, "contoso", "PC02");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Status_goes_offline_without_heartbeats_and_back_online_with_one()
    {
        await _factory.CreateTenantAsync(_client, "contoso");
        var agent = await RegisterAgentAsync("PC01");
        Assert.Equal(AgentStatus.Online, agent.Status);

        _factory.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(AgentStatus.Offline, (await GetAgentAsync(agent.Id)).Status);
        Assert.Equal(1, (await ListAsync("status=Offline")).TotalCount);

        var heartbeat = await _client.PutAsJsonAsync($"api/agents/{agent.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.Equal(HttpStatusCode.NoContent, heartbeat.StatusCode);
        Assert.Equal(AgentStatus.Online, (await GetAgentAsync(agent.Id)).Status);
        Assert.Equal(1, (await ListAsync("status=Online")).TotalCount);
    }

    [Fact]
    public async Task Heartbeat_for_unknown_agent_returns_404()
    {
        var response = await _client.PutAsJsonAsync($"api/agents/{Guid.NewGuid()}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_sets_group_tags_and_maintenance()
    {
        await _factory.CreateTenantAsync(_client, "contoso");
        var agent = await RegisterAgentAsync("PC01");

        var response = await _client.PutAsJsonAsync($"api/agents/{agent.Id}",
            new UpdateAgentRequest { GroupName = "Finance", Tags = ["laptop", " vip ", "LAPTOP"], InMaintenance = true }, Signal9Json.Options, Ct);

        var updated = await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Finance", updated!.GroupName);
        Assert.Equal(["laptop", "vip"], updated.Tags);
        Assert.Equal(AgentStatus.Maintenance, updated.Status);
    }

    [Fact]
    public async Task Delete_removes_agent()
    {
        await _factory.CreateTenantAsync(_client, "contoso");
        var agent = await RegisterAgentAsync("PC01");

        var deleted = await _client.DeleteAsync($"api/agents/{agent.Id}", Ct);
        var again = await _client.DeleteAsync($"api/agents/{agent.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task Command_lifecycle()
    {
        await _factory.CreateTenantAsync(_client, "contoso");
        var agent = await RegisterAgentAsync("PC01");

        var created = await _client.PostAsJsonAsync($"api/agents/{agent.Id}/commands",
            new CreateAgentCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        var command = await created.Content.ReadFromJsonAsync<AgentCommandDto>(Signal9Json.Options, Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(CommandStatus.Pending, command!.Status);

        var pending = await _client.GetFromJsonAsync<PagedResult<AgentCommandDto>>(
            $"api/agents/{agent.Id}/commands?status=Pending", Signal9Json.Options, Ct);
        Assert.Equal(command.Id, Assert.Single(pending!.Items).Id);

        var result = await _client.PutAsJsonAsync($"api/agents/{agent.Id}/commands/{command.Id}/result",
            new CommandResultRequest { Succeeded = true, Output = "{}" }, Signal9Json.Options, Ct);
        var completed = await result.Content.ReadFromJsonAsync<AgentCommandDto>(Signal9Json.Options, Ct);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(CommandStatus.Completed, completed!.Status);
        Assert.NotNull(completed.CompletedAt);

        var cancel = await _client.PutAsync($"api/agents/{agent.Id}/commands/{command.Id}/cancellation", null, Ct);
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
    }

    [Fact]
    public async Task Pending_command_can_be_cancelled()
    {
        await _factory.CreateTenantAsync(_client, "contoso");
        var agent = await RegisterAgentAsync("PC01");
        var created = await _client.PostAsJsonAsync($"api/agents/{agent.Id}/commands",
            new CreateAgentCommandRequest { Type = CommandType.RunScript }, Signal9Json.Options, Ct);
        var command = await created.Content.ReadFromJsonAsync<AgentCommandDto>(Signal9Json.Options, Ct);

        var cancel = await _client.PutAsync($"api/agents/{agent.Id}/commands/{command!.Id}/cancellation", null, Ct);
        var lateResult = await _client.PutAsJsonAsync($"api/agents/{agent.Id}/commands/{command.Id}/result",
            new CommandResultRequest { Succeeded = true }, Signal9Json.Options, Ct);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lateResult.StatusCode);
    }

    [Fact]
    public async Task Telemetry_is_stored_newest_first_and_counts_as_a_heartbeat()
    {
        await _factory.CreateTenantAsync(_client, "contoso");
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
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.NotNull(response.Headers.Location);
        }

        var samples = await _client.GetFromJsonAsync<PagedResult<TelemetrySampleDto>>(
            $"api/agents/{agent.Id}/telemetry", Signal9Json.Options, Ct);
        Assert.Equal([20.0, 10.0], samples!.Items.Select(s => s.CpuPercent!.Value));
        Assert.Equal("C:\\", samples.Items[0].Disks.Single().Name);
        Assert.Equal(AgentStatus.Online, (await GetAgentAsync(agent.Id)).Status);
    }

    private async Task<AgentDto> RegisterAgentAsync(string machineName)
    {
        var response = await Signal9ApiFactory.RegisterAsync(_client, "contoso", machineName);
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
