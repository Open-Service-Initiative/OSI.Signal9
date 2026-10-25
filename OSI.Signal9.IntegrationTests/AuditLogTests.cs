using System.Net;
using System.Net.Http.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Users;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// The audit log (#115): append-only, each entry stored at the tenant where its event happened and readable with
/// <c>audit.read</c> at that tenant or above. Every test reads the trail back through the API, as an auditor would.
/// </summary>
[TestClass]
public sealed class AuditLogTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public AuditLogTests(TestContext testContext)
    {
        _testContext = testContext;
        // The fixture's own client stands in for a Superadministrator.
        _superadministrator = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task A_role_assignment_is_audited_at_its_tenant_and_readable_above_it_but_not_beside_it()
    {
        var tree = await TreeAsync();
        using var mspAdministrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        using var siblingAdministrator = await PersonAsync(tree.Sibling, ("Administrator", tree.Sibling));
        var viewer = await CreateUserAsync(tree.Client, EntraUser.WithMfa(), "Megan Bowen");

        await AssignAsync(tree.Client, viewer.Id, "Viewer");

        var seenAbove = (await TrailAsync(mspAdministrator)).Where(e => e.Action == AuditActions.RoleAssignmentCreated && e.UserId == viewer.Id).ToList();
        var seenBeside = (await TrailAsync(siblingAdministrator)).Where(e => e.UserId == viewer.Id).ToList();
        Assert.HasCount(1, seenAbove);
        var entry = seenAbove[0];
        Assert.AreEqual(tree.Client, entry.TenantId);
        Assert.AreEqual("Client", entry.TenantName);
        Assert.AreEqual("Megan Bowen", entry.UserDisplayName);
        Assert.AreEqual("Viewer", entry.RoleName);
        Assert.AreEqual(AuditOutcome.Succeeded, entry.Outcome);
        Assert.AreEqual("role-assignment", entry.TargetType);
        Assert.IsTrue(entry.Actor.Superadministrator);
        Assert.IsEmpty(seenBeside);
    }

    [TestMethod]
    public async Task Creating_a_tenant_is_audited_at_the_new_tenant_and_deleting_one_at_its_parent()
    {
        var tree = await TreeAsync();
        using var mspAdministrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var site = await _factory.CreateTenantAsync(mspAdministrator, "Site", parentTenantId: tree.Client);
        var created = (await TrailAsync(mspAdministrator)).Single(e => e.Action == AuditActions.TenantCreated && e.TargetId == site.Id);

        var deleted = await mspAdministrator.DeleteAsync($"api/v1/tenants/{site.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(site.Id, created.TenantId);
        Assert.AreEqual("Site", created.TenantName);
        Assert.AreEqual(AuditActorKind.Person, created.Actor.Kind);
        Assert.AreEqual("Alex Wilber", created.Actor.DisplayName);
        Assert.IsFalse(created.Actor.Superadministrator);
        var trail = await TrailAsync(mspAdministrator);
        var removed = trail.Single(e => e.Action == AuditActions.TenantDeleted && e.TargetId == site.Id);
        Assert.AreEqual(tree.Client, removed.TenantId);
        Assert.AreEqual("Client", removed.TenantName);
        var siteCreated = trail.Single(e => e.Action == AuditActions.TenantCreated && e.TargetId == tree.Client);
        Assert.IsTrue(trail.IndexOf(removed) < trail.IndexOf(siteCreated), "Newest first.");
    }

    [TestMethod]
    public async Task Commands_are_audited_when_created_cancelled_and_reported_on()
    {
        var tree = await TreeAsync();
        var device = await DeviceAsync(tree.Client, "HOST-123");
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));
        using var agent = await _factory.CreateClientAsync("agent");
        var cancelled = await QueueAsync(technician, device);
        var reported = await QueueAsync(technician, device);

        Assert.AreEqual(HttpStatusCode.OK, (await technician.PostAsync($"api/v1/devices/{device}/commands/{cancelled}:cancel", null, Ct)).StatusCode);
        var result = await agent.PutAsJsonAsync($"api/v1/devices/{device}/commands/{reported}/result",
            new CommandResultRequest { Succeeded = false, Output = "HOST-123 serial 5CG1234", Error = "Access denied on HOST-123" }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        var trail = await TrailAsync(_superadministrator);
        var entries = trail.Where(e => e.DeviceId == device && e.Action.StartsWith("command.", StringComparison.Ordinal)).ToList();
        Assert.HasCount(2, entries.Where(e => e.Action == AuditActions.CommandCreated));
        var cancellation = entries.Single(e => e.Action == AuditActions.CommandCancelled);
        Assert.AreEqual(cancelled, cancellation.TargetId);
        var outcome = entries.Single(e => e.Action == AuditActions.CommandResult);
        Assert.AreEqual(reported, outcome.TargetId);
        Assert.AreEqual(AuditOutcome.Failed, outcome.Outcome);
        Assert.AreEqual(AuditActorKind.Agent, outcome.Actor.Kind);
        Assert.IsTrue(entries.All(e => e.TenantId == tree.Client && e.DeviceName == "HOST-123" && e.TargetType == "command"));
        Assert.IsTrue(entries.All(e => e.Detail is null || !e.Detail.Contains("HOST-123", StringComparison.Ordinal)),
            "Details never copy device names or output.");
    }

    [TestMethod]
    public async Task An_erased_devices_entries_return_ids_only()
    {
        var tree = await TreeAsync();
        var device = await DeviceAsync(tree.Client, "HOST-123");
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var command = await QueueAsync(administrator, device);

        Assert.AreEqual(HttpStatusCode.NoContent, (await administrator.DeleteAsync($"api/v1/devices/{device}", Ct)).StatusCode);

        var entry = (await TrailAsync(administrator)).Single(e => e.Action == AuditActions.CommandCreated && e.TargetId == command);
        Assert.AreEqual(device, entry.DeviceId);
        Assert.AreEqual(tree.Client, entry.TenantId);
        Assert.IsNotNull(entry.Actor.UserId);
        Assert.IsNull(entry.DeviceName);
        Assert.IsNull(entry.TenantName);
        Assert.IsNull(entry.Actor.DisplayName);
        var single = await administrator.GetFromJsonAsync<AuditEntryDto>($"api/v1/audit-entries/{entry.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual(entry, single, "The entry reads the same on its own.");
    }

    [TestMethod]
    public async Task An_authorization_denial_is_audited_at_the_tenant_it_targeted_or_else_at_the_callers_own()
    {
        var tree = await TreeAsync();
        using var mspAdministrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var person = EntraUser.WithMfa();
        var user = await CreateUserAsync(tree.Client, person, "Megan Bowen");
        await AssignAsync(tree.Client, user.Id, "Viewer");
        using var narrow = await _factory.CreatePersonClientAsync(person, "tenants.read");

        var targeted = await narrow.GetAsync($"api/v1/tenants/{tree.Sibling}/role-assignments", Ct);
        var untargeted = await narrow.GetAsync("api/v1/audit-entries", Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, targeted.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, untargeted.StatusCode);
        var denials = (await TrailAsync(_superadministrator)).Where(e => e.Action == AuditActions.AuthorizationDenied).ToList();
        Assert.HasCount(2, denials);
        Assert.IsTrue(denials.All(e => e.Outcome == AuditOutcome.Denied && e.Actor.Kind == AuditActorKind.Person
            && e.Actor.UserId == user.Id && e.Actor.DisplayName == "Megan Bowen"));
        var atTarget = denials.Single(e => e.TenantId == tree.Sibling);
        Assert.AreEqual("GET /api/v1/tenants/{tenantId:guid}/role-assignments (needs roleassignments.read)", atTarget.Detail);
        var atOwn = denials.Single(e => e.TenantId == tree.Client);
        Assert.AreEqual("GET /api/v1/audit-entries (needs audit.read)", atOwn.Detail);
        var seenAtMsp = (await TrailAsync(mspAdministrator)).Where(e => e.Action == AuditActions.AuthorizationDenied).Select(e => e.Id).ToList();
        CollectionAssert.AreEqual(new[] { atOwn.Id }, seenAtMsp, "Msp's Administrator reads Client's denial, not Sibling's.");
    }

    [TestMethod]
    public async Task A_superadministrators_change_is_audited_at_the_tenant_it_touched()
    {
        var tree = await TreeAsync();
        using var clientAdministrator = await PersonAsync(tree.Client, ("Administrator", tree.Client));

        var suspended = await _superadministrator.PostAsync($"api/v1/tenants/{tree.Client}:suspend", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, suspended.StatusCode);
        var trail = await TrailAsync(clientAdministrator);
        var entry = trail.Single(e => e.Action == AuditActions.SuperadministratorRequest);
        Assert.AreEqual(tree.Client, entry.TenantId);
        Assert.IsTrue(entry.Actor.Superadministrator);
        Assert.AreEqual("POST /api/v1/tenants/{tenantId:guid}:suspend", entry.Detail);
        var created = trail.Single(e => e.Action == AuditActions.TenantCreated && e.TargetId == tree.Client);
        Assert.IsTrue(created.Actor.Superadministrator, "A change with its own entry isn't recorded twice.");
    }

    [TestMethod]
    public async Task Repeated_authentication_failures_are_a_deployment_level_entry_only_superadministrators_read()
    {
        var tree = await TreeAsync();
        using var rootAdministrator = await PersonAsync(tree.Root, ("Administrator", tree.Root));
        using var forged = _factory.CreateAnonymousClient();
        forged.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-token");
        forged.DefaultRequestHeaders.Add(Signal9ApiFactory.ClientIpHeader, "198.51.100.7");

        for (var attempt = 1; attempt <= 4; attempt++)
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await forged.GetAsync("api/v1/tenants", Ct)).StatusCode);
        var beforeThreshold = (await TrailAsync(_superadministrator)).Count(e => e.Action == AuditActions.RepeatedAuthenticationFailures);
        await forged.GetAsync("api/v1/tenants", Ct);

        Assert.AreEqual(0, beforeThreshold);
        var entry = (await TrailAsync(_superadministrator)).Single(e => e.Action == AuditActions.RepeatedAuthenticationFailures);
        Assert.IsNull(entry.TenantId);
        Assert.AreEqual("198.51.100.7", entry.ClientIp);
        Assert.AreEqual(AuditOutcome.Failed, entry.Outcome);
        Assert.AreEqual(AuditActorKind.Anonymous, entry.Actor.Kind);
        Assert.IsFalse((await TrailAsync(rootAdministrator)).Any(e => e.TenantId is null), "Deployment-level entries are for Superadministrators only.");
    }

    [TestMethod]
    public async Task Repeated_client_authentication_failures_at_the_token_endpoint_are_audited()
    {
        await _factory.SeedClientAsync("guessed-app", "tenants.read");
        using var auth = _factory.Auth.CreateClient();
        auth.DefaultRequestHeaders.Add(Signal9ApiFactory.ClientIpHeader, "198.51.100.8");

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var response = await auth.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "guessed-app",
                ["client_secret"] = $"wrong-secret-{attempt}",
            }), Ct);
            Assert.IsFalse(response.IsSuccessStatusCode);
        }

        var entry = (await TrailAsync(_superadministrator)).Single(e => e.Action == AuditActions.RepeatedAuthenticationFailures);
        Assert.AreEqual("198.51.100.8", entry.ClientIp);
        Assert.AreEqual("guessed-app", entry.Actor.ClientId);
        Assert.IsNull(entry.TenantId);
    }

    [TestMethod]
    public async Task The_audit_log_is_append_only()
    {
        var tree = await TreeAsync();
        var entry = (await TrailAsync(_superadministrator)).First(e => e.TargetId == tree.Client);

        var deleted = await _superadministrator.DeleteAsync($"api/v1/audit-entries/{entry.Id}", Ct);
        using var patch = new StringContent("{}", System.Text.Encoding.UTF8, "application/merge-patch+json");
        var patched = await _superadministrator.PatchAsync($"api/v1/audit-entries/{entry.Id}", patch, Ct);

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, patched.StatusCode);
        var read = await _superadministrator.GetAsync($"api/v1/audit-entries/{entry.Id}", Ct);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.IsNotNull(read.Headers.ETag);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _superadministrator.GetAsync($"api/v1/audit-entries/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Reading_the_audit_log_needs_audit_read()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Root, ("Technician", tree.Root));
        using var noScope = await _factory.CreateClientAsync("tenants.read");

        var byTechnician = await technician.GetFromJsonAsync<PagedResult<AuditEntryDto>>("api/v1/audit-entries", Signal9Json.Options, Ct);
        var withoutScope = await noScope.GetAsync("api/v1/audit-entries", Ct);

        Assert.IsEmpty(byTechnician!.Items, "A role without audit.read reads nothing.");
        Assert.AreEqual(HttpStatusCode.Forbidden, withoutScope.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await _factory.CreateAnonymousClient().GetAsync("api/v1/audit-entries", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task A_sign_in_is_audited_at_the_persons_owning_tenant()
    {
        var tree = await TreeAsync();
        using var mspAdministrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var person = EntraUser.WithMfa();
        var user = await CreateUserAsync(tree.Client, person, "Megan Bowen");
        var stranger = EntraUser.WithMfa();

        using var signedIn = await _factory.CreatePersonClientAsync(person, "tenants.read");
        using var strangerSignedIn = await _factory.CreatePersonClientAsync(stranger, "tenants.read");

        var entry = (await TrailAsync(mspAdministrator)).Single(e => e.Action == AuditActions.SignIn && e.Actor.UserId == user.Id);
        Assert.AreEqual(tree.Client, entry.TenantId);
        Assert.AreEqual(AuditOutcome.Succeeded, entry.Outcome);
        Assert.AreEqual(AuditActorKind.Person, entry.Actor.Kind);
        Assert.AreEqual($"{person.TenantId}:{person.ObjectId}", entry.Actor.Subject);
        Assert.AreEqual("Megan Bowen", entry.Actor.DisplayName);
        Assert.AreEqual("203.0.113.1", entry.ClientIp);
        var strangers = (await TrailAsync(_superadministrator)).Single(e => e.Action == AuditActions.SignIn
            && e.Actor.Subject == $"{stranger.TenantId}:{stranger.ObjectId}");
        Assert.IsNull(strangers.TenantId, "Without a user, a sign-in is deployment-level.");
    }

    [TestMethod]
    public async Task A_refused_sign_in_is_audited_as_failed()
    {
        await _factory.SeedCodeClientAsync("audit-sign-in-app", "tenants.read");
        var person = EntraUser.WithMfa() with { Amr = ["pwd"] };
        using var browser = new Browser(_factory.Auth.Server);
        var flow = new CodeFlow("audit-sign-in-app");

        var redirect = await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra, person);

        Assert.AreEqual("access_denied", CodeFlow.ErrorFrom(redirect));
        var entry = (await TrailAsync(_superadministrator)).Single(e => e.Action == AuditActions.SignIn);
        Assert.AreEqual(AuditOutcome.Failed, entry.Outcome);
        Assert.AreEqual("NoMfa", entry.Detail);
        Assert.AreEqual($"{person.TenantId}:{person.ObjectId}", entry.Actor.Subject);
        Assert.AreEqual("audit-sign-in-app", entry.Actor.ClientId);
    }

    // Helpers

    private async Task<Guid> QueueAsync(HttpClient caller, Guid deviceId)
    {
        var response = await caller.PostAsJsonAsync($"api/v1/devices/{deviceId}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<CommandDto>(Signal9Json.Options, Ct))!.Id;
    }

    private sealed record Tree(Guid Root, Guid Msp, Guid Client, Guid Sibling);

    /// <summary>Root ─ Msp ─ Client, and Root ─ Sibling.</summary>
    private async Task<Tree> TreeAsync()
    {
        var root = await _factory.CreateTenantAsync(_superadministrator, "Root");
        var msp = await _factory.CreateTenantAsync(_superadministrator, "Msp", parentTenantId: root.Id);
        var client = await _factory.CreateTenantAsync(_superadministrator, "Client", parentTenantId: msp.Id);
        var sibling = await _factory.CreateTenantAsync(_superadministrator, "Sibling", parentTenantId: root.Id);
        return new Tree(root.Id, msp.Id, client.Id, sibling.Id);
    }

    private async Task<Guid> DeviceAsync(Guid tenantId, string machineName)
    {
        var registered = await Signal9ApiFactory.RegisterAsync(_superadministrator, tenantId, machineName);
        Assert.AreEqual(HttpStatusCode.Created, registered.StatusCode);
        return (await registered.Content.ReadFromJsonAsync<AgentDto>(Signal9Json.Options, Ct))!.DeviceId;
    }

    /// <summary>A newly signed-in person, owned by <paramref name="owner"/>, holding these role assignments.</summary>
    private async Task<HttpClient> PersonAsync(Guid owner, params (string Role, Guid Tenant)[] assignments) =>
        await PersonAsync(EntraUser.WithMfa(), owner, assignments);

    private async Task<HttpClient> PersonAsync(EntraUser person, Guid owner, params (string Role, Guid Tenant)[] assignments)
    {
        var user = await CreateUserAsync(owner, person);
        foreach (var (role, tenant) in assignments)
            await AssignAsync(tenant, user.Id, role);
        return await _factory.CreatePersonClientAsync(person);
    }

    private async Task<UserDto> CreateUserAsync(Guid tenantId, EntraUser person, string displayName = "Alex Wilber")
    {
        var response = await _superadministrator.PostAsJsonAsync("api/v1/users", new CreateUserRequest
        {
            TenantId = tenantId,
            EntraTenantId = person.TenantId,
            EntraObjectId = person.ObjectId,
            DisplayName = displayName,
        }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;
    }

    private async Task AssignAsync(Guid tenantId, Guid userId, string role)
    {
        var roles = await _superadministrator.GetFromJsonAsync<PagedResult<RoleDto>>("api/v1/roles", Signal9Json.Options, Ct);
        var response = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenantId}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = userId, RoleId = roles!.Items.Single(r => r.Name == role).Id },
            Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Every audit entry <paramref name="caller"/> can read, newest first.</summary>
    private async Task<List<AuditEntryDto>> TrailAsync(HttpClient caller)
    {
        List<AuditEntryDto> entries = [];
        for (var page = 1; ; page++)
        {
            var response = await caller.GetAsync($"api/v1/audit-entries?page={page}&pageSize=100", Ct);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
            var result = (await response.Content.ReadFromJsonAsync<PagedResult<AuditEntryDto>>(Signal9Json.Options, Ct))!;
            entries.AddRange(result.Items);
            if (page >= result.TotalPages)
                return entries;
        }
    }

    public void Dispose()
    {
        _superadministrator.Dispose();
        _factory.Dispose();
    }
}
