using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Users;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Enrollment tokens (#127, stories 92-94): issued per tenant, shown once, revocable, multi-use and non-expiring unless
/// limited. Every test drives the public API as a Technician or an auditor would.
/// </summary>
[TestClass]
public sealed class EnrollmentTokensTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public EnrollmentTokensTests(TestContext testContext)
    {
        _testContext = testContext;
        // The fixture's own client stands in for a Superadministrator.
        _superadministrator = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task A_technician_issues_a_token_whose_secret_is_shown_once_and_defaults_to_multi_use_and_non_expiring()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));

        var response = await technician.PostAsJsonAsync($"api/v1/tenants/{tree.Client}/enrollment-tokens",
            new CreateEnrollmentTokenRequest { Name = "Head office installer" }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.IsTrue(response.Headers.CacheControl?.NoStore, "A response carrying a secret is never cached.");
        var issued = (await response.Content.ReadFromJsonAsync<EnrollmentTokenDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual($"/api/v1/tenants/{tree.Client}/enrollment-tokens/{issued.Id}", response.Headers.Location?.AbsolutePath);
        Assert.AreEqual(response.Headers.Location?.AbsolutePath, issued.Url);
        Assert.AreEqual(tree.Client, issued.TenantId);
        Assert.AreEqual("Head office installer", issued.Name);
        Assert.AreEqual(EnrollmentTokenStatus.Active, issued.Status);
        Assert.IsNull(issued.MaxUses, "Multi-use by default.");
        Assert.IsNull(issued.ExpiresAt, "Non-expiring by default.");
        Assert.IsNotNull(issued.Secret);
        Assert.IsGreaterThanOrEqualTo(43, issued.Secret.Length, "At least 256 bits, base64url-encoded.");

        var fetched = await technician.GetFromJsonAsync<EnrollmentTokenDto>(issued.Url, Signal9Json.Options, Ct);
        var listed = await technician.GetFromJsonAsync<PagedResult<EnrollmentTokenDto>>(
            $"api/v1/tenants/{tree.Client}/enrollment-tokens", Signal9Json.Options, Ct);
        Assert.IsNull(fetched!.Secret, "The secret is shown only when it's issued.");
        Assert.AreEqual(issued.Id, listed!.Items.Single().Id);
        Assert.IsNull(listed.Items.Single().Secret);
    }

    [TestMethod]
    public async Task A_token_can_be_single_use_limited_or_expiring_and_shows_as_expired_once_its_expiry_passes()
    {
        var tree = await TreeAsync();
        var expiresAt = _factory.Time.GetUtcNow().AddMinutes(10);

        var singleUse = await IssueAsync(_superadministrator, tree.Client, new CreateEnrollmentTokenRequest { Name = "One laptop", MaxUses = 1 });
        var limited = await IssueAsync(_superadministrator, tree.Client,
            new CreateEnrollmentTokenRequest { Name = "Rollout", MaxUses = 50, ExpiresAt = expiresAt });
        _factory.Time.Advance(TimeSpan.FromMinutes(10));
        var expired = await _superadministrator.GetFromJsonAsync<EnrollmentTokenDto>(limited.Url, Signal9Json.Options, Ct);

        Assert.AreEqual(1, singleUse.MaxUses);
        Assert.AreEqual(50, limited.MaxUses);
        Assert.AreEqual(expiresAt, limited.ExpiresAt);
        Assert.AreEqual(EnrollmentTokenStatus.Active, limited.Status);
        Assert.AreEqual(EnrollmentTokenStatus.Expired, expired!.Status);
    }

    [TestMethod]
    [DataRow("""{"name":"Installer","maxUses":0}""", "MaxUses")]
    [DataRow("""{"name":"Installer","expiresAt":"2000-01-01T00:00:00Z"}""", "ExpiresAt")]
    [DataRow("""{"name":""}""", "Name")]
    public async Task An_invalid_token_request_is_refused_with_400(string body, string field)
    {
        var tree = await TreeAsync();

        var response = await _superadministrator.PostAsync($"api/v1/tenants/{tree.Client}/enrollment-tokens",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Rotating_returns_a_new_secret_once_and_keeps_the_token()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));
        var issued = await IssueAsync(technician, tree.Client);

        var response = await technician.PostAsync($"{issued.Url}:rotate", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        var rotated = (await response.Content.ReadFromJsonAsync<EnrollmentTokenDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(issued.Id, rotated.Id);
        Assert.AreEqual(EnrollmentTokenStatus.Active, rotated.Status);
        Assert.IsNotNull(rotated.Secret);
        Assert.AreNotEqual(issued.Secret, rotated.Secret);
        Assert.IsNotNull(rotated.RotatedAt);
        var fetched = await technician.GetFromJsonAsync<EnrollmentTokenDto>(issued.Url, Signal9Json.Options, Ct);
        Assert.IsNull(fetched!.Secret);
    }

    [TestMethod]
    public async Task Revoking_is_final_and_a_revoked_token_cannot_be_revoked_or_rotated_again()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));
        var issued = await IssueAsync(technician, tree.Client);

        var revoked = await technician.PostAsync($"{issued.Url}:revoke", null, Ct);
        var again = await technician.PostAsync($"{issued.Url}:revoke", null, Ct);
        var rotate = await technician.PostAsync($"{issued.Url}:rotate", null, Ct);

        Assert.AreEqual(HttpStatusCode.OK, revoked.StatusCode, await revoked.Content.ReadAsStringAsync(Ct));
        var dto = (await revoked.Content.ReadFromJsonAsync<EnrollmentTokenDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(EnrollmentTokenStatus.Revoked, dto.Status);
        Assert.IsNotNull(dto.RevokedAt);
        Assert.IsNull(dto.Secret);
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        Assert.AreEqual("application/problem+json", again.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Conflict, rotate.StatusCode);
    }

    [TestMethod]
    public async Task A_stale_if_match_is_refused_with_412()
    {
        var tree = await TreeAsync();
        var issued = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tree.Client}/enrollment-tokens",
            new CreateEnrollmentTokenRequest { Name = "Installer" }, Signal9Json.Options, Ct);
        var etag = issued.Headers.ETag!;
        var url = (await issued.Content.ReadFromJsonAsync<EnrollmentTokenDto>(Signal9Json.Options, Ct))!.Url;
        Assert.AreEqual(HttpStatusCode.OK, (await _superadministrator.PostAsync($"{url}:rotate", null, Ct)).StatusCode);

        using var revoke = new HttpRequestMessage(HttpMethod.Post, $"{url}:revoke");
        revoke.Headers.IfMatch.Add(etag);
        var response = await _superadministrator.SendAsync(revoke, Ct);

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode);
    }

    [TestMethod]
    public async Task Tokens_are_listed_within_the_callers_subtree_only()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));
        var atMsp = await IssueAsync(_superadministrator, tree.Msp);
        var atClient = await IssueAsync(_superadministrator, tree.Client);
        var atSibling = await IssueAsync(_superadministrator, tree.Sibling);

        var subtree = await technician.GetFromJsonAsync<PagedResult<EnrollmentTokenDto>>(
            $"api/v1/tenants/{tree.Msp}/enrollment-tokens", Signal9Json.Options, Ct);
        var own = await technician.GetFromJsonAsync<PagedResult<EnrollmentTokenDto>>(
            $"api/v1/tenants/{tree.Msp}/enrollment-tokens?includeDescendants=false", Signal9Json.Options, Ct);
        var fromRoot = await technician.GetAsync($"api/v1/tenants/{tree.Root}/enrollment-tokens", Ct);
        var sibling = await technician.GetAsync(atSibling.Url, Ct);
        var revokeSibling = await technician.PostAsync($"{atSibling.Url}:revoke", null, Ct);
        var issueAtSibling = await technician.PostAsJsonAsync($"api/v1/tenants/{tree.Sibling}/enrollment-tokens",
            new CreateEnrollmentTokenRequest { Name = "Installer" }, Signal9Json.Options, Ct);

        CollectionAssert.AreEquivalent(new[] { atMsp.Id, atClient.Id }, subtree!.Items.Select(t => t.Id).ToArray());
        CollectionAssert.AreEquivalent(new[] { atMsp.Id }, own!.Items.Select(t => t.Id).ToArray());
        Assert.AreEqual(HttpStatusCode.NotFound, fromRoot.StatusCode, "A tenant above the caller's reach is treated as missing.");
        Assert.AreEqual(HttpStatusCode.NotFound, sibling.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, revokeSibling.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, issueAtSibling.StatusCode);
    }

    [TestMethod]
    public async Task A_viewer_can_neither_list_nor_issue_tokens()
    {
        var tree = await TreeAsync();
        using var viewer = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));

        var list = await viewer.GetAsync($"api/v1/tenants/{tree.Client}/enrollment-tokens", Ct);
        var issue = await viewer.PostAsJsonAsync($"api/v1/tenants/{tree.Client}/enrollment-tokens",
            new CreateEnrollmentTokenRequest { Name = "Installer" }, Signal9Json.Options, Ct);

        // The Viewer role grants neither enrollment scope at any tenant, so the tenant is out of reach.
        Assert.AreEqual(HttpStatusCode.NotFound, list.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, issue.StatusCode);
    }

    [TestMethod]
    public async Task Issuing_rotating_and_revoking_are_audited_at_the_tokens_tenant_without_the_secret()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));
        using var mspAdministrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        using var siblingAdministrator = await PersonAsync(tree.Sibling, ("Administrator", tree.Sibling));
        var issued = await IssueAsync(technician, tree.Client);
        var rotated = await (await technician.PostAsync($"{issued.Url}:rotate", null, Ct))
            .Content.ReadFromJsonAsync<EnrollmentTokenDto>(Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.OK, (await technician.PostAsync($"{issued.Url}:revoke", null, Ct)).StatusCode);

        var trail = (await TrailAsync(mspAdministrator)).Where(e => e.TargetId == issued.Id).ToList();
        var beside = (await TrailAsync(siblingAdministrator)).Where(e => e.TargetId == issued.Id).ToList();

        CollectionAssert.AreEqual(
            new[] { AuditActions.EnrollmentTokenRevoked, AuditActions.EnrollmentTokenRotated, AuditActions.EnrollmentTokenCreated },
            trail.Select(e => e.Action).ToArray(), "Newest first.");
        Assert.IsTrue(trail.All(e => e.TenantId == tree.Client && e.TargetType == "enrollment-token"
            && e.Outcome == AuditOutcome.Succeeded && e.Actor.Kind == AuditActorKind.Person));
        Assert.IsTrue(trail.All(e => e.Detail is null
            || (!e.Detail.Contains(issued.Secret!, StringComparison.Ordinal) && !e.Detail.Contains(rotated!.Secret!, StringComparison.Ordinal))));
        Assert.IsEmpty(beside);
    }

    [TestMethod]
    public async Task Deleting_a_tenant_removes_its_tokens()
    {
        var tree = await TreeAsync();
        var site = await _factory.CreateTenantAsync(_superadministrator, "Site", parentTenantId: tree.Client);
        var issued = await IssueAsync(_superadministrator, site.Id);
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        var deleted = await administrator.DeleteAsync($"api/v1/tenants/{site.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await _superadministrator.GetAsync(issued.Url, Ct)).StatusCode);
        var listed = await _superadministrator.GetFromJsonAsync<PagedResult<EnrollmentTokenDto>>(
            $"api/v1/tenants/{tree.Client}/enrollment-tokens", Signal9Json.Options, Ct);
        Assert.IsEmpty(listed!.Items);
    }

    // Helpers

    private async Task<EnrollmentTokenDto> IssueAsync(HttpClient caller, Guid tenantId, CreateEnrollmentTokenRequest? request = null)
    {
        var response = await caller.PostAsJsonAsync($"api/v1/tenants/{tenantId}/enrollment-tokens",
            request ?? new CreateEnrollmentTokenRequest { Name = "Installer" }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<EnrollmentTokenDto>(Signal9Json.Options, Ct))!;
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

    /// <summary>A newly signed-in person, owned by <paramref name="owner"/>, holding these role assignments.</summary>
    private async Task<HttpClient> PersonAsync(Guid owner, params (string Role, Guid Tenant)[] assignments)
    {
        var person = EntraUser.WithMfa();
        var created = await _superadministrator.PostAsJsonAsync("api/v1/users", new CreateUserRequest
        {
            TenantId = owner,
            EntraTenantId = person.TenantId,
            EntraObjectId = person.ObjectId,
            DisplayName = "Alex Wilber",
        }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        var user = (await created.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;

        var roles = await _superadministrator.GetFromJsonAsync<PagedResult<RoleDto>>("api/v1/roles", Signal9Json.Options, Ct);
        foreach (var (role, tenant) in assignments)
        {
            var assigned = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenant}/role-assignments",
                new CreateRoleAssignmentRequest { UserId = user.Id, RoleId = roles!.Items.Single(r => r.Name == role).Id },
                Signal9Json.Options, Ct);
            Assert.AreEqual(HttpStatusCode.Created, assigned.StatusCode, await assigned.Content.ReadAsStringAsync(Ct));
        }
        return await _factory.CreatePersonClientAsync(person);
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
