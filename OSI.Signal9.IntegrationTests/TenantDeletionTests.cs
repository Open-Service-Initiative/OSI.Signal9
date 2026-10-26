using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.ApiApplications;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Contracts.Invitations;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Contracts.Users;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Tenant deletion (#134, stories 11-13, 21): only from above, once child tenants and live devices are gone; it takes
/// everything the tenant owns with it, users included, and keeps the audit trail readable by the ancestors it had.
/// </summary>
[TestClass]
public sealed class TenantDeletionTests : IDisposable
{
    private static readonly Guid Uuid = Guid.Parse("4c4c4544-0042-3510-8058-b4c04f4d5a31");

    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public TenantDeletionTests(TestContext testContext)
    {
        _testContext = testContext;
        // The fixture's own client stands in for a Superadministrator.
        _superadministrator = _factory.CreateSuperadministratorClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Deletion_is_refused_with_409_listing_the_child_tenants_and_devices_that_remain()
    {
        var tree = await TreeAsync();
        var site = await _factory.CreateTenantAsync(_superadministrator, "Site", parentTenantId: tree.Client);
        using var agent = await _factory.EnrollAgentAsync(tree.Client, "PC01");
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        var response = await administrator.DeleteAsync(TenantUrl(tree.Client), Ct);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        CollectionAssert.AreEqual(new[] { site.Id }, Ids(problem.GetProperty("childTenantIds")));
        CollectionAssert.AreEqual(new[] { agent.DeviceId }, Ids(problem.GetProperty("deviceIds")));
        Assert.AreEqual(HttpStatusCode.OK, (await _superadministrator.GetAsync(TenantUrl(tree.Client), Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Once_its_devices_and_child_tenants_are_gone_the_tenant_can_be_deleted()
    {
        var tree = await TreeAsync();
        var site = await _factory.CreateTenantAsync(_superadministrator, "Site", parentTenantId: tree.Client);
        using var agent = await _factory.EnrollAgentAsync(tree.Client, "PC01");
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        Assert.AreEqual(HttpStatusCode.NoContent, (await administrator.DeleteAsync(TenantUrl(site.Id), Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, (await _superadministrator.DeleteAsync($"api/v1/devices/{agent.DeviceId}", Ct)).StatusCode);

        var response = await administrator.DeleteAsync(TenantUrl(tree.Client), Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NotFound, (await _superadministrator.GetAsync(TenantUrl(tree.Client), Ct)).StatusCode);
    }

    [TestMethod]
    public async Task An_Administrator_cannot_delete_their_own_assigned_tenant_but_one_above_it_can()
    {
        var tree = await TreeAsync();
        using var clientAdministrator = await PersonAsync(tree.Msp, ("Administrator", tree.Client));
        using var mspAdministrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        var own = await clientAdministrator.DeleteAsync(TenantUrl(tree.Client), Ct);
        var preview = await clientAdministrator.GetAsync(PreviewUrl(tree.Client), Ct);
        var above = await mspAdministrator.DeleteAsync(TenantUrl(tree.Client), Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, own.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, preview.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, above.StatusCode, await above.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task A_tenant_outside_the_callers_reach_is_not_found()
    {
        var tree = await TreeAsync();
        using var siblingAdministrator = await PersonAsync(tree.Sibling, ("Administrator", tree.Sibling));

        Assert.AreEqual(HttpStatusCode.NotFound, (await siblingAdministrator.DeleteAsync(TenantUrl(tree.Client), Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await siblingAdministrator.GetAsync(PreviewUrl(tree.Client), Ct)).StatusCode);
    }

    [TestMethod]
    public async Task The_cascade_revokes_enrollment_tokens_invitations_and_api_applications_and_ends_role_assignments()
    {
        var tree = await TreeAsync();
        var token = await _factory.IssueEnrollmentTokenAsync(tree.Client);
        var invitation = await InviteAsync(tree.Client);
        var (application, secret) = await RegisterApplicationAsync(tree.Client);
        using var applicationClient = await SecretClientAsync(application.ClientId, secret, "tenants.read");
        Assert.AreEqual(HttpStatusCode.OK, (await applicationClient.GetAsync(TenantUrl(tree.Client), Ct)).StatusCode);
        var mspUser = await CreateUserAsync(tree.Msp, EntraUser.WithMfa());
        var outsider = await AssignAsync(tree.Client, mspUser.Id, "Technician");
        await AssignAsync(tree.Msp, mspUser.Id, "Viewer");
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        var deleted = await administrator.DeleteAsync(TenantUrl(tree.Client), Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        var enrolled = await _factory.EnrollAsync(token.Secret!, TestCertificates.AgentCertificate());
        Assert.AreEqual(HttpStatusCode.BadRequest, enrolled.StatusCode, "The token no longer exists.");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await applicationClient.GetAsync("api/v1/tenants", Ct)).StatusCode);
        Assert.AreNotEqual(HttpStatusCode.OK, (await SecretTokenResponseAsync(application.ClientId, secret, "tenants.read")).StatusCode);
        // The user owned elsewhere keeps their account and their other role assignments.
        var kept = await _superadministrator.GetFromJsonAsync<PagedResult<RoleAssignmentDto>>(
            $"api/v1/tenants/{tree.Msp}/role-assignments", Signal9Json.Options, Ct);
        Assert.IsTrue(kept!.Items.Any(a => a.UserId == mspUser.Id));
        Assert.AreEqual(HttpStatusCode.OK, (await _superadministrator.GetAsync($"api/v1/users/{mspUser.Id}", Ct)).StatusCode);

        // Each cascaded removal is audited at the deleted tenant, and the tenant's ancestors still read it there.
        var trail = await TrailAsync(administrator);
        Assert.IsTrue(trail.Any(e => e.Action == AuditActions.EnrollmentTokenRevoked && e.TargetId == token.Id && e.TenantId == tree.Client));
        Assert.IsTrue(trail.Any(e => e.Action == AuditActions.InvitationRevoked && e.TargetId == invitation && e.TenantId == tree.Client));
        Assert.IsTrue(trail.Any(e => e.Action == AuditActions.ApiApplicationDeleted && e.TargetId == application.Id && e.TenantId == tree.Client));
        Assert.IsTrue(trail.Any(e => e.Action == AuditActions.RoleAssignmentDeleted && e.TargetId == outsider.Id && e.TenantId == tree.Client));
        var deletion = trail.Single(e => e.Action == AuditActions.TenantDeleted && e.TargetId == tree.Client);
        Assert.AreEqual(tree.Msp, deletion.TenantId);
        Assert.IsTrue(trail.IndexOf(deletion) < trail.FindIndex(e => e.Action == AuditActions.EnrollmentTokenRevoked && e.TargetId == token.Id),
            "The deletion is recorded after its cascade.");
    }

    [TestMethod]
    public async Task A_user_owned_by_the_deleted_tenant_is_deleted_with_an_Administrator_assignment_in_another_tree_which_is_audited_there()
    {
        var tree = await TreeAsync();
        var otherRoot = await _factory.CreateTenantAsync(_superadministrator, "Other root");
        var person = EntraUser.WithMfa();
        var owned = await CreateUserAsync(tree.Client, person, "Megan Bowen");
        var outside = await AssignAsync(otherRoot.Id, owned.Id, "Administrator");
        var inside = await AssignAsync(tree.Client, owned.Id, "Viewer");
        using var ownedClient = await _factory.CreatePersonClientAsync(person);
        using var otherAdministrator = await PersonAsync(otherRoot.Id, ("Administrator", otherRoot.Id));
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        var preview = await administrator.GetFromJsonAsync<TenantDeletionPreviewDto>(PreviewUrl(tree.Client), Signal9Json.Options, Ct);
        var deleted = await administrator.DeleteAsync(TenantUrl(tree.Client), Ct);

        Assert.AreEqual(tree.Client, preview!.TenantId);
        Assert.IsEmpty(preview.ChildTenantIds);
        Assert.IsEmpty(preview.DeviceIds);
        Assert.IsEmpty(preview.TenantsLeftWithoutAdministrator);
        var previewed = Assert.ContainsSingle(preview.OutsideRoleAssignments);
        Assert.AreEqual((outside.Id, otherRoot.Id, owned.Id), (previewed.Id, previewed.TenantId, previewed.UserId));

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NotFound, (await _superadministrator.GetAsync($"api/v1/users/{owned.Id}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await _superadministrator.GetAsync($"api/v1/tenants/{otherRoot.Id}/role-assignments/{outside.Id}", Ct)).StatusCode);
        // Their sessions and tokens end with them.
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await ownedClient.GetAsync("api/v1/tenants", Ct)).StatusCode);

        // The other tree sees why its Administrator went.
        var there = (await TrailAsync(otherAdministrator)).Single(e => e.Action == AuditActions.RoleAssignmentDeleted && e.TargetId == outside.Id);
        Assert.AreEqual(otherRoot.Id, there.TenantId);
        Assert.AreEqual(owned.Id, there.UserId);
        Assert.AreEqual("tenant-deleted", there.Detail);
        var trail = await TrailAsync(administrator);
        Assert.IsTrue(trail.Any(e => e.Action == AuditActions.RoleAssignmentDeleted && e.TargetId == inside.Id && e.TenantId == tree.Client));
        Assert.IsTrue(trail.Any(e => e.Action == AuditActions.UserDeleted && e.TargetId == owned.Id && e.TenantId == tree.Client));
        Assert.IsFalse(trail.Any(e => e.TargetId == outside.Id), "The other tree's entry stays there.");
    }

    [TestMethod]
    public async Task Deletion_is_refused_if_it_would_leave_a_tenant_without_an_Administrator_unless_by_a_Superadministrator()
    {
        var tree = await TreeAsync();
        var otherRoot = await _factory.CreateTenantAsync(_superadministrator, "Other root");
        var owned = await CreateUserAsync(tree.Client, EntraUser.WithMfa());
        await AssignAsync(otherRoot.Id, owned.Id, "Administrator");
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        var preview = await administrator.GetFromJsonAsync<TenantDeletionPreviewDto>(PreviewUrl(tree.Client), Signal9Json.Options, Ct);
        var refused = await administrator.DeleteAsync(TenantUrl(tree.Client), Ct);
        using var superadministrator = await _factory.CreatePersonClientAsync(_factory.Superadministrator());
        var deleted = await superadministrator.DeleteAsync(TenantUrl(tree.Client), Ct);

        CollectionAssert.AreEqual(new[] { otherRoot.Id }, preview!.TenantsLeftWithoutAdministrator.ToArray());
        Assert.AreEqual(HttpStatusCode.Conflict, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(Ct);
        CollectionAssert.AreEqual(new[] { otherRoot.Id }, Ids(problem.GetProperty("tenantsLeftWithoutAdministrator")));
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NotFound, (await _superadministrator.GetAsync($"api/v1/users/{owned.Id}", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task An_Administrator_above_the_outside_assignment_keeps_the_tenant_managed()
    {
        var tree = await TreeAsync();
        var owned = await CreateUserAsync(tree.Client, EntraUser.WithMfa());
        // The Sibling's only Administrator at it, but the Root has one above it.
        await AssignAsync(tree.Sibling, owned.Id, "Administrator");
        using var rootAdministrator = await PersonAsync(tree.Root, ("Administrator", tree.Root));

        var deleted = await rootAdministrator.DeleteAsync(TenantUrl(tree.Client), Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task An_agent_awaiting_identification_in_the_tenant_goes_with_it()
    {
        var tree = await TreeAsync();
        using var first = await _factory.EnrollAgentAsync(tree.Client, "PC01", r => r with { SmbiosUuid = Uuid });
        using var pending = await _factory.EnrollAgentAsync(tree.Client, "PC01-NEW", r => r with { SmbiosUuid = Uuid });
        Assert.AreEqual(AgentStatus.NeedsDecision, pending.Agent.Status);
        Assert.AreEqual(HttpStatusCode.NoContent, (await _superadministrator.DeleteAsync($"api/v1/devices/{first.DeviceId}", Ct)).StatusCode);
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        var deleted = await administrator.DeleteAsync(TenantUrl(tree.Client), Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NotFound, (await _superadministrator.GetAsync($"api/v1/agents/{pending.Id}", Ct)).StatusCode);
        var token = await _factory.RequestAgentTokenAsync(pending.Id, pending.Certificate);
        Assert.AreNotEqual(HttpStatusCode.OK, token.StatusCode, "Its OAuth client goes with it.");
    }

    [TestMethod]
    public async Task After_a_transfer_and_deletion_the_new_parent_reads_the_trail_and_the_old_one_cannot()
    {
        var tree = await TreeAsync();
        var token = await _factory.IssueEnrollmentTokenAsync(tree.Client);
        using var mspAdministrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        using var siblingAdministrator = await PersonAsync(tree.Sibling, ("Administrator", tree.Sibling));
        await TransferAsync(tree.Client, tree.Sibling);

        var deleted = await siblingAdministrator.DeleteAsync(TenantUrl(tree.Client), Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        bool AboutClient(AuditEntryDto e) => e.TenantId == tree.Client;
        var newParent = (await TrailAsync(siblingAdministrator)).Where(AboutClient).ToList();
        var oldParent = (await TrailAsync(mspAdministrator)).Where(AboutClient).ToList();
        Assert.IsTrue(newParent.Any(e => e.Action == AuditActions.TenantCreated && e.TargetId == tree.Client));
        Assert.IsTrue(newParent.Any(e => e.Action == AuditActions.EnrollmentTokenCreated && e.TargetId == token.Id));
        Assert.IsTrue(newParent.All(e => e.TenantName is null), "A deleted tenant's record is anonymous.");
        Assert.IsEmpty(oldParent);
        Assert.IsTrue((await TrailAsync(_superadministrator)).Any(AboutClient));
    }

    [TestMethod]
    public async Task The_trail_of_a_deleted_tenant_stays_readable_at_every_ancestor_it_had()
    {
        var tree = await TreeAsync();
        using var rootAdministrator = await PersonAsync(tree.Root, ("Administrator", tree.Root));
        using var siblingAdministrator = await PersonAsync(tree.Sibling, ("Administrator", tree.Sibling));

        using var superadministrator = await _factory.CreatePersonClientAsync(_factory.Superadministrator());
        Assert.AreEqual(HttpStatusCode.NoContent, (await superadministrator.DeleteAsync(TenantUrl(tree.Client), Ct)).StatusCode);

        Assert.IsTrue((await TrailAsync(rootAdministrator)).Any(e => e.TenantId == tree.Client));
        Assert.IsFalse((await TrailAsync(siblingAdministrator)).Any(e => e.TenantId == tree.Client));
    }

    [TestMethod]
    public async Task Previewing_a_tenant_with_child_tenants_and_devices_lists_them()
    {
        var tree = await TreeAsync();
        using var agent = await _factory.EnrollAgentAsync(tree.Msp, "PC01");
        using var administrator = await PersonAsync(tree.Root, ("Administrator", tree.Root));

        var preview = await administrator.GetFromJsonAsync<TenantDeletionPreviewDto>(PreviewUrl(tree.Msp), Signal9Json.Options, Ct);

        CollectionAssert.AreEqual(new[] { tree.Client }, preview!.ChildTenantIds.ToArray());
        CollectionAssert.AreEqual(new[] { agent.DeviceId }, preview.DeviceIds.ToArray());
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string TenantUrl(Guid tenantId) => $"api/v1/tenants/{tenantId}";

    private static string PreviewUrl(Guid tenantId) => $"api/v1/tenants/{tenantId}:preview-delete";

    private static Guid[] Ids(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetGuid())];

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

    /// <summary>A newly signed-in person with MFA, owned by <paramref name="owner"/>, holding these role assignments.</summary>
    private async Task<HttpClient> PersonAsync(Guid owner, params (string Role, Guid Tenant)[] assignments)
    {
        var person = EntraUser.WithMfa();
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

    private async Task<RoleAssignmentDto> AssignAsync(Guid tenantId, Guid userId, string role)
    {
        var response = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenantId}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = userId, RoleId = await RoleIdAsync(role) }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RoleAssignmentDto>(Signal9Json.Options, Ct))!;
    }

    private List<RoleDto>? _roles;

    private async Task<Guid> RoleIdAsync(string name)
    {
        _roles ??= [.. (await _superadministrator.GetFromJsonAsync<PagedResult<RoleDto>>("api/v1/roles", Signal9Json.Options, Ct))!.Items];
        return _roles.Single(r => r.Name == name).Id;
    }

    private async Task<Guid> InviteAsync(Guid tenantId)
    {
        var response = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenantId}/invitations",
            new CreateInvitationRequest { RoleId = await RoleIdAsync("Technician") }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<InvitationDto>(Signal9Json.Options, Ct))!.Id;
    }

    private async Task<(ApiApplicationDto Application, string Secret)> RegisterApplicationAsync(Guid tenantId)
    {
        using var superadministrator = await _factory.CreatePersonClientAsync(_factory.Superadministrator());
        var registered = await superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenantId}/api-applications",
            new CreateApiApplicationRequest
            {
                DisplayName = "PSA sync",
                RoleId = await RoleIdAsync("Viewer"),
                TokenEndpointAuthMethod = TokenEndpointAuthMethods.ClientSecret,
                Scopes = ["tenants.read"],
            }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, registered.StatusCode, await registered.Content.ReadAsStringAsync(Ct));
        var application = (await registered.Content.ReadFromJsonAsync<ApiApplicationDto>(Signal9Json.Options, Ct))!;
        var credential = await superadministrator.PostAsJsonAsync(
            $"api/v1/tenants/{tenantId}/api-applications/{application.Id}/credentials",
            new CreateApiApplicationCredentialRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, credential.StatusCode, await credential.Content.ReadAsStringAsync(Ct));
        return (application, (await credential.Content.ReadFromJsonAsync<ApiApplicationCredentialDto>(Signal9Json.Options, Ct))!.Secret!);
    }

    private async Task<HttpResponseMessage> SecretTokenResponseAsync(string clientId, string secret, params string[] scopes)
    {
        using var auth = _factory.Auth.CreateClient();
        return await auth.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["scope"] = string.Join(' ', scopes),
        }), Ct);
    }

    private async Task<HttpClient> SecretClientAsync(string clientId, string secret, params string[] scopes)
    {
        var response = await SecretTokenResponseAsync(clientId, secret, scopes);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var token = (await response.Content.ReadFromJsonAsync<AccessToken>(Ct))!;
        var client = _factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        return client;
    }

    /// <summary>Moves a tenant as a Superadministrator, who is on both sides of every transfer.</summary>
    private async Task TransferAsync(Guid tenantId, Guid toParentTenantId)
    {
        var proposed = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenantId}/transfers",
            new ProposeTenantTransferRequest { ToParentTenantId = toParentTenantId }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, proposed.StatusCode, await proposed.Content.ReadAsStringAsync(Ct));
        var transfer = (await proposed.Content.ReadFromJsonAsync<TenantTransferDto>(Signal9Json.Options, Ct))!;
        var accepted = await _superadministrator.PostAsync($"api/v1/tenants/{tenantId}/transfers/{transfer.Id}:accept", null, Ct);
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, await accepted.Content.ReadAsStringAsync(Ct));
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
