using System.Net;
using System.Net.Http.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;
using OSI.Signal9.Contracts.Users;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Device transfer (#123, story 75): moving a device to another tenant is its own scope, <c>devices.transfer</c>, and
/// an action, <c>POST /devices/{id}:transfer</c>. The device's agent, telemetry and command history go with it.
/// </summary>
[TestClass]
public sealed class DeviceTransferTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public DeviceTransferTests(TestContext testContext)
    {
        _testContext = testContext;
        // The fixture's own client stands in for a Superadministrator.
        _superadministrator = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task A_transferred_device_takes_its_agent_and_history_to_the_new_tenants_subtree()
    {
        var root = await _factory.CreateTenantAsync(_superadministrator, "Msp");
        var from = await _factory.CreateTenantAsync(_superadministrator, "Contoso", parentTenantId: root.Id);
        var to = await _factory.CreateTenantAsync(_superadministrator, "Fabrikam", parentTenantId: root.Id);
        using var agent = await _factory.EnrollAgentAsync(from.Id, "PC01");
        var command = await QueueCommandAsync(agent.DeviceId);
        var sample = await SendTelemetryAsync(agent);
        using var technician = await PersonAsync(root.Id, ("Technician", from.Id), ("Technician", to.Id));
        using var oldSide = await PersonAsync(from.Id, ("Viewer", from.Id));
        using var newSide = await PersonAsync(to.Id, ("Viewer", to.Id));

        var response = await TransferAsync(technician, agent.DeviceId, to.Id);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.IsNotNull(response.Headers.ETag);
        var device = (await response.Content.ReadFromJsonAsync<DeviceDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(to.Id, device.TenantId);
        Assert.AreEqual(agent.Id, device.ActiveAgentId);

        foreach (var path in HistoryPaths(agent, command, sample))
        {
            Assert.AreEqual(HttpStatusCode.OK, (await newSide.GetAsync(path, Ct)).StatusCode, path);
            Assert.AreEqual(HttpStatusCode.NotFound, (await oldSide.GetAsync(path, Ct)).StatusCode, path);
        }
        Assert.AreEqual(to.Id, (await newSide.GetFromJsonAsync<AgentDto>($"api/v1/agents/{agent.Id}", Signal9Json.Options, Ct))!.TenantId);
        CollectionAssert.Contains(await DeviceIdsAsync(newSide), agent.DeviceId);
        CollectionAssert.DoesNotContain(await DeviceIdsAsync(oldSide), agent.DeviceId);
        Assert.AreEqual(1, (await AgentsAsync(newSide, to.Id)).TotalCount);
        Assert.AreEqual(0, (await AgentsAsync(oldSide, from.Id)).TotalCount);
    }

    [TestMethod]
    public async Task Transfer_needs_the_devices_transfer_scope_not_devices_write()
    {
        var (from, to, deviceId) = await TwoSitesAsync();
        var person = await SignedUpAsync(from, ("Administrator", from), ("Administrator", to));
        using var writer = await _factory.CreatePersonClientAsync(person,
            [.. Signal9ApiFactory.PersonScopes.Where(s => s != "devices.transfer")]);

        var response = await TransferAsync(writer, deviceId, to);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("insufficient_scope", response.Headers.WwwAuthenticate.ToString());
        Assert.Contains("devices.transfer", response.Headers.WwwAuthenticate.ToString());
        Assert.AreEqual(from, (await GetDeviceAsync(deviceId)).TenantId);
    }

    [TestMethod]
    public async Task Transfer_needs_devices_transfer_from_a_role_at_both_the_current_and_the_new_tenant()
    {
        var (from, to, deviceId) = await TwoSitesAsync();
        using var onlyFrom = await PersonAsync(from, ("Technician", from), ("Viewer", to));
        using var onlyTo = await PersonAsync(to, ("Viewer", from), ("Technician", to));
        using var viewer = await PersonAsync(from, ("Viewer", from), ("Viewer", to));

        // The destination is looked up like any tenant the request names: one the caller can't transfer into is missing.
        var intoUnreachable = await TransferAsync(onlyFrom, deviceId, to);
        // A device the caller can't transfer reads as missing.
        var outOfUnreachable = await TransferAsync(onlyTo, deviceId, to);
        var byViewer = await TransferAsync(viewer, deviceId, to);

        Assert.AreEqual(HttpStatusCode.BadRequest, intoUnreachable.StatusCode);
        Assert.AreEqual("application/problem+json", intoUnreachable.Content.Headers.ContentType?.MediaType);
        Assert.Contains("\"TenantId\"", await intoUnreachable.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NotFound, outOfUnreachable.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, byViewer.StatusCode);
        Assert.AreEqual(from, (await GetDeviceAsync(deviceId)).TenantId);
    }

    [TestMethod]
    public async Task Transfer_answers_404_for_a_missing_device_and_400_for_a_missing_or_absent_tenant()
    {
        var (from, _, deviceId) = await TwoSitesAsync();

        var missingDevice = await TransferAsync(_superadministrator, Guid.NewGuid(), from);
        var missingTenant = await TransferAsync(_superadministrator, deviceId, Guid.NewGuid());
        var noTenant = await _superadministrator.PostAsJsonAsync($"api/v1/devices/{deviceId}:transfer", new { }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, missingDevice.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, missingTenant.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, noTenant.StatusCode);
        Assert.AreEqual(from, (await GetDeviceAsync(deviceId)).TenantId);
    }

    [TestMethod]
    public async Task Editing_a_device_with_devices_write_cant_change_its_tenant()
    {
        var (from, to, deviceId) = await TwoSitesAsync();
        using var technician = await PersonAsync(from, ("Technician", from), ("Technician", to));
        using var patch = new StringContent($$"""{ "tenantId": "{{to}}" }""", System.Text.Encoding.UTF8, "application/merge-patch+json");

        var response = await technician.PatchAsync($"api/v1/devices/{deviceId}", patch, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(from, (await GetDeviceAsync(deviceId)).TenantId);
    }

    [TestMethod]
    public async Task A_transfer_is_audited_where_both_sides_can_read_it()
    {
        var (from, to, deviceId) = await TwoSitesAsync();
        var technician = await SignedUpAsync(from, ("Technician", from), ("Technician", to));
        using var technicianClient = await _factory.CreatePersonClientAsync(technician);
        using var oldAdministrator = await PersonAsync(from, ("Administrator", from));
        using var newAdministrator = await PersonAsync(to, ("Administrator", to));

        Assert.AreEqual(HttpStatusCode.OK, (await TransferAsync(technicianClient, deviceId, to)).StatusCode);

        var left = Assert.ContainsSingle(await TransfersAsync(oldAdministrator));
        var joined = Assert.ContainsSingle(await TransfersAsync(newAdministrator));
        Assert.AreEqual(from, left.TenantId);
        Assert.AreEqual($"to tenant {to}", left.Detail);
        Assert.AreEqual(to, joined.TenantId);
        Assert.AreEqual($"from tenant {from}", joined.Detail);
        foreach (var entry in new[] { left, joined })
        {
            Assert.AreEqual(deviceId, entry.DeviceId);
            Assert.AreEqual(AuditOutcome.Succeeded, entry.Outcome);
            Assert.AreEqual($"{technician.TenantId}:{technician.ObjectId}", entry.Actor.Subject);
        }
        // The old side can no longer see the device, so its name isn't resolved for them.
        Assert.IsNull(left.DeviceName);
        Assert.AreEqual("PC01", joined.DeviceName);
    }

    [TestMethod]
    public async Task Transfer_into_a_root_tree_holding_a_device_for_the_same_hardware_is_refused_with_409()
    {
        var uuid = Guid.Parse("4c4c4544-0042-3510-8058-b4c04f4d5a31");
        var rootA = await _factory.CreateTenantAsync(_superadministrator, "MspA");
        var siteA = await _factory.CreateTenantAsync(_superadministrator, "SiteA", parentTenantId: rootA.Id);
        var otherSiteA = await _factory.CreateTenantAsync(_superadministrator, "OtherSiteA", parentTenantId: rootA.Id);
        var rootB = await _factory.CreateTenantAsync(_superadministrator, "MspB");
        var siteB = await _factory.CreateTenantAsync(_superadministrator, "SiteB", parentTenantId: rootB.Id);
        var otherSiteB = await _factory.CreateTenantAsync(_superadministrator, "OtherSiteB", parentTenantId: rootB.Id);
        using var byUuid = await _factory.EnrollAgentAsync(siteA.Id, "PC01", r => r with { SmbiosUuid = uuid });
        using var bySerial = await _factory.EnrollAgentAsync(siteA.Id, "PC02",
            r => r with { Manufacturer = "Dell Inc.", SerialNumber = "5CG1234XYZ" });
        using var sameUuid = await _factory.EnrollAgentAsync(otherSiteB.Id, "B01", r => r with { SmbiosUuid = uuid });
        using var sameSerial = await _factory.EnrollAgentAsync(otherSiteB.Id, "B02",
            r => r with { Manufacturer = " DELL INC. ", SerialNumber = "5cg1234xyz" });
        // Neither the destination's tree nor the device already there needs to be within the caller's reach.
        using var technician = await PersonAsync(siteA.Id, ("Technician", siteA.Id), ("Technician", siteB.Id));

        var uuidClash = await TransferAsync(technician, byUuid.DeviceId, siteB.Id);
        var serialClash = await TransferAsync(technician, bySerial.DeviceId, siteB.Id);

        Assert.AreEqual(HttpStatusCode.Conflict, uuidClash.StatusCode);
        Assert.AreEqual("application/problem+json", uuidClash.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(sameUuid.DeviceId.ToString(), await uuidClash.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.Conflict, serialClash.StatusCode);
        Assert.AreEqual(siteA.Id, (await GetDeviceAsync(byUuid.DeviceId)).TenantId);
        Assert.AreEqual(siteA.Id, (await GetDeviceAsync(bySerial.DeviceId)).TenantId);

        // Within its own root tree, a device never clashes with itself.
        Assert.AreEqual(HttpStatusCode.OK, (await TransferAsync(_superadministrator, byUuid.DeviceId, otherSiteA.Id)).StatusCode);
    }

    [TestMethod]
    public async Task Transfer_to_the_devices_own_tenant_or_one_at_its_device_limit_is_refused_with_409()
    {
        var root = await _factory.CreateTenantAsync(_superadministrator, "Msp");
        var from = await _factory.CreateTenantAsync(_superadministrator, "Contoso", parentTenantId: root.Id);
        var full = await _factory.CreateTenantAsync(_superadministrator, "Fabrikam", maxDevices: 1, parentTenantId: root.Id);
        using var agent = await _factory.EnrollAgentAsync(from.Id, "PC01");
        using var occupant = await _factory.EnrollAgentAsync(full.Id, "PC02");

        var toItself = await TransferAsync(_superadministrator, agent.DeviceId, from.Id);
        var toFull = await TransferAsync(_superadministrator, agent.DeviceId, full.Id);

        Assert.AreEqual(HttpStatusCode.Conflict, toItself.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, toFull.StatusCode);
        Assert.AreEqual(from.Id, (await GetDeviceAsync(agent.DeviceId)).TenantId);
    }

    [TestMethod]
    public async Task Transfer_honours_if_match()
    {
        var (from, to, deviceId) = await TwoSitesAsync();
        var current = await _superadministrator.GetAsync($"api/v1/devices/{deviceId}", Ct);

        using var stale = new HttpRequestMessage(HttpMethod.Post, $"api/v1/devices/{deviceId}:transfer")
        {
            Content = JsonContent.Create(new TransferDeviceRequest { TenantId = to }, options: Signal9Json.Options),
        };
        stale.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"stale\""));
        var refused = await _superadministrator.SendAsync(stale, Ct);

        using var fresh = new HttpRequestMessage(HttpMethod.Post, $"api/v1/devices/{deviceId}:transfer")
        {
            Content = JsonContent.Create(new TransferDeviceRequest { TenantId = to }, options: Signal9Json.Options),
        };
        fresh.Headers.IfMatch.Add(current.Headers.ETag!);
        var accepted = await _superadministrator.SendAsync(fresh, Ct);

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, refused.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
        Assert.AreEqual(to, (await GetDeviceAsync(deviceId)).TenantId);
    }

    private async Task<List<AuditEntryDto>> TransfersAsync(HttpClient caller) =>
        [.. (await caller.GetFromJsonAsync<PagedResult<AuditEntryDto>>("api/v1/audit-entries?pageSize=100", Signal9Json.Options, Ct))!
            .Items.Where(e => e.Action == AuditActions.DeviceTransferred)];

    /// <summary>Msp with two client tenants, Contoso holding one device.</summary>
    private async Task<(Guid From, Guid To, Guid DeviceId)> TwoSitesAsync()
    {
        var root = await _factory.CreateTenantAsync(_superadministrator, "Msp");
        var from = await _factory.CreateTenantAsync(_superadministrator, "Contoso", parentTenantId: root.Id);
        var to = await _factory.CreateTenantAsync(_superadministrator, "Fabrikam", parentTenantId: root.Id);
        using var agent = await _factory.EnrollAgentAsync(from.Id, "PC01");
        return (from.Id, to.Id, agent.DeviceId);
    }

    private async Task<DeviceDto> GetDeviceAsync(Guid deviceId) =>
        (await _superadministrator.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{deviceId}", Signal9Json.Options, Ct))!;

    private static string[] HistoryPaths(TestAgent agent, CommandDto command, TelemetrySampleDto sample) =>
    [
        $"api/v1/devices/{agent.DeviceId}",
        $"api/v1/agents/{agent.Id}",
        $"api/v1/devices/{agent.DeviceId}/commands",
        $"api/v1/devices/{agent.DeviceId}/commands/{command.Id}",
        $"api/v1/devices/{agent.DeviceId}/telemetry",
        $"api/v1/devices/{agent.DeviceId}/telemetry/{sample.Id}",
    ];

    private async Task<HttpResponseMessage> TransferAsync(HttpClient caller, Guid deviceId, Guid tenantId) =>
        await caller.PostAsJsonAsync($"api/v1/devices/{deviceId}:transfer", new TransferDeviceRequest { TenantId = tenantId },
            Signal9Json.Options, Ct);

    private async Task<CommandDto> QueueCommandAsync(Guid deviceId)
    {
        var response = await _superadministrator.PostAsJsonAsync($"api/v1/devices/{deviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<TelemetrySampleDto> SendTelemetryAsync(TestAgent agent)
    {
        var response = await agent.Client.PostAsJsonAsync($"api/v1/agents/{agent.Id}/telemetry",
            new CreateTelemetrySampleRequest { CollectedAt = _factory.Time.GetUtcNow(), CpuPercent = 10 }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<TelemetrySampleDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<Guid[]> DeviceIdsAsync(HttpClient caller) =>
        [.. (await caller.GetFromJsonAsync<PagedResult<DeviceDto>>("api/v1/devices?pageSize=100", Signal9Json.Options, Ct))!.Items.Select(d => d.Id)];

    private async Task<PagedResult<AgentDto>> AgentsAsync(HttpClient caller, Guid tenantId) =>
        (await caller.GetFromJsonAsync<PagedResult<AgentDto>>($"api/v1/tenants/{tenantId}/agents", Signal9Json.Options, Ct))!;

    /// <summary>A newly signed-in person, owned by <paramref name="owner"/>, holding these role assignments.</summary>
    private async Task<HttpClient> PersonAsync(Guid owner, params (string Role, Guid Tenant)[] assignments) =>
        await _factory.CreatePersonClientAsync(await SignedUpAsync(owner, assignments));

    /// <summary>A new user owned by <paramref name="owner"/>, holding these role assignments, who can sign in.</summary>
    private async Task<EntraUser> SignedUpAsync(Guid owner, params (string Role, Guid Tenant)[] assignments)
    {
        var person = EntraUser.WithMfa();
        var response = await _superadministrator.PostAsJsonAsync("api/v1/users", new CreateUserRequest
        {
            TenantId = owner,
            EntraTenantId = person.TenantId,
            EntraObjectId = person.ObjectId,
            DisplayName = "Alex Wilber",
        }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var user = (await response.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;
        var roles = (await _superadministrator.GetFromJsonAsync<PagedResult<RoleDto>>("api/v1/roles", Signal9Json.Options, Ct))!.Items;
        foreach (var (role, tenant) in assignments)
        {
            var assigned = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenant}/role-assignments",
                new CreateRoleAssignmentRequest { UserId = user.Id, RoleId = roles.Single(r => r.Name == role).Id }, Signal9Json.Options, Ct);
            Assert.AreEqual(HttpStatusCode.Created, assigned.StatusCode, await assigned.Content.ReadAsStringAsync(Ct));
        }
        return person;
    }

    public void Dispose()
    {
        _superadministrator.Dispose();
        _factory.Dispose();
    }
}
