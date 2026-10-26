using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Telemetry;
using OSI.Signal9.Server.Data;
using Scopes = OSI.Signal9.Server.Identity.Scopes;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Identification (#131, stories 103-107): a reinstalled agent rejoins its existing device when its hardware says so
/// unambiguously, and anything unclear waits in the Needs-decision queue for a person.
/// </summary>
[TestClass]
public sealed class AgentIdentificationTests : IDisposable
{
    private static readonly Guid Uuid = Guid.Parse("4c4c4544-0042-3510-8058-b4c04f4d5a31");

    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly TestContext _testContext;

    public AgentIdentificationTests(TestContext testContext)
    {
        _testContext = testContext;
        // The fixture's own client stands in for a Superadministrator.
        _client = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    private static Func<EnrollAgentRequest, EnrollAgentRequest> Hardware(
        Guid? uuid = null, string? manufacturer = null, string? serial = null) =>
        request => request with { SmbiosUuid = uuid, Manufacturer = manufacturer, SerialNumber = serial };

    private async Task RevokeAsync(Guid agentId) =>
        (await _client.PostAsync($"api/v1/agents/{agentId}:revoke", null, Ct)).EnsureSuccessStatusCode();

    [TestMethod]
    public async Task A_reinstalled_agent_rejoins_its_device_by_SMBIOS_UUID_once_the_previous_agent_is_revoked()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, "PC01", Hardware(Uuid));
        await RevokeAsync(first.Id);

        using var second = await _factory.EnrollAgentAsync(tenant.Id, "RENAMED", Hardware(Uuid));

        Assert.AreEqual(first.DeviceId, second.DeviceId);
        Assert.IsTrue(second.Agent.IsActive);
        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{first.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual(second.Id, device!.ActiveAgentId);
    }

    [TestMethod]
    public async Task A_reinstalled_agent_rejoins_its_device_by_manufacturer_and_serial_however_they_are_spaced_or_cased()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(manufacturer: "Dell Inc.", serial: "5CG1234XYZ"));
        await RevokeAsync(first.Id);

        using var second = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(manufacturer: " DELL INC. ", serial: "5cg1234xyz "));

        Assert.AreEqual(first.DeviceId, second.DeviceId);
    }

    [TestMethod]
    public async Task A_serial_alone_without_its_manufacturer_never_matches()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(manufacturer: "Dell Inc.", serial: "5CG1234XYZ"));
        await RevokeAsync(first.Id);

        using var other = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(manufacturer: "HP", serial: "5CG1234XYZ"));
        using var none = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(serial: "5CG1234XYZ"));

        Assert.AreNotEqual(first.DeviceId, other.DeviceId);
        Assert.AreNotEqual(first.DeviceId, none.DeviceId);
    }

    [TestMethod]
    [DataRow("00000000-0000-0000-0000-000000000000", null, null, DisplayName = "Zeroed UUID")]
    [DataRow("ffffffff-ffff-ffff-ffff-ffffffffffff", null, null, DisplayName = "All-FF UUID")]
    [DataRow("03000200-0400-0500-0006-000700080009", null, null, DisplayName = "AMI placeholder UUID")]
    [DataRow(null, "To Be Filled By O.E.M.", "To Be Filled By O.E.M.", DisplayName = "OEM placeholder serial")]
    [DataRow(null, "LENOVO", "Default string", DisplayName = "Default string serial")]
    [DataRow(null, "LENOVO", "0000000000", DisplayName = "Zeroed serial")]
    [DataRow(null, "System manufacturer", "PF12345", DisplayName = "Placeholder manufacturer")]
    [DataRow(null, "LENOVO", "   ", DisplayName = "Blank serial")]
    public async Task Zeroed_or_known_junk_identifiers_never_match(string? uuid, string? manufacturer, string? serial)
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var hardware = Hardware(uuid is null ? null : Guid.Parse(uuid), manufacturer, serial);
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: hardware);
        await RevokeAsync(first.Id);

        using var second = await _factory.EnrollAgentAsync(tenant.Id, hardware: hardware);

        Assert.AreNotEqual(first.DeviceId, second.DeviceId);
        Assert.AreEqual(AgentStatus.Active, second.Agent.Status);
    }

    [TestMethod]
    public async Task Machine_name_never_influences_matching()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, "PC01");
        await RevokeAsync(first.Id);
        using var withHardware = await _factory.EnrollAgentAsync(tenant.Id, "PC02", Hardware(Uuid));

        // Same name, no hardware match: a new device. Same hardware, another name: the same device.
        using var sameName = await _factory.EnrollAgentAsync(tenant.Id, "PC01");
        await RevokeAsync(withHardware.Id);
        using var renamed = await _factory.EnrollAgentAsync(tenant.Id, "PC01", Hardware(Uuid));

        Assert.AreNotEqual(first.DeviceId, sameName.DeviceId);
        Assert.AreEqual(AgentStatus.Active, sameName.Agent.Status);
        Assert.AreEqual(withHardware.DeviceId, renamed.DeviceId);
    }

    [TestMethod]
    public async Task Matching_hardware_while_the_previous_agent_is_still_active_needs_a_decision()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, "PC01", Hardware(Uuid));

        using var second = await _factory.EnrollAgentAsync(tenant.Id, "PC01-NEW", Hardware(Uuid));

        Assert.AreEqual(AgentStatus.NeedsDecision, second.Agent.Status);
        Assert.IsNull(second.Agent.DeviceId);
        Assert.IsFalse(second.Agent.IsActive);
        Assert.AreEqual(tenant.Id, second.Agent.TenantId);
        Assert.AreEqual("PC01-NEW", second.Agent.MachineName);
        var queue = await _client.GetFromJsonAsync<PagedResult<AgentDto>>(
            $"api/v1/tenants/{tenant.Id}/agents?status=NeedsDecision", Signal9Json.Options, Ct);
        Assert.AreEqual(second.Id, Assert.ContainsSingle(queue!.Items).Id);
        var candidates = await _client.GetFromJsonAsync<PagedResult<DeviceDto>>(
            $"api/v1/agents/{second.Id}/candidate-devices", Signal9Json.Options, Ct);
        Assert.AreEqual(first.DeviceId, Assert.ContainsSingle(candidates!.Items).Id);
        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{first.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual(first.Id, device!.ActiveAgentId);
    }

    [TestMethod]
    public async Task Several_matching_devices_need_a_decision_even_when_their_agents_are_revoked()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        using var clone = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        await IdentifyAsync(clone.Id, IdentificationDecision.KeepBoth);
        await RevokeAsync(first.Id);
        await RevokeAsync(clone.Id);

        using var third = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));

        Assert.AreEqual(AgentStatus.NeedsDecision, third.Agent.Status);
        var candidates = await _client.GetFromJsonAsync<PagedResult<DeviceDto>>(
            $"api/v1/agents/{third.Id}/candidate-devices", Signal9Json.Options, Ct);
        Assert.HasCount(2, candidates!.Items);
    }

    [TestMethod]
    public async Task An_agent_awaiting_a_decision_checks_in_and_hears_its_doorbell_but_gets_no_work_or_telemetry()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        using var pending = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));

        var heartbeat = await pending.Client.PutAsJsonAsync($"api/v1/agents/{pending.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        var work = await pending.Client.GetFromJsonAsync<PagedResult<CommandDto>>($"api/v1/agents/{pending.Id}/pending-commands", Signal9Json.Options, Ct);
        var telemetry = await pending.Client.PostAsJsonAsync($"api/v1/agents/{pending.Id}/telemetry",
            new CreateTelemetrySampleRequest { CollectedAt = _factory.Time.GetUtcNow() }, Signal9Json.Options, Ct);
        await using var hub = BuildHub(pending);
        await hub.StartAsync(Ct);
        await Assert.ThrowsExactlyAsync<HubException>(() => hub.InvokeAsync("Ping", Ct));

        Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode, await heartbeat.Content.ReadAsStringAsync(Ct));
        Assert.IsFalse((await heartbeat.Content.ReadFromJsonAsync<HeartbeatResponse>(Signal9Json.Options, Ct))!.HasPendingWork);
        Assert.IsEmpty(work!.Items);
        Assert.AreEqual(HttpStatusCode.Conflict, telemetry.StatusCode);
        Assert.AreEqual("application/problem+json", telemetry.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HubConnectionState.Connected, hub.State);
    }

    [TestMethod]
    public async Task Reinstalled_rebinds_the_agent_to_the_device_and_revokes_the_agent_it_replaces()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        var command = await QueueCommandAsync(first.DeviceId);
        using var second = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));

        var decided = await IdentifyAsync(second.Id, IdentificationDecision.Reinstalled, first.DeviceId);

        Assert.AreEqual(first.DeviceId, decided.DeviceId);
        Assert.IsTrue(decided.IsActive);
        Assert.AreEqual(AgentStatus.Active, decided.Status);
        var previous = await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{first.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual(AgentStatus.Revoked, previous!.Status);
        var refused = await first.Client.PutAsJsonAsync($"api/v1/agents/{first.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Unauthorized, refused.StatusCode);
        // The device's waiting work goes to the agent now on it.
        var work = await second.Client.GetFromJsonAsync<PagedResult<CommandDto>>($"api/v1/agents/{second.Id}/pending-commands", Signal9Json.Options, Ct);
        Assert.AreEqual(command.Id, Assert.ContainsSingle(work!.Items).Id);
        var queue = await _client.GetFromJsonAsync<PagedResult<AgentDto>>(
            $"api/v1/tenants/{tenant.Id}/agents?status=NeedsDecision", Signal9Json.Options, Ct);
        Assert.IsEmpty(queue!.Items);
    }

    [TestMethod]
    public async Task Keep_both_gives_the_agent_a_new_device_and_leaves_the_existing_one_alone()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, "PC01", Hardware(Uuid));
        using var second = await _factory.EnrollAgentAsync(tenant.Id, "PC02", Hardware(Uuid));

        var decided = await IdentifyAsync(second.Id, IdentificationDecision.KeepBoth);

        Assert.IsNotNull(decided.DeviceId);
        Assert.AreNotEqual(first.DeviceId, decided.DeviceId);
        Assert.AreEqual(AgentStatus.Active, decided.Status);
        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{decided.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual("PC02", device!.MachineName);
        Assert.AreEqual(tenant.Id, device.TenantId);
        Assert.AreEqual(second.Id, device.ActiveAgentId);
        var existing = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{first.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual(first.Id, existing!.ActiveAgentId);
    }

    [TestMethod]
    public async Task Keep_both_at_the_device_limit_is_refused()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso", maxDevices: 1);
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        using var second = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));

        var refused = await _client.PostAsJsonAsync($"api/v1/agents/{second.Id}:identify",
            new IdentifyAgentRequest { Decision = IdentificationDecision.KeepBoth }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.AreEqual("application/problem+json", refused.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task Identify_answers_each_refusal_with_its_status_code()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var other = await _factory.CreateTenantAsync(_client, "Fabrikam");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        using var unrelated = await _factory.EnrollAgentAsync(other.Id);
        using var pending = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        var url = $"api/v1/agents/{pending.Id}:identify";
        Task<HttpResponseMessage> Post(string body, HttpClient? client = null) =>
            (client ?? _client).PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        var noDecision = await Post("{}");
        var noDevice = await Post("""{ "decision": "Reinstalled" }""");
        var deviceForKeepBoth = await Post($$"""{ "decision": "KeepBoth", "deviceId": "{{first.DeviceId}}" }""");
        var notCandidate = await Post($$"""{ "decision": "Reinstalled", "deviceId": "{{unrelated.DeviceId}}" }""");
        var unknown = await _client.PostAsJsonAsync($"api/v1/agents/{Guid.NewGuid()}:identify",
            new IdentifyAgentRequest { Decision = IdentificationDecision.KeepBoth }, Signal9Json.Options, Ct);
        using var reader = await _factory.CreateAssignedClientAsync([tenant.Id], Scopes.Devices.Read);
        var withoutScope = await Post("""{ "decision": "KeepBoth" }""", reader);
        using var stale = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent("""{ "decision": "KeepBoth" }""", System.Text.Encoding.UTF8, "application/json"),
        };
        stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"stale\""));
        var preconditionFailed = await _client.SendAsync(stale, Ct);
        var notPending = await _client.PostAsJsonAsync($"api/v1/agents/{first.Id}:identify",
            new IdentifyAgentRequest { Decision = IdentificationDecision.KeepBoth }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, noDecision.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, noDevice.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, deviceForKeepBoth.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, notCandidate.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, withoutScope.StatusCode);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, preconditionFailed.StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, notPending.StatusCode);
        Assert.AreEqual(AgentStatus.NeedsDecision,
            (await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{pending.Id}", Signal9Json.Options, Ct))!.Status);
    }

    [TestMethod]
    public async Task Callers_outside_the_tenant_cannot_see_an_agent_awaiting_a_decision_or_its_candidates()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var other = await _factory.CreateTenantAsync(_client, "Fabrikam");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        using var pending = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        using var outsider = await _factory.CreateAssignedClientAsync([other.Id], Scopes.Devices.Read, Scopes.Devices.Write);
        using var insider = await _factory.CreateAssignedClientAsync([tenant.Id], Scopes.Devices.Read);

        Assert.AreEqual(HttpStatusCode.NotFound, (await outsider.GetAsync($"api/v1/agents/{pending.Id}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await outsider.GetAsync($"api/v1/agents/{pending.Id}/candidate-devices", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await outsider.PostAsJsonAsync($"api/v1/agents/{pending.Id}:identify",
            new IdentifyAgentRequest { Decision = IdentificationDecision.KeepBoth }, Signal9Json.Options, Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await insider.GetAsync($"api/v1/agents/{pending.Id}", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Revoke_then_reinstall_with_matching_hardware_awaits_approval_and_approving_delivers_work()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        await RevokeAsync(first.Id);
        var command = await QueueCommandAsync(first.DeviceId);

        using var second = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));

        Assert.AreEqual(first.DeviceId, second.DeviceId);
        Assert.AreEqual(AgentStatus.AwaitingApproval, second.Agent.Status);
        var held = await second.Client.GetFromJsonAsync<PagedResult<CommandDto>>($"api/v1/agents/{second.Id}/pending-commands", Signal9Json.Options, Ct);
        Assert.IsEmpty(held!.Items);

        var approved = await _client.PostAsync($"api/v1/agents/{second.Id}:approve", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode, await approved.Content.ReadAsStringAsync(Ct));
        var work = await second.Client.GetFromJsonAsync<PagedResult<CommandDto>>($"api/v1/agents/{second.Id}/pending-commands", Signal9Json.Options, Ct);
        Assert.AreEqual(command.Id, Assert.ContainsSingle(work!.Items).Id);
        var result = await second.Client.PutAsJsonAsync($"{command.Url.TrimStart('/')}/result",
            new CommandResultRequest { Succeeded = true, Output = "ok" }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode, await result.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Telemetry_and_command_history_persist_across_the_reinstall()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(manufacturer: "Dell Inc.", serial: "5CG1234XYZ"));
        var command = await QueueCommandAsync(first.DeviceId);
        (await first.Client.PutAsJsonAsync($"{command.Url.TrimStart('/')}/result",
            new CommandResultRequest { Succeeded = true }, Signal9Json.Options, Ct)).EnsureSuccessStatusCode();
        (await first.Client.PostAsJsonAsync($"api/v1/agents/{first.Id}/telemetry",
            new CreateTelemetrySampleRequest { CollectedAt = _factory.Time.GetUtcNow(), CpuPercent = 12 }, Signal9Json.Options, Ct)).EnsureSuccessStatusCode();
        await RevokeAsync(first.Id);

        using var second = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(manufacturer: "Dell Inc.", serial: "5CG1234XYZ"));
        (await _client.PostAsync($"api/v1/agents/{second.Id}:approve", null, Ct)).EnsureSuccessStatusCode();
        (await second.Client.PostAsJsonAsync($"api/v1/agents/{second.Id}/telemetry",
            new CreateTelemetrySampleRequest { CollectedAt = _factory.Time.GetUtcNow(), CpuPercent = 34 }, Signal9Json.Options, Ct)).EnsureSuccessStatusCode();

        Assert.AreEqual(first.DeviceId, second.DeviceId);
        var commands = await _client.GetFromJsonAsync<PagedResult<CommandDto>>($"api/v1/devices/{first.DeviceId}/commands", Signal9Json.Options, Ct);
        Assert.AreEqual(CommandStatus.Completed, Assert.ContainsSingle(commands!.Items.Where(c => c.Id == command.Id)).Status);
        var samples = await _client.GetFromJsonAsync<PagedResult<TelemetrySampleDto>>($"api/v1/devices/{first.DeviceId}/telemetry", Signal9Json.Options, Ct);
        CollectionAssert.AreEquivalent(new double?[] { 12, 34 }, samples!.Items.Select(s => s.CpuPercent).ToList());
    }

    [TestMethod]
    public async Task Identification_outcomes_and_decisions_are_audited_without_hardware_identifiers()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var created = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid, "Dell Inc.", "5CG1234XYZ"));
        await RevokeAsync(created.Id);
        using var matched = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid, "Dell Inc.", "5CG1234XYZ"));
        using var pending = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        await IdentifyAsync(pending.Id, IdentificationDecision.Reinstalled, matched.DeviceId);
        using var clone = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(manufacturer: "Dell Inc.", serial: "5CG1234XYZ"));
        await IdentifyAsync(clone.Id, IdentificationDecision.KeepBoth);
        var cloneDevice = (await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{clone.Id}", Signal9Json.Options, Ct))!.DeviceId;

        var entries = (await _client.GetFromJsonAsync<PagedResult<AuditEntryDto>>("api/v1/audit-entries?pageSize=100", Signal9Json.Options, Ct))!.Items;

        string?[] Identified(Guid agentId) => [.. entries
            .Where(e => e.TargetId == agentId && e.Action is AuditActions.AgentIdentified or AuditActions.AgentIdentificationPending)
            .OrderBy(e => e.OccurredAt).Select(e => $"{e.Action} {e.Detail} {e.DeviceId}")];
        CollectionAssert.AreEqual(new[] { $"agent.identified new-device {created.DeviceId}" }, Identified(created.Id));
        CollectionAssert.AreEqual(new[] { $"agent.identified matched smbios-uuid {created.DeviceId}" }, Identified(matched.Id));
        CollectionAssert.AreEquivalent(new[] { "agent.identification-pending needs-decision ", $"agent.identified reinstalled {created.DeviceId}" }, Identified(pending.Id));
        CollectionAssert.AreEquivalent(new[] { "agent.identification-pending needs-decision ", $"agent.identified keep-both {cloneDevice}" }, Identified(clone.Id));
        var replaced = Assert.ContainsSingle(entries.Where(e => e.Action == AuditActions.AgentRevoked && e.TargetId == matched.Id && e.Detail != null));
        Assert.AreEqual($"replaced by agent {pending.Id}", replaced.Detail);
        Assert.IsTrue(entries.Where(e => e.Detail?.StartsWith("matched", StringComparison.Ordinal) == true)
            .All(e => e.Actor.Kind == AuditActorKind.Anonymous), "Automatic matches happen at enrollment, with no caller.");
        Assert.IsFalse(entries.Any(e => e.Detail?.Contains("5CG1234XYZ", StringComparison.OrdinalIgnoreCase) == true
            || e.Detail?.Contains(Uuid.ToString(), StringComparison.OrdinalIgnoreCase) == true));
    }

    [TestMethod]
    public async Task The_same_hardware_in_two_root_trees_makes_two_independent_devices()
    {
        var contoso = await _factory.CreateTenantAsync(_client, "Contoso");
        var fabrikam = await _factory.CreateTenantAsync(_client, "Fabrikam");
        using var inContoso = await _factory.EnrollAgentAsync(contoso.Id, hardware: Hardware(Uuid, "Dell Inc.", "5CG1234XYZ"));

        // Contoso's agent is still active, so a match in the same tree would need a decision; another tree never matches.
        using var inFabrikam = await _factory.EnrollAgentAsync(fabrikam.Id, hardware: Hardware(Uuid, "Dell Inc.", "5CG1234XYZ"));
        await RevokeAsync(inContoso.Id);
        using var again = await _factory.EnrollAgentAsync(fabrikam.Id, hardware: Hardware(Uuid, "Dell Inc.", "5CG1234XYZ"));

        Assert.AreEqual(AgentStatus.Active, inFabrikam.Agent.Status);
        Assert.AreNotEqual(inContoso.DeviceId, inFabrikam.DeviceId);
        Assert.AreEqual(fabrikam.Id, inFabrikam.TenantId);
        // Within Fabrikam its device is the only match, but its agent is active: a decision, never Contoso's device.
        Assert.AreEqual(AgentStatus.NeedsDecision, again.Agent.Status);
        var candidates = await _client.GetFromJsonAsync<PagedResult<DeviceDto>>(
            $"api/v1/agents/{again.Id}/candidate-devices", Signal9Json.Options, Ct);
        Assert.AreEqual(inFabrikam.DeviceId, Assert.ContainsSingle(candidates!.Items).Id);
    }

    [TestMethod]
    public async Task Matching_spans_the_root_tree_and_a_rebound_agent_stays_with_its_device_in_another_subtree()
    {
        var msp = await _factory.CreateTenantAsync(_client, "Msp");
        var east = await _factory.CreateTenantAsync(_client, "East", parentTenantId: msp.Id);
        var west = await _factory.CreateTenantAsync(_client, "West", parentTenantId: msp.Id);
        using var inEast = await _factory.EnrollAgentAsync(east.Id, hardware: Hardware(Uuid));
        await RevokeAsync(inEast.Id);

        using var fromWest = await _factory.EnrollAgentAsync(west.Id, hardware: Hardware(Uuid));

        Assert.AreEqual(inEast.DeviceId, fromWest.DeviceId);
        Assert.AreEqual(east.Id, fromWest.TenantId, "The device stays in its tenant, and its agent belongs there.");
        Assert.AreEqual(AgentStatus.AwaitingApproval, fromWest.Agent.Status);
        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{inEast.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual(east.Id, device!.TenantId);
        Assert.AreEqual(fromWest.Id, device.ActiveAgentId);
    }

    [TestMethod]
    public async Task A_match_in_another_subtree_needs_a_decision_whose_candidates_are_those_the_caller_can_see()
    {
        var msp = await _factory.CreateTenantAsync(_client, "Msp");
        var east = await _factory.CreateTenantAsync(_client, "East", parentTenantId: msp.Id);
        var west = await _factory.CreateTenantAsync(_client, "West", parentTenantId: msp.Id);
        using var inEast = await _factory.EnrollAgentAsync(east.Id, hardware: Hardware(Uuid));
        using var fromWest = await _factory.EnrollAgentAsync(west.Id, hardware: Hardware(Uuid));
        using var westAdmin = await _factory.CreateAssignedClientAsync([west.Id], Scopes.Devices.Read, Scopes.Devices.Write);
        using var mspAdmin = await _factory.CreateAssignedClientAsync([msp.Id], Scopes.Devices.Read, Scopes.Devices.Write);

        Assert.AreEqual(AgentStatus.NeedsDecision, fromWest.Agent.Status);
        Assert.AreEqual(west.Id, fromWest.TenantId);
        var seenFromWest = await westAdmin.GetFromJsonAsync<PagedResult<DeviceDto>>(
            $"api/v1/agents/{fromWest.Id}/candidate-devices", Signal9Json.Options, Ct);
        Assert.IsEmpty(seenFromWest!.Items);
        var refused = await westAdmin.PostAsJsonAsync($"api/v1/agents/{fromWest.Id}:identify",
            new IdentifyAgentRequest { Decision = IdentificationDecision.Reinstalled, DeviceId = inEast.DeviceId }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        var seenFromMsp = await mspAdmin.GetFromJsonAsync<PagedResult<DeviceDto>>(
            $"api/v1/agents/{fromWest.Id}/candidate-devices", Signal9Json.Options, Ct);
        Assert.AreEqual(inEast.DeviceId, Assert.ContainsSingle(seenFromMsp!.Items).Id);

        var identified = await mspAdmin.PostAsJsonAsync($"api/v1/agents/{fromWest.Id}:identify",
            new IdentifyAgentRequest { Decision = IdentificationDecision.Reinstalled, DeviceId = inEast.DeviceId }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.OK, identified.StatusCode, await identified.Content.ReadAsStringAsync(Ct));
        var rebound = (await identified.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(inEast.DeviceId, rebound.DeviceId);
        Assert.AreEqual(east.Id, rebound.TenantId);
        var replaced = await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{inEast.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual(AgentStatus.Revoked, replaced!.Status);
    }

    [TestMethod]
    public async Task Concurrent_enrollments_of_one_machine_from_sibling_subtrees_make_one_device_and_a_decision()
    {
        var msp = await _factory.CreateTenantAsync(_client, "Msp");
        var east = await _factory.CreateTenantAsync(_client, "East", parentTenantId: msp.Id);
        var west = await _factory.CreateTenantAsync(_client, "West", parentTenantId: msp.Id);
        var (inEast, inWest) = (await _factory.IssueEnrollmentTokenAsync(east.Id), await _factory.IssueEnrollmentTokenAsync(west.Id));
        // Both enrollments look for matching devices together, unless enrollment serializes them first.
        using var racing = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<Signal9DbContext>(options =>
                options.AddInterceptors(new Rendezvous("FROM \"Devices\"", parties: 2, TimeSpan.FromSeconds(2))))));
        async Task<AgentDto> EnrollAsync(string secret)
        {
            using var anonymous = racing.Server.CreateClient();
            anonymous.BaseAddress = Signal9ApiFactory.HttpsLocalhost;
            using var certificate = TestCertificates.AgentCertificate();
            var response = await anonymous.PostAsJsonAsync("api/v1/agents",
                Hardware(Uuid)(Signal9ApiFactory.Enrollment(secret, certificate)), Signal9Json.Options, Ct);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;
        }

        var agents = await Task.WhenAll(EnrollAsync(inEast.Secret!), EnrollAsync(inWest.Secret!));

        CollectionAssert.AreEquivalent(new[] { AgentStatus.Active, AgentStatus.NeedsDecision }, agents.Select(a => a.Status).ToArray());
        var devices = 0;
        foreach (var tenant in new[] { east, west })
            devices += (await _client.GetFromJsonAsync<PagedResult<DeviceDto>>($"api/v1/devices?tenantId={tenant.Id}", Signal9Json.Options, Ct))!.Items.Count;
        Assert.AreEqual(1, devices);
    }

    /// <summary>Reports <see cref="Uuid"/>, and an operating system installed <paramref name="hoursAgo"/> hours ago.</summary>
    private Func<EnrollAgentRequest, EnrollAgentRequest> InstalledHoursAgo(double? hoursAgo) =>
        request => Hardware(Uuid)(request) with
        {
            OsInstalledAt = hoursAgo is { } hours ? _factory.Time.GetUtcNow().AddHours(-hours) : null,
        };

    [TestMethod]
    public async Task A_fresh_os_rebinds_to_the_one_match_but_is_held_and_leaves_the_working_agent_alone_until_approved()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var working = await _factory.EnrollAgentAsync(tenant.Id, "PC01", Hardware(Uuid));
        var command = await QueueCommandAsync(working.DeviceId);

        using var fresh = await _factory.EnrollAgentAsync(tenant.Id, "REIMAGED", InstalledHoursAgo(2));

        Assert.AreEqual(working.DeviceId, fresh.DeviceId);
        Assert.AreEqual(AgentStatus.AwaitingApproval, fresh.Agent.Status, "Held even though the tenant doesn't hold new agents.");
        Assert.IsFalse(fresh.Agent.IsActive);
        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{working.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual(working.Id, device!.ActiveAgentId);
        Assert.AreEqual("PC01", device.MachineName, "An unapproved claim doesn't change the device.");
        var stillWorking = await working.Client.GetFromJsonAsync<PagedResult<CommandDto>>(
            $"api/v1/agents/{working.Id}/pending-commands", Signal9Json.Options, Ct);
        Assert.AreEqual(command.Id, Assert.ContainsSingle(stillWorking!.Items).Id);
        // The held agent checks in and hears its doorbell, but gets no work and can't write to the device.
        var heartbeat = await fresh.Client.PutAsJsonAsync($"api/v1/agents/{fresh.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode, await heartbeat.Content.ReadAsStringAsync(Ct));
        var held = await fresh.Client.GetFromJsonAsync<PagedResult<CommandDto>>($"api/v1/agents/{fresh.Id}/pending-commands", Signal9Json.Options, Ct);
        Assert.IsEmpty(held!.Items);
        var telemetry = await fresh.Client.PostAsJsonAsync($"api/v1/agents/{fresh.Id}/telemetry",
            new CreateTelemetrySampleRequest { CollectedAt = _factory.Time.GetUtcNow() }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Conflict, telemetry.StatusCode);
        await using (var hub = BuildHub(fresh))
        {
            await hub.StartAsync(Ct);
            await Assert.ThrowsExactlyAsync<HubException>(() => hub.InvokeAsync("Ping", Ct));
            Assert.AreEqual(HubConnectionState.Connected, hub.State);
        }

        var approved = await _client.PostAsync($"api/v1/agents/{fresh.Id}:approve", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode, await approved.Content.ReadAsStringAsync(Ct));
        var active = (await approved.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(AgentStatus.Active, active.Status);
        Assert.IsTrue(active.IsActive);
        var replaced = await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{working.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual(AgentStatus.Revoked, replaced!.Status);
        device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{working.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual(fresh.Id, device!.ActiveAgentId);
        Assert.AreEqual("REIMAGED", device.MachineName);
        var work = await fresh.Client.GetFromJsonAsync<PagedResult<CommandDto>>($"api/v1/agents/{fresh.Id}/pending-commands", Signal9Json.Options, Ct);
        Assert.AreEqual(command.Id, Assert.ContainsSingle(work!.Items).Id);
        var entries = (await _client.GetFromJsonAsync<PagedResult<AuditEntryDto>>("api/v1/audit-entries?pageSize=100", Signal9Json.Options, Ct))!.Items;
        var identified = Assert.ContainsSingle(entries.Where(e => e.TargetId == fresh.Id && e.Action == AuditActions.AgentIdentified));
        Assert.AreEqual("matched smbios-uuid fresh-os", identified.Detail);
        Assert.AreEqual(working.DeviceId, identified.DeviceId);
        Assert.AreEqual($"replaced by agent {fresh.Id}", Assert.ContainsSingle(entries
            .Where(e => e.TargetId == working.Id && e.Action == AuditActions.AgentRevoked)).Detail);
    }

    [TestMethod]
    public async Task Rejecting_a_fresh_os_claim_leaves_the_working_agent_and_its_device_alone()
    {
        var msp = await _factory.CreateTenantAsync(_client, "Msp");
        var east = await _factory.CreateTenantAsync(_client, "East", parentTenantId: msp.Id);
        var west = await _factory.CreateTenantAsync(_client, "West", parentTenantId: msp.Id);
        using var working = await _factory.EnrollAgentAsync(east.Id, "PC01", Hardware(Uuid));
        using var fresh = await _factory.EnrollAgentAsync(west.Id, "FORGED", InstalledHoursAgo(1));
        using var eastAdmin = await _factory.CreateAssignedClientAsync([east.Id], Scopes.Devices.Read, Scopes.Devices.Write);

        var revoked = await eastAdmin.PostAsync($"api/v1/agents/{fresh.Id}:revoke", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode, await revoked.Content.ReadAsStringAsync(Ct));
        var agent = await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{working.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual(AgentStatus.Active, agent!.Status);
        Assert.IsTrue(agent.IsActive);
        var heartbeat = await working.Client.PutAsJsonAsync($"api/v1/agents/{working.Id}/heartbeat", new HeartbeatRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, heartbeat.StatusCode);
        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{working.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual("PC01", device!.MachineName);
        Assert.IsFalse(device.RequiresAgentApproval);
    }

    [TestMethod]
    [DataRow(25.0, DisplayName = "Installed 25 hours ago")]
    [DataRow(24.0, DisplayName = "Installed exactly 24 hours ago")]
    [DataRow(-1.0, DisplayName = "Installed in the future")]
    [DataRow(null, DisplayName = "Install time unknown")]
    public async Task An_os_not_installed_within_the_last_day_needs_a_decision_as_before(double? hoursAgo)
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var working = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));

        using var reinstalled = await _factory.EnrollAgentAsync(tenant.Id, hardware: InstalledHoursAgo(hoursAgo));

        Assert.AreEqual(AgentStatus.NeedsDecision, reinstalled.Agent.Status);
    }

    [TestMethod]
    public async Task A_fresh_os_needs_a_decision_when_several_devices_match()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        using var first = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        using var pending = await _factory.EnrollAgentAsync(tenant.Id, hardware: Hardware(Uuid));
        await IdentifyAsync(pending.Id, IdentificationDecision.KeepBoth);

        using var fresh = await _factory.EnrollAgentAsync(tenant.Id, hardware: InstalledHoursAgo(1));

        Assert.AreEqual(AgentStatus.NeedsDecision, fresh.Agent.Status);
    }

    [TestMethod]
    public async Task A_fresh_os_never_matches_a_device_in_another_root_tree()
    {
        var contoso = await _factory.CreateTenantAsync(_client, "Contoso");
        var fabrikam = await _factory.CreateTenantAsync(_client, "Fabrikam");
        using var inContoso = await _factory.EnrollAgentAsync(contoso.Id, hardware: Hardware(Uuid));

        using var inFabrikam = await _factory.EnrollAgentAsync(fabrikam.Id, hardware: InstalledHoursAgo(1));

        Assert.AreEqual(AgentStatus.Active, inFabrikam.Agent.Status);
        Assert.AreNotEqual(inContoso.DeviceId, inFabrikam.DeviceId);
        Assert.AreEqual(fabrikam.Id, inFabrikam.TenantId);
    }

    [TestMethod]
    public async Task Approving_a_fresh_os_claim_needs_permission_at_the_devices_tenant()
    {
        var msp = await _factory.CreateTenantAsync(_client, "Msp");
        var east = await _factory.CreateTenantAsync(_client, "East", parentTenantId: msp.Id);
        var west = await _factory.CreateTenantAsync(_client, "West", parentTenantId: msp.Id);
        using var working = await _factory.EnrollAgentAsync(east.Id, hardware: Hardware(Uuid));
        using var fresh = await _factory.EnrollAgentAsync(west.Id, "REIMAGED", InstalledHoursAgo(1));
        using var westAdmin = await _factory.CreateAssignedClientAsync([west.Id], Scopes.Devices.Read, Scopes.Devices.Write);
        using var eastAdmin = await _factory.CreateAssignedClientAsync([east.Id], Scopes.Devices.Read, Scopes.Devices.Write);

        Assert.AreEqual(working.DeviceId, fresh.DeviceId);
        Assert.AreEqual(east.Id, fresh.TenantId);
        Assert.AreEqual(HttpStatusCode.NotFound, (await westAdmin.PostAsync($"api/v1/agents/{fresh.Id}:approve", null, Ct)).StatusCode);
        var approved = await eastAdmin.PostAsync($"api/v1/agents/{fresh.Id}:approve", null, Ct);
        Assert.AreEqual(HttpStatusCode.OK, approved.StatusCode, await approved.Content.ReadAsStringAsync(Ct));
        Assert.IsTrue((await approved.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!.IsActive);
        var replaced = await _client.GetFromJsonAsync<AgentDto>($"api/v1/agents/{working.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual(AgentStatus.Revoked, replaced!.Status);
        var device = await _client.GetFromJsonAsync<DeviceDto>($"api/v1/devices/{working.DeviceId}", Signal9Json.Options, Ct);
        Assert.AreEqual(fresh.Id, device!.ActiveAgentId);
        Assert.AreEqual("REIMAGED", device.MachineName);
    }

    [TestMethod]
    public async Task Concurrent_enrollments_of_one_machine_with_different_tokens_make_one_device_and_a_decision()
    {
        var tenant = await _factory.CreateTenantAsync(_client, "Contoso");
        var (first, second) = (await _factory.IssueEnrollmentTokenAsync(tenant.Id), await _factory.IssueEnrollmentTokenAsync(tenant.Id));
        // Both enrollments look for matching devices together, unless enrollment serializes them first.
        using var racing = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<Signal9DbContext>(options =>
                options.AddInterceptors(new Rendezvous("FROM \"Devices\"", parties: 2, TimeSpan.FromSeconds(2))))));
        async Task<AgentDto> EnrollAsync(string secret)
        {
            using var anonymous = racing.Server.CreateClient();
            anonymous.BaseAddress = Signal9ApiFactory.HttpsLocalhost;
            using var certificate = TestCertificates.AgentCertificate();
            var response = await anonymous.PostAsJsonAsync("api/v1/agents",
                Hardware(Uuid)(Signal9ApiFactory.Enrollment(secret, certificate)), Signal9Json.Options, Ct);
            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
            return (await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;
        }

        var agents = await Task.WhenAll(EnrollAsync(first.Secret!), EnrollAsync(second.Secret!));

        CollectionAssert.AreEquivalent(new[] { AgentStatus.Active, AgentStatus.NeedsDecision }, agents.Select(a => a.Status).ToArray());
        var devices = await _client.GetFromJsonAsync<PagedResult<DeviceDto>>($"api/v1/devices?tenantId={tenant.Id}", Signal9Json.Options, Ct);
        Assert.ContainsSingle(devices!.Items);
    }

    private async Task<AgentDto> IdentifyAsync(Guid agentId, IdentificationDecision decision, Guid? deviceId = null)
    {
        var response = await _client.PostAsJsonAsync($"api/v1/agents/{agentId}:identify",
            new IdentifyAgentRequest { Decision = decision, DeviceId = deviceId }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<CommandDto> QueueCommandAsync(Guid deviceId)
    {
        var response = await _client.PostAsJsonAsync($"api/v1/devices/{deviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!;
    }

    private HubConnection BuildHub(TestAgent agent) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Signal9ApiFactory.HttpsLocalhost, HubRoutes.Agents.TrimStart('/')), options =>
            {
                options.HttpMessageHandlerFactory = _ =>
                    _factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = Signal9ApiFactory.TrustedProxy);
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult(agent.Client.DefaultRequestHeaders.Authorization!.Parameter);
                options.Headers[Signal9ApiFactory.ClientCertificateHeader] = TestCertificates.ContainerAppsHeader(agent.Certificate);
            })
            .AddJsonProtocol(o => Signal9Json.Configure(o.PayloadSerializerOptions))
            .Build();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }
}
