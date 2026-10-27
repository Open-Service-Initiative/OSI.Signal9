using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Contracts.Users;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Tenant-scoped RBAC (ADR-0010, #114): a person's access comes only from role assignments, each granting a role's
/// scopes at a tenant and its descendants, intersected with their token's scopes and evaluated live.
/// </summary>
[TestClass]
public sealed class RoleBasedAccessTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public RoleBasedAccessTests(TestContext testContext)
    {
        _testContext = testContext;
        // The fixture's own client stands in for a Superadministrator.
        _superadministrator = _factory.CreateSuperadministratorClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    // The bundles as #63 defines them, written out independently of the seeded roles.
    private static readonly string[] Viewer =
        ["tenants.read", "devices.read", "commands.read", "scripts.read", "telemetry.read", "users.read"];

    private static readonly string[] Technician =
    [
        .. Viewer, "devices.write", "devices.transfer", "commands.execute", "scripts.write", "scripts.delete",
        "scripts.execute", "terminal.execute", "users.write", "enrollment.read", "enrollment.write",
    ];

    private static readonly string[] Administrator =
    [
        .. Technician, "tenants.write", "tenants.delete", "tenants.transfer", "devices.delete", "roleassignments.read",
        "roleassignments.write", "roles.read", "roles.write", "audit.read", "apiapplications.read",
        "apiapplications.write", "integrations.read", "integrations.write", "users.transfer",
    ];

    private static string[] Bundle(string role) => role switch
    {
        "Viewer" => Viewer,
        "Technician" => Technician,
        "Administrator" => Administrator,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    [TestMethod]
    public async Task The_built_in_roles_are_named_bundles_of_scopes()
    {
        var roles = await RolesAsync();

        CollectionAssert.AreEquivalent(new[] { "Viewer", "Technician", "Administrator" }, roles.Select(r => r.Name).ToArray());
        foreach (var role in roles)
        {
            Assert.IsTrue(role.BuiltIn, role.Name);
            CollectionAssert.AreEquivalent(Bundle(role.Name), role.Scopes.ToArray(), role.Name);
        }
    }

    [TestMethod]
    public async Task Built_in_roles_refuse_modification()
    {
        var viewer = await RoleAsync("Viewer");
        using var patch = new StringContent("""{"scopes":["tenants.read","tenants.write"]}""", Encoding.UTF8, "application/merge-patch+json");

        var patched = await _superadministrator.PatchAsync($"api/v1/roles/{viewer.Id}", patch, Ct);
        var deleted = await _superadministrator.DeleteAsync($"api/v1/roles/{viewer.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, patched.StatusCode);
        Assert.AreEqual("application/problem+json", patched.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Conflict, deleted.StatusCode);
        CollectionAssert.AreEquivalent(Viewer, (await RoleAsync("Viewer")).Scopes.ToArray());
    }

    [TestMethod]
    public async Task A_role_can_be_read_by_its_id_with_an_etag()
    {
        var viewer = await RoleAsync("Viewer");

        var response = await _superadministrator.GetAsync($"api/v1/roles/{viewer.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsNotNull(response.Headers.ETag);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _superadministrator.GetAsync($"api/v1/roles/{Guid.NewGuid()}", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Roles_need_the_scope_from_a_role_at_some_tenant()
    {
        var tree = await TreeAsync();
        var viewerRole = await RoleAsync("Viewer");
        using var viewer = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));
        using var administrator = await PersonAsync(tree.Client, ("Administrator", tree.Client));

        var byViewer = await viewer.GetAsync("api/v1/roles", Ct);
        var oneByViewer = await viewer.GetAsync($"api/v1/roles/{viewerRole.Id}", Ct);
        var byAdministrator = await administrator.GetAsync("api/v1/roles", Ct);
        var deleteByViewer = await viewer.DeleteAsync($"api/v1/roles/{viewerRole.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, byViewer.StatusCode);
        Assert.AreEqual("application/problem+json", byViewer.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Forbidden, oneByViewer.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, byAdministrator.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, deleteByViewer.StatusCode);
    }

    [TestMethod]
    public async Task Users_and_role_assignments_outside_the_callers_reach_are_hidden()
    {
        var tree = await TreeAsync();
        var outsider = await CreateUserAsync(tree.Sibling, EntraUser.WithMfa());
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        var users = await administrator.GetFromJsonAsync<PagedResult<UserDto>>("api/v1/users?pageSize=100", Signal9Json.Options, Ct);
        var hidden = await administrator.GetAsync($"api/v1/users/{outsider.Id}", Ct);
        var assignOutsider = await administrator.PostAsJsonAsync($"api/v1/tenants/{tree.Msp}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = outsider.Id, RoleId = (await RoleAsync("Viewer")).Id }, Signal9Json.Options, Ct);
        var siblingAssignments = await administrator.GetAsync($"api/v1/tenants/{tree.Sibling}/role-assignments", Ct);
        var ownAssignments = await administrator.GetFromJsonAsync<PagedResult<RoleAssignmentDto>>(
            $"api/v1/tenants/{tree.Msp}/role-assignments", Signal9Json.Options, Ct);

        CollectionAssert.DoesNotContain(users!.Items.Select(u => u.Id).ToArray(), outsider.Id);
        Assert.HasCount(1, users.Items);
        Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, assignOutsider.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, siblingAssignments.StatusCode);
        Assert.HasCount(1, ownAssignments!.Items);
    }

    [TestMethod]
    public async Task A_user_belongs_to_one_owning_tenant_and_links_to_an_entra_identity()
    {
        var tenant = await _factory.CreateTenantAsync(_superadministrator, "Contoso");
        var person = EntraUser.WithMfa();

        var created = await _superadministrator.PostAsJsonAsync("api/v1/users", NewUser(tenant.Id, person), Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        var user = (await created.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(new Uri(Signal9ApiFactory.HttpsLocalhost, $"/api/v1/users/{user.Id}"), created.Headers.Location);
        Assert.AreEqual(tenant.Id, user.TenantId);
        Assert.AreEqual(person.TenantId, user.EntraTenantId);
        Assert.AreEqual(person.ObjectId, user.EntraObjectId);
        var read = await _superadministrator.GetAsync($"api/v1/users/{user.Id}", Ct);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.IsNotNull(read.Headers.ETag);
    }

    [TestMethod]
    public async Task An_entra_identity_links_to_only_one_user()
    {
        var first = await _factory.CreateTenantAsync(_superadministrator, "Contoso");
        var second = await _factory.CreateTenantAsync(_superadministrator, "Fabrikam");
        var person = EntraUser.WithMfa();
        await CreateUserAsync(first.Id, person);

        var again = await _superadministrator.PostAsJsonAsync("api/v1/users", NewUser(second.Id, person), Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        Assert.AreEqual("application/problem+json", again.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task A_user_needs_an_owning_tenant_that_exists()
    {
        var response = await _superadministrator.PostAsJsonAsync("api/v1/users", NewUser(Guid.NewGuid(), EntraUser.WithMfa()), Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task A_superadministrator_creates_a_role_assignment()
    {
        var tenant = await _factory.CreateTenantAsync(_superadministrator, "Contoso");
        var user = await CreateUserAsync(tenant.Id, EntraUser.WithMfa());
        var technician = await RoleAsync("Technician");

        var created = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenant.Id}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = user.Id, RoleId = technician.Id }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        var assignment = (await created.Content.ReadFromJsonAsync<RoleAssignmentDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(new Uri(Signal9ApiFactory.HttpsLocalhost, $"/api/v1/tenants/{tenant.Id}/role-assignments/{assignment.Id}"),
            created.Headers.Location);
        Assert.AreEqual((tenant.Id, user.Id, technician.Id), (assignment.TenantId, assignment.UserId, assignment.RoleId));
        var read = await _superadministrator.GetAsync($"api/v1/tenants/{tenant.Id}/role-assignments/{assignment.Id}", Ct);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.IsNotNull(read.Headers.ETag);
        var listed = await _superadministrator.GetFromJsonAsync<PagedResult<RoleAssignmentDto>>(
            $"api/v1/tenants/{tenant.Id}/role-assignments", Signal9Json.Options, Ct);
        Assert.AreEqual(assignment.Id, Assert.ContainsSingle(listed!.Items).Id);
    }

    [TestMethod]
    public async Task The_same_role_assignment_cannot_be_made_twice()
    {
        var tenant = await _factory.CreateTenantAsync(_superadministrator, "Contoso");
        var user = await CreateUserAsync(tenant.Id, EntraUser.WithMfa());
        await AssignAsync(tenant.Id, user.Id, "Viewer");

        var again = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenant.Id}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = user.Id, RoleId = (await RoleAsync("Viewer")).Id }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
    }

    [TestMethod]
    public async Task A_role_assignment_needs_a_user_and_a_role_that_exist()
    {
        var tenant = await _factory.CreateTenantAsync(_superadministrator, "Contoso");
        var user = await CreateUserAsync(tenant.Id, EntraUser.WithMfa());

        var noUser = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenant.Id}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = Guid.NewGuid(), RoleId = (await RoleAsync("Viewer")).Id }, Signal9Json.Options, Ct);
        var noRole = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenant.Id}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = user.Id, RoleId = Guid.NewGuid() }, Signal9Json.Options, Ct);
        var noTenant = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{Guid.NewGuid()}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = user.Id, RoleId = (await RoleAsync("Viewer")).Id }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, noUser.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, noRole.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, noTenant.StatusCode);
    }

    [TestMethod]
    public async Task An_administrator_assigns_roles_within_their_subtree_only()
    {
        var tree = await TreeAsync();
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var colleague = await CreateUserAsync(tree.Client, EntraUser.WithMfa());
        var viewer = await RoleAsync("Viewer");

        var inside = await administrator.PostAsJsonAsync($"api/v1/tenants/{tree.Client}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = colleague.Id, RoleId = viewer.Id }, Signal9Json.Options, Ct);
        var outside = await administrator.PostAsJsonAsync($"api/v1/tenants/{tree.Sibling}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = colleague.Id, RoleId = viewer.Id }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, inside.StatusCode, await inside.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NotFound, outside.StatusCode);
    }

    [TestMethod]
    public async Task A_technician_cannot_assign_roles()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));
        var colleague = await CreateUserAsync(tree.Client, EntraUser.WithMfa());

        var response = await technician.PostAsJsonAsync($"api/v1/tenants/{tree.Client}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = colleague.Id, RoleId = (await RoleAsync("Viewer")).Id }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task A_signed_in_person_with_no_assignments_sees_nothing()
    {
        var tree = await TreeAsync();
        var withUser = EntraUser.WithMfa();
        await CreateUserAsync(tree.Msp, withUser);
        using var unknown = await _factory.CreatePersonClientAsync(EntraUser.WithMfa());
        using var unassigned = await _factory.CreatePersonClientAsync(withUser);

        foreach (var person in new[] { unknown, unassigned })
        {
            Assert.IsEmpty(await TenantIdsAsync(person));
            Assert.AreEqual(HttpStatusCode.NotFound, (await person.GetAsync($"api/v1/tenants/{tree.Msp}", Ct)).StatusCode);
        }
    }

    [TestMethod]
    public async Task A_role_assignment_covers_its_tenant_and_every_descendant_but_no_sibling_or_ancestor()
    {
        var tree = await TreeAsync();
        using var viewer = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));

        CollectionAssert.AreEquivalent(new[] { tree.Msp, tree.Client }, await TenantIdsAsync(viewer));
        CollectionAssert.AreEquivalent(new[] { tree.MspDevice, tree.ClientDevice }, await DeviceIdsAsync(viewer));
    }

    [TestMethod]
    public async Task A_new_role_assignment_takes_effect_on_the_next_request()
    {
        var tree = await TreeAsync();
        var person = EntraUser.WithMfa();
        var user = await CreateUserAsync(tree.Msp, person);
        using var api = await _factory.CreatePersonClientAsync(person);
        var before = await TenantIdsAsync(api);

        await AssignAsync(tree.Client, user.Id, "Viewer");

        Assert.IsEmpty(before);
        CollectionAssert.AreEquivalent(new[] { tree.Client }, await TenantIdsAsync(api));
    }

    /// <summary>
    /// The scope/role/subtree matrix (stories 20, 24, 25, 67): with a token holding every scope and one assignment at
    /// MSP, each catalogue scope is granted at MSP and Client exactly when the role has it, and never at Root or Sibling.
    /// </summary>
    [TestMethod]
    [DataRow("Viewer")]
    [DataRow("Technician")]
    [DataRow("Administrator")]
    public async Task Each_role_grants_exactly_its_bundle_across_its_subtree(string role)
    {
        var tree = await TreeAsync();
        using var api = ProbingApi();
        using var person = await PersonAsync(tree.Msp, (role, tree.Msp));

        var unexpected = new List<string>();
        foreach (var scope in TenantScopedCatalogue)
        {
            foreach (var (name, tenant, inSubtree) in new[]
            {
                ("MSP", tree.Msp, true), ("Client", tree.Client, true), ("Root", tree.Root, false), ("Sibling", tree.Sibling, false),
            })
            {
                var expected = inSubtree && Bundle(role).Contains(scope) ? HttpStatusCode.OK : HttpStatusCode.NotFound;
                var actual = (await ProbeAsync(api, person, scope, tenant)).StatusCode;
                if (actual != expected)
                    unexpected.Add($"{scope} at {name}: {actual}, expected {expected}");
            }
        }

        Assert.IsEmpty(unexpected, string.Join(Environment.NewLine, unexpected));
    }

    [TestMethod]
    public async Task A_role_grants_nothing_beyond_the_tokens_scopes()
    {
        var tree = await TreeAsync();
        using var api = ProbingApi();
        var person = EntraUser.WithMfa();
        var user = await CreateUserAsync(tree.Msp, person);
        await AssignAsync(tree.Msp, user.Id, "Administrator");
        using var readOnly = await _factory.CreatePersonClientAsync(person, "tenants.read");

        var read = await ProbeAsync(api, readOnly, "tenants.read", tree.Msp);
        var write = await ProbeAsync(api, readOnly, "tenants.write", tree.Msp);
        var patch = await readOnly.PatchAsync($"api/v1/tenants/{tree.Msp}", MergePatch("""{"name":"Renamed"}"""), Ct);

        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, write.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, patch.StatusCode);
        StringAssert.Contains(patch.Headers.WwwAuthenticate.ToString(), "insufficient_scope");
    }

    [TestMethod]
    public async Task Several_assignments_give_the_union_of_their_access()
    {
        var tree = await TreeAsync();
        using var api = ProbingApi();
        using var person = await PersonAsync(tree.Msp, ("Viewer", tree.Root), ("Technician", tree.Sibling), ("Administrator", tree.Client));

        CollectionAssert.AreEquivalent(new[] { tree.Root, tree.Msp, tree.Client, tree.Sibling }, await TenantIdsAsync(person));
        Assert.AreEqual(HttpStatusCode.NotFound, (await ProbeAsync(api, person, "devices.write", tree.Msp)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await ProbeAsync(api, person, "devices.write", tree.Sibling)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await ProbeAsync(api, person, "devices.write", tree.Client)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await ProbeAsync(api, person, "tenants.write", tree.Client)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await ProbeAsync(api, person, "tenants.write", tree.Sibling)).StatusCode);
    }

    [TestMethod]
    public async Task A_viewer_reads_a_tenant_but_cannot_change_it()
    {
        var tree = await TreeAsync();
        using var viewer = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));

        var read = await viewer.GetAsync($"api/v1/tenants/{tree.Client}", Ct);
        var patch = await viewer.PatchAsync($"api/v1/tenants/{tree.Client}", MergePatch("""{"name":"Renamed"}"""), Ct);
        var command = await viewer.PostAsJsonAsync($"api/v1/devices/{tree.ClientDevice}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, command.StatusCode);
    }

    [TestMethod]
    public async Task A_technician_operates_devices_in_their_subtree()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));

        var inside = await technician.PostAsJsonAsync($"api/v1/devices/{tree.ClientDevice}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        var outside = await technician.PostAsJsonAsync($"api/v1/devices/{tree.SiblingDevice}/commands",
            new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
        var deleteDevice = await technician.DeleteAsync($"api/v1/devices/{tree.ClientDevice}", Ct);

        Assert.AreEqual(HttpStatusCode.Created, inside.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, outside.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, deleteDevice.StatusCode);
    }

    [TestMethod]
    public async Task Running_a_script_needs_terminal_execute_from_the_role_as_well_as_the_token()
    {
        var tree = await TreeAsync();
        // Technician at Client grants terminal.execute there; Viewer at Root grants nothing to run.
        using var person = await PersonAsync(tree.Msp, ("Technician", tree.Client), ("Viewer", tree.Root));
        var script = new CreateCommandRequest { Type = CommandType.RunScript, Parameters = "Get-Date" };

        var inside = await person.PostAsJsonAsync($"api/v1/devices/{tree.ClientDevice}/commands", script, Signal9Json.Options, Ct);
        var outside = await person.PostAsJsonAsync($"api/v1/devices/{tree.MspDevice}/commands", script, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, inside.StatusCode, await inside.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NotFound, outside.StatusCode);
    }

    [TestMethod]
    public async Task An_administrator_creates_tenants_only_beneath_tenants_they_administer()
    {
        var tree = await TreeAsync();
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp), ("Viewer", tree.Root));

        var inside = await administrator.PostAsJsonAsync("api/v1/tenants", NewTenant(tree.Client), Signal9Json.Options, Ct);
        var readOnly = await administrator.PostAsJsonAsync("api/v1/tenants", NewTenant(tree.Sibling), Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, inside.StatusCode);
        // As if the parent didn't exist, so a caller learns nothing about tenants outside their reach.
        Assert.AreEqual(HttpStatusCode.BadRequest, readOnly.StatusCode);
    }

    /// <summary>Catalogue scopes a role grants at a tenant: all but the OpenID Connect, agent and deployment scopes.</summary>
    private static readonly string[] TenantScopedCatalogue = Signal9ApiFactory.PersonScopes
        .Where(s => !s.StartsWith("deployment.", StringComparison.Ordinal)).ToArray();

    private sealed record Tree(Guid Root, Guid Msp, Guid Client, Guid Sibling, Guid MspDevice, Guid ClientDevice, Guid SiblingDevice);

    /// <summary>Root ─ MSP ─ Client, and Root ─ Sibling; MSP, Client and Sibling each have a device.</summary>
    private async Task<Tree> TreeAsync()
    {
        var root = await _factory.CreateTenantAsync(_superadministrator, "Root");
        var msp = await _factory.CreateTenantAsync(_superadministrator, "Msp", parentTenantId: root.Id);
        var client = await _factory.CreateTenantAsync(_superadministrator, "Client", parentTenantId: msp.Id);
        var sibling = await _factory.CreateTenantAsync(_superadministrator, "Sibling", parentTenantId: root.Id);
        return new Tree(root.Id, msp.Id, client.Id, sibling.Id,
            await DeviceAsync(msp.Id), await DeviceAsync(client.Id), await DeviceAsync(sibling.Id));
    }

    private async Task<Guid> DeviceAsync(Guid tenantId)
    {
        using var agent = await _factory.EnrollAgentAsync(tenantId, $"PC-{tenantId:N}");
        return agent.DeviceId;
    }

    /// <summary>A newly signed-in person, owned by <paramref name="owner"/>, holding these role assignments.</summary>
    private async Task<HttpClient> PersonAsync(Guid owner, params (string Role, Guid Tenant)[] assignments)
    {
        var person = EntraUser.WithMfa();
        var user = await CreateUserAsync(owner, person);
        foreach (var (role, tenant) in assignments)
            await AssignAsync(tenant, user.Id, role);
        return await _factory.CreatePersonClientAsync(person);
    }

    private static CreateUserRequest NewUser(Guid tenantId, EntraUser person) => new()
    {
        TenantId = tenantId,
        EntraTenantId = person.TenantId,
        EntraObjectId = person.ObjectId,
        DisplayName = "Alex Wilber",
    };

    private static CreateTenantRequest NewTenant(Guid parent) =>
        new() { Name = "Site", ContactEmail = "it@site.example", MaxDevices = 5, ParentTenantId = parent };

    private static StringContent MergePatch(string json) => new(json, Encoding.UTF8, "application/merge-patch+json");

    private async Task<UserDto> CreateUserAsync(Guid tenantId, EntraUser person)
    {
        var response = await _superadministrator.PostAsJsonAsync("api/v1/users", NewUser(tenantId, person), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;
    }

    private async Task AssignAsync(Guid tenantId, Guid userId, string role)
    {
        var response = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenantId}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = userId, RoleId = (await RoleAsync(role)).Id }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<List<RoleDto>> RolesAsync()
    {
        var response = await _superadministrator.GetAsync("api/v1/roles", Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return [.. (await response.Content.ReadFromJsonAsync<PagedResult<RoleDto>>(Signal9Json.Options, Ct))!.Items];
    }

    private async Task<RoleDto> RoleAsync(string name) => (await RolesAsync()).Single(r => r.Name == name);

    private async Task<Guid[]> TenantIdsAsync(HttpClient caller) =>
        [.. (await caller.GetFromJsonAsync<PagedResult<TenantDto>>("api/v1/tenants?pageSize=100", Signal9Json.Options, Ct))!.Items.Select(t => t.Id)];

    private async Task<Guid[]> DeviceIdsAsync(HttpClient caller) =>
        [.. (await caller.GetFromJsonAsync<PagedResult<OSI.Signal9.Contracts.Devices.DeviceDto>>("api/v1/devices?pageSize=100", Signal9Json.Options, Ct))!.Items.Select(d => d.Id)];

    /// <summary>The api with <see cref="RoleProbeController"/>, sharing the fixture's database and keys.</summary>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<OSI.Signal9.API.ApiHost> ProbingApi() =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(RoleProbeController).Assembly)));

    private async Task<HttpResponseMessage> ProbeAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<OSI.Signal9.API.ApiHost> api, HttpClient caller, string scope, Guid tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"test/role-probe/{scope}/{tenantId}");
        request.Headers.Authorization = caller.DefaultRequestHeaders.Authorization;
        using var server = api.CreateClient();
        return await server.SendAsync(request, Ct);
    }

    public void Dispose()
    {
        _superadministrator.Dispose();
        _factory.Dispose();
    }
}

/// <summary>
/// Answers whether the caller may use <c>scope</c> at a tenant, as any endpoint requiring that scope would decide it:
/// 200 when the tenant is visible through the tenant filter for that scope, 404 when it isn't.
/// </summary>
[ApiController]
[Route("test/role-probe")]
public sealed class RoleProbeController(Signal9DbContext db, CallerAccess access) : ControllerBase
{
    [HttpGet("{scope}/{tenantId:guid}")]
    public async Task<IActionResult> Get(string scope, Guid tenantId, CancellationToken cancellationToken)
    {
        access.Require(scope);
        return await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken) ? Ok() : NotFound();
    }
}
