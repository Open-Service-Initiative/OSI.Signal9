using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Users;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Managing role assignments (#116, stories 17-18, 22-23, 64): Administrators grant and revoke roles within their
/// subtree, without escalating privileges or locking each other out.
/// </summary>
[TestClass]
public sealed class RoleAssignmentManagementTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public RoleAssignmentManagementTests(TestContext testContext)
    {
        _testContext = testContext;
        _superadministrator = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task An_administrator_grants_and_revokes_each_built_in_role_within_their_subtree()
    {
        var tree = await TreeAsync();
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        foreach (var (role, at) in new[] { ("Administrator", tree.Client), ("Technician", tree.Msp), ("Viewer", tree.Client) })
        {
            var user = await CreateUserAsync(tree.Client, EntraUser.WithMfa());

            var granted = await GrantAsync(administrator, at, user.Id, role);
            Assert.AreEqual(HttpStatusCode.Created, granted.StatusCode, await granted.Content.ReadAsStringAsync(Ct));
            var assignment = (await granted.Content.ReadFromJsonAsync<RoleAssignmentDto>(Signal9Json.Options, Ct))!;

            var revoked = await administrator.DeleteAsync(granted.Headers.Location, Ct);
            Assert.AreEqual(HttpStatusCode.NoContent, revoked.StatusCode, await revoked.Content.ReadAsStringAsync(Ct));
            Assert.AreEqual(HttpStatusCode.NotFound, (await administrator.GetAsync(granted.Headers.Location, Ct)).StatusCode);
            Assert.AreEqual(HttpStatusCode.NotFound,
                (await administrator.DeleteAsync($"api/v1/tenants/{at}/role-assignments/{assignment.Id}", Ct)).StatusCode);
        }
    }

    [TestMethod]
    public async Task Granting_or_revoking_outside_your_subtree_reads_as_missing()
    {
        var tree = await TreeAsync();
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var user = await CreateUserAsync(tree.Msp, EntraUser.WithMfa());
        var siblingAssignment = await AssignAsync(tree.Sibling, user.Id, "Viewer");

        var atSibling = await GrantAsync(administrator, tree.Sibling, user.Id, "Viewer");
        var atParent = await GrantAsync(administrator, tree.Root, user.Id, "Viewer");
        var revokeAtSibling = await administrator.DeleteAsync($"api/v1/tenants/{tree.Sibling}/role-assignments/{siblingAssignment.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, atSibling.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, atParent.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, revokeAtSibling.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK,
            (await _superadministrator.GetAsync($"api/v1/tenants/{tree.Sibling}/role-assignments/{siblingAssignment.Id}", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Granting_a_role_beyond_your_own_access_is_refused()
    {
        var tree = await TreeAsync();
        // An Administrator whose token carries only what a Viewer has, plus assigning roles: their own access at the
        // tenant is token scopes ∩ role scopes, so it's a Viewer's.
        var person = EntraUser.WithMfa();
        var caller = await CreateUserAsync(tree.Msp, person);
        await AssignAsync(tree.Msp, caller.Id, "Administrator");
        using var narrow = await _factory.CreatePersonClientAsync(person,
            [.. BuiltInRoles.Viewer.Scopes, Scopes.RoleAssignments.Read, Scopes.RoleAssignments.Write]);
        var user = await CreateUserAsync(tree.Client, EntraUser.WithMfa());

        var technician = await GrantAsync(narrow, tree.Client, user.Id, "Technician");
        var administrator = await GrantAsync(narrow, tree.Client, user.Id, "Administrator");
        var viewer = await GrantAsync(narrow, tree.Client, user.Id, "Viewer");

        Assert.AreEqual(HttpStatusCode.Forbidden, technician.StatusCode);
        Assert.AreEqual("application/problem+json", technician.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Forbidden, administrator.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, viewer.StatusCode, await viewer.Content.ReadAsStringAsync(Ct));
        var held = await _superadministrator.GetFromJsonAsync<PagedResult<RoleAssignmentDto>>(
            $"api/v1/tenants/{tree.Client}/role-assignments", Signal9Json.Options, Ct);
        CollectionAssert.AreEqual(new[] { BuiltInRoles.Viewer.Id }, held!.Items.Where(a => a.UserId == user.Id).Select(a => a.RoleId).ToArray());
    }

    [TestMethod]
    public async Task A_peer_administrator_at_your_own_assigned_tenant_cant_be_revoked()
    {
        var tree = await TreeAsync();
        var (peerUser, _) = await AdministratorAsync(tree.Msp);
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var peer = await AssignmentOfAsync(tree.Msp, peerUser.Id);
        var technician = await AssignAsync(tree.Msp, (await CreateUserAsync(tree.Msp, EntraUser.WithMfa())).Id, "Technician");
        var below = await AssignAsync(tree.Client, (await CreateUserAsync(tree.Client, EntraUser.WithMfa())).Id, "Administrator");

        var revokePeer = await administrator.DeleteAsync($"api/v1/tenants/{tree.Msp}/role-assignments/{peer.Id}", Ct);
        var revokeTechnician = await administrator.DeleteAsync($"api/v1/tenants/{tree.Msp}/role-assignments/{technician.Id}", Ct);
        var revokeBelow = await administrator.DeleteAsync($"api/v1/tenants/{tree.Client}/role-assignments/{below.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, revokePeer.StatusCode);
        Assert.AreEqual("application/problem+json", revokePeer.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.OK,
            (await _superadministrator.GetAsync($"api/v1/tenants/{tree.Msp}/role-assignments/{peer.Id}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, revokeTechnician.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, revokeBelow.StatusCode);
    }

    [TestMethod]
    public async Task An_administrator_above_can_revoke_an_administrator_at_a_descendant()
    {
        var tree = await TreeAsync();
        var (mspUser, _) = await AdministratorAsync(tree.Msp);
        await AdministratorAsync(tree.Msp);
        using var rootAdministrator = await PersonAsync(tree.Root, ("Administrator", tree.Root));
        var assignment = await AssignmentOfAsync(tree.Msp, mspUser.Id);

        var revoked = await rootAdministrator.DeleteAsync($"api/v1/tenants/{tree.Msp}/role-assignments/{assignment.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, revoked.StatusCode, await revoked.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Role_assignments_are_revoked_and_granted_rather_than_downgraded_in_place()
    {
        var tree = await TreeAsync();
        var (peerUser, _) = await AdministratorAsync(tree.Msp);
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var peer = await AssignmentOfAsync(tree.Msp, peerUser.Id);
        using var downgrade = new StringContent($$"""{"roleId":"{{BuiltInRoles.Viewer.Id}}"}""", Encoding.UTF8, "application/merge-patch+json");

        var patched = await administrator.PatchAsync($"api/v1/tenants/{tree.Msp}/role-assignments/{peer.Id}", downgrade, Ct);

        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, patched.StatusCode);
        Assert.AreEqual(BuiltInRoles.Administrator.Id, (await AssignmentOfAsync(tree.Msp, peerUser.Id)).RoleId);
    }

    [TestMethod]
    public async Task The_last_administrator_of_a_tenant_cant_remove_themselves()
    {
        var tree = await TreeAsync();
        var (firstUser, first) = await AdministratorAsync(tree.Msp);
        using var _ = first;
        var own = await AssignmentOfAsync(tree.Msp, firstUser.Id);

        var alone = await first.DeleteAsync($"api/v1/tenants/{tree.Msp}/role-assignments/{own.Id}", Ct);
        Assert.AreEqual(HttpStatusCode.Conflict, alone.StatusCode);
        Assert.AreEqual("application/problem+json", alone.Content.Headers.ContentType?.MediaType);

        var (secondUser, second) = await AdministratorAsync(tree.Msp);
        using var __ = second;
        var withPeer = await first.DeleteAsync($"api/v1/tenants/{tree.Msp}/role-assignments/{own.Id}", Ct);
        Assert.AreEqual(HttpStatusCode.NoContent, withPeer.StatusCode, await withPeer.Content.ReadAsStringAsync(Ct));

        var secondOwn = await AssignmentOfAsync(tree.Msp, secondUser.Id);
        var last = await second.DeleteAsync($"api/v1/tenants/{tree.Msp}/role-assignments/{secondOwn.Id}", Ct);
        Assert.AreEqual(HttpStatusCode.Conflict, last.StatusCode);
    }

    [TestMethod]
    public async Task An_administrator_above_keeps_the_tenant_administered_so_its_own_last_one_may_leave()
    {
        var tree = await TreeAsync();
        await AdministratorAsync(tree.Root);
        var (clientUser, client) = await AdministratorAsync(tree.Client);
        using var _ = client;
        var own = await AssignmentOfAsync(tree.Client, clientUser.Id);

        var left = await client.DeleteAsync($"api/v1/tenants/{tree.Client}/role-assignments/{own.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, left.StatusCode, await left.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task A_superadministrator_may_remove_a_tenants_last_administrator()
    {
        var tree = await TreeAsync();
        var (user, administrator) = await AdministratorAsync(tree.Msp);
        administrator.Dispose();
        var assignment = await AssignmentOfAsync(tree.Msp, user.Id);

        var revoked = await _superadministrator.DeleteAsync($"api/v1/tenants/{tree.Msp}/role-assignments/{assignment.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, revoked.StatusCode, await revoked.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Revoking_an_assignment_takes_effect_on_the_holders_next_request()
    {
        var tree = await TreeAsync();
        var person = EntraUser.WithMfa();
        var user = await CreateUserAsync(tree.Client, person);
        var assignment = await AssignAsync(tree.Client, user.Id, "Viewer");
        // Not their last, which would end their session too (#119, SessionCheckTests).
        await AssignAsync(tree.Sibling, user.Id, "Viewer");
        using var viewer = await _factory.CreatePersonClientAsync(person);
        Assert.AreEqual(HttpStatusCode.OK, (await viewer.GetAsync($"api/v1/tenants/{tree.Client}", Ct)).StatusCode);

        var revoked = await _superadministrator.DeleteAsync($"api/v1/tenants/{tree.Client}/role-assignments/{assignment.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await viewer.GetAsync($"api/v1/tenants/{tree.Client}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await viewer.GetAsync($"api/v1/tenants/{tree.Sibling}", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Revoking_needs_a_token_with_the_scope_and_honours_if_match()
    {
        var tree = await TreeAsync();
        var assignment = await AssignAsync(tree.Client, (await CreateUserAsync(tree.Client, EntraUser.WithMfa())).Id, "Viewer");
        var url = $"api/v1/tenants/{tree.Client}/role-assignments/{assignment.Id}";
        using var anonymous = _factory.CreateAnonymousClient();
        using var readOnly = await _factory.CreateClientAsync(Scopes.RoleAssignments.Read);

        var unauthenticated = await anonymous.DeleteAsync(url, Ct);
        var withoutScope = await readOnly.DeleteAsync(url, Ct);
        using var stale = new HttpRequestMessage(HttpMethod.Delete, url);
        stale.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue("\"stale\""));
        var mismatched = await _superadministrator.SendAsync(stale, Ct);
        var current = await _superadministrator.GetAsync(url, Ct);
        using var fresh = new HttpRequestMessage(HttpMethod.Delete, url);
        fresh.Headers.IfMatch.Add(current.Headers.ETag!);
        var matched = await _superadministrator.SendAsync(fresh, Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, withoutScope.StatusCode);
        StringAssert.Contains(withoutScope.Headers.WwwAuthenticate.ToString(), "insufficient_scope");
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, mismatched.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, matched.StatusCode);
    }

    [TestMethod]
    public async Task Grants_and_revocations_are_audited_at_the_tenant()
    {
        var tree = await TreeAsync();
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var user = await CreateUserAsync(tree.Client, EntraUser.WithMfa());
        var granted = await GrantAsync(administrator, tree.Client, user.Id, "Technician");
        var assignment = (await granted.Content.ReadFromJsonAsync<RoleAssignmentDto>(Signal9Json.Options, Ct))!;

        Assert.AreEqual(HttpStatusCode.NoContent, (await administrator.DeleteAsync(granted.Headers.Location, Ct)).StatusCode);

        var trail = (await administrator.GetFromJsonAsync<PagedResult<AuditEntryDto>>("api/v1/audit-entries?pageSize=100",
            Signal9Json.Options, Ct))!.Items.Where(e => e.TargetId == assignment.Id).ToList();
        var grant = trail.Single(e => e.Action == AuditActions.RoleAssignmentCreated);
        var revocation = trail.Single(e => e.Action == AuditActions.RoleAssignmentDeleted);
        foreach (var entry in new[] { grant, revocation })
        {
            Assert.AreEqual(tree.Client, entry.TenantId);
            Assert.AreEqual("role-assignment", entry.TargetType);
            Assert.AreEqual(user.Id, entry.UserId);
            Assert.AreEqual("Technician", entry.RoleName);
            Assert.AreEqual(AuditOutcome.Succeeded, entry.Outcome);
            Assert.IsFalse(entry.Actor.Superadministrator);
        }
    }

    [TestMethod]
    public async Task Require_ancestor_administrator_admits_only_an_administrator_above_the_tenant()
    {
        var tree = await TreeAsync();
        using var api = ProbingApi();
        using var atTenant = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        using var above = await PersonAsync(tree.Root, ("Administrator", tree.Root));
        using var technicianAbove = await PersonAsync(tree.Root, ("Technician", tree.Root), ("Administrator", tree.Msp));
        using var beside = await PersonAsync(tree.Sibling, ("Administrator", tree.Sibling));

        Assert.AreEqual(HttpStatusCode.Forbidden, (await ProbeAsync(api, atTenant, tree.Msp)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await ProbeAsync(api, technicianAbove, tree.Msp)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await ProbeAsync(api, atTenant, tree.Client)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await ProbeAsync(api, above, tree.Msp)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await ProbeAsync(api, _superadministrator, tree.Msp)).StatusCode);
        // Outside the caller's subtree the tenant reads as missing, as it would without the attribute.
        Assert.AreEqual(HttpStatusCode.NotFound, (await ProbeAsync(api, beside, tree.Msp)).StatusCode);
    }

    [TestMethod]
    public async Task Require_ancestor_administrator_is_shown_in_the_openapi_document()
    {
        using var api = ProbingApi();
        using var client = api.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>("docs/openapi/v1.json", Ct);

        var probe = document.GetProperty("paths").GetProperty("/test/ancestor-administrator-probe/{tenantId}").GetProperty("get");
        var requirement = probe.GetProperty(DocumentedRequirements.Extension).EnumerateArray().Single();
        Assert.AreEqual("ancestor-administrator", requirement.GetProperty("name").GetString());
        StringAssert.Contains(probe.GetProperty("description").GetString(), requirement.GetProperty("description").GetString());
    }

    private sealed record Tree(Guid Root, Guid Msp, Guid Client, Guid Sibling);

    /// <summary>Root ─ MSP ─ Client, and Root ─ Sibling.</summary>
    private async Task<Tree> TreeAsync()
    {
        var root = await _factory.CreateTenantAsync(_superadministrator, "Root");
        var msp = await _factory.CreateTenantAsync(_superadministrator, "Msp", parentTenantId: root.Id);
        var client = await _factory.CreateTenantAsync(_superadministrator, "Client", parentTenantId: msp.Id);
        var sibling = await _factory.CreateTenantAsync(_superadministrator, "Sibling", parentTenantId: root.Id);
        return new Tree(root.Id, msp.Id, client.Id, sibling.Id);
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

    private async Task<(UserDto User, HttpClient Client)> AdministratorAsync(Guid tenantId)
    {
        var person = EntraUser.WithMfa();
        var user = await CreateUserAsync(tenantId, person);
        await AssignAsync(tenantId, user.Id, "Administrator");
        return (user, await _factory.CreatePersonClientAsync(person));
    }

    private async Task<UserDto> CreateUserAsync(Guid tenantId, EntraUser person)
    {
        var response = await _superadministrator.PostAsJsonAsync("api/v1/users", new CreateUserRequest
        {
            TenantId = tenantId,
            EntraTenantId = person.TenantId,
            EntraObjectId = person.ObjectId,
            DisplayName = "Alex Wilber",
        }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;
    }

    private Task<HttpResponseMessage> GrantAsync(HttpClient caller, Guid tenantId, Guid userId, string role) =>
        caller.PostAsJsonAsync($"api/v1/tenants/{tenantId}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = userId, RoleId = RoleId(role) }, Signal9Json.Options, Ct);

    private async Task<RoleAssignmentDto> AssignAsync(Guid tenantId, Guid userId, string role)
    {
        var response = await GrantAsync(_superadministrator, tenantId, userId, role);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RoleAssignmentDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<RoleAssignmentDto> AssignmentOfAsync(Guid tenantId, Guid userId) =>
        (await _superadministrator.GetFromJsonAsync<PagedResult<RoleAssignmentDto>>(
            $"api/v1/tenants/{tenantId}/role-assignments?pageSize=100", Signal9Json.Options, Ct))!.Items.Single(a => a.UserId == userId);

    private static Guid RoleId(string role) => BuiltInRoles.All.Single(r => r.Name == role).Id;

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<OSI.Signal9.API.ApiHost> ProbingApi() =>
        _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(AncestorAdministratorProbeController).Assembly)));

    private async Task<HttpResponseMessage> ProbeAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<OSI.Signal9.API.ApiHost> api, HttpClient caller, Guid tenantId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"test/ancestor-administrator-probe/{tenantId}");
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

/// <summary>An endpoint only an ancestor's Administrator may use: 200 when the tenant is visible, 404 when it isn't.</summary>
[ApiController]
[Route("test/ancestor-administrator-probe")]
public sealed class AncestorAdministratorProbeController(Signal9DbContext db) : ControllerBase
{
    [RequireScope(Scopes.Tenants.Read)]
    [RequireAncestorAdministrator]
    [HttpGet("{tenantId:guid}")]
    public async Task<IActionResult> Get(Guid tenantId, CancellationToken cancellationToken) =>
        await db.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken) ? Ok() : NotFound();
}
