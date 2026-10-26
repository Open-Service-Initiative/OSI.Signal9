using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Users;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Managing users (#117, stories 15-16, 25): Technicians record end users, while users who hold role assignments can be
/// edited only by someone with authority over every one of those assignments.
/// </summary>
[TestClass]
public sealed class UserManagementTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public UserManagementTests(TestContext testContext)
    {
        _testContext = testContext;
        _superadministrator = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task A_technician_creates_and_edits_a_user_without_role_assignments_in_their_subtree()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));

        var created = await technician.PostAsJsonAsync("api/v1/users", NewUser(tree.Client, "Alex Wilber"), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        var user = (await created.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;

        var edited = await PatchAsync(technician, user.Id, """{"displayName":"Alex W. Wilber"}""");

        Assert.AreEqual(HttpStatusCode.OK, edited.StatusCode, await edited.Content.ReadAsStringAsync(Ct));
        Assert.IsNotNull(edited.Headers.ETag);
        var body = (await edited.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual("Alex W. Wilber", body.DisplayName);
        Assert.AreEqual(user with { DisplayName = "Alex W. Wilber" }, body);
        var read = await technician.GetFromJsonAsync<UserDto>($"api/v1/users/{user.Id}", Signal9Json.Options, Ct);
        Assert.AreEqual("Alex W. Wilber", read!.DisplayName);
    }

    [TestMethod]
    public async Task Users_outside_the_callers_subtree_read_as_missing()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));
        var outsider = await CreateUserAsync(tree.Sibling);

        var created = await technician.PostAsJsonAsync("api/v1/users", NewUser(tree.Sibling, "Outsider"), Signal9Json.Options, Ct);
        var edited = await PatchAsync(technician, outsider.Id, """{"displayName":"Renamed"}""");

        Assert.AreEqual(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, edited.StatusCode);
        Assert.AreEqual("Alex Wilber", (await ReadAsync(outsider.Id)).DisplayName);
    }

    [TestMethod]
    public async Task Editing_a_user_with_role_assignments_needs_role_assignment_authority_over_all_of_them()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var viewer = await CreateUserAsync(tree.Client);
        await AssignAsync(tree.Client, viewer.Id, "Viewer");

        var byTechnician = await PatchAsync(technician, viewer.Id, """{"displayName":"Tampered"}""");
        Assert.AreEqual(HttpStatusCode.Forbidden, byTechnician.StatusCode);
        Assert.AreEqual("application/problem+json", byTechnician.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual("Alex Wilber", (await ReadAsync(viewer.Id)).DisplayName);

        var byAdministrator = await PatchAsync(administrator, viewer.Id, """{"displayName":"Alex W. Wilber"}""");
        Assert.AreEqual(HttpStatusCode.OK, byAdministrator.StatusCode, await byAdministrator.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual("Alex W. Wilber", (await ReadAsync(viewer.Id)).DisplayName);
    }

    [TestMethod]
    public async Task A_role_assignment_beyond_the_callers_reach_protects_the_user()
    {
        var tree = await TreeAsync();
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        // Owned within the Administrator's subtree, but also assigned where they have no authority: beside it and above it.
        var beside = await CreateUserAsync(tree.Client);
        await AssignAsync(tree.Client, beside.Id, "Viewer");
        await AssignAsync(tree.Sibling, beside.Id, "Viewer");
        var above = await CreateUserAsync(tree.Client);
        await AssignAsync(tree.Root, above.Id, "Administrator");

        var editBeside = await PatchAsync(administrator, beside.Id, """{"displayName":"Tampered"}""");
        var editAbove = await PatchAsync(administrator, above.Id, """{"displayName":"Tampered"}""");
        var bySuperadministrator = await PatchAsync(_superadministrator, above.Id, """{"displayName":"Alex W. Wilber"}""");

        Assert.AreEqual(HttpStatusCode.Forbidden, editBeside.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, editAbove.StatusCode);
        Assert.AreEqual("Alex Wilber", (await ReadAsync(beside.Id)).DisplayName);
        Assert.AreEqual(HttpStatusCode.OK, bySuperadministrator.StatusCode, await bySuperadministrator.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Role_assignment_authority_counts_only_with_the_scope_in_the_token()
    {
        var tree = await TreeAsync();
        var person = EntraUser.WithMfa();
        var caller = await CreateUserAsync(tree.Msp, person);
        await AssignAsync(tree.Msp, caller.Id, "Administrator");
        // An Administrator whose token can write users but not role assignments.
        using var narrow = await _factory.CreatePersonClientAsync(person, Scopes.Users.Read, Scopes.Users.Write);
        var viewer = await CreateUserAsync(tree.Client);
        await AssignAsync(tree.Client, viewer.Id, "Viewer");
        var endUser = await CreateUserAsync(tree.Client);

        Assert.AreEqual(HttpStatusCode.Forbidden, (await PatchAsync(narrow, viewer.Id, """{"displayName":"Tampered"}""")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await PatchAsync(narrow, endUser.Id, """{"displayName":"Renamed"}""")).StatusCode);
    }

    [TestMethod]
    public async Task A_role_assignment_cant_be_granted_while_an_edit_is_being_checked()
    {
        var tree = await TreeAsync();
        var user = await CreateUserAsync(tree.Client);
        var race = new GrantDuringEdit(_factory.ConnectionString, user.Id, tree.Client, BuiltInRoles.Viewer.Id);
        using var api = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureDbContext<Signal9DbContext>(options => options.AddInterceptors(race))));
        using var technician = api.CreateClient();
        technician.DefaultRequestHeaders.Authorization = (await PersonAsync(tree.Msp, ("Technician", tree.Msp))).DefaultRequestHeaders.Authorization;

        var edited = await PatchAsync(technician, user.Id, """{"displayName":"Renamed"}""");

        Assert.AreEqual(HttpStatusCode.OK, edited.StatusCode, await edited.Content.ReadAsStringAsync(Ct));
        Assert.IsTrue(race.Blocked, "A concurrent grant waits for the edit, so the edit's check can't miss it.");
    }

    [TestMethod]
    public async Task A_viewer_lists_and_reads_users_in_their_subtree_but_cant_change_them()
    {
        var tree = await TreeAsync();
        using var viewer = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));
        var inside = await CreateUserAsync(tree.Client);
        var outside = await CreateUserAsync(tree.Sibling);

        var listed = await viewer.GetFromJsonAsync<PagedResult<UserDto>>("api/v1/users?pageSize=100", Signal9Json.Options, Ct);
        var read = await viewer.GetAsync($"api/v1/users/{inside.Id}", Ct);
        var hidden = await viewer.GetAsync($"api/v1/users/{outside.Id}", Ct);
        var created = await viewer.PostAsJsonAsync("api/v1/users", NewUser(tree.Client, "Someone"), Signal9Json.Options, Ct);
        var edited = await PatchAsync(viewer, inside.Id, """{"displayName":"Renamed"}""");

        var ids = listed!.Items.Select(u => u.Id).ToArray();
        CollectionAssert.Contains(ids, inside.Id);
        CollectionAssert.DoesNotContain(ids, outside.Id);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, hidden.StatusCode);
        // Without users.write at any tenant, every tenant and user reads as missing to a change.
        Assert.AreEqual(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, edited.StatusCode);
    }

    [TestMethod]
    public async Task Editing_needs_the_scope_and_a_merge_patch_of_editable_fields_and_honours_if_match()
    {
        var user = await CreateUserAsync((await _factory.CreateTenantAsync(_superadministrator, "Contoso")).Id);
        var url = $"api/v1/users/{user.Id}";
        using var anonymous = _factory.CreateAnonymousClient();
        using var readOnly = await _factory.CreateClientAsync(Scopes.Users.Read);

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await PatchAsync(anonymous, user.Id, """{"displayName":"X"}""")).StatusCode);
        var withoutScope = await PatchAsync(readOnly, user.Id, """{"displayName":"X"}""");
        Assert.AreEqual(HttpStatusCode.Forbidden, withoutScope.StatusCode);
        StringAssert.Contains(withoutScope.Headers.WwwAuthenticate.ToString(), "insufficient_scope");
        Assert.AreEqual(HttpStatusCode.NotFound, (await PatchAsync(_superadministrator, Guid.NewGuid(), """{"displayName":"X"}""")).StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await PatchAsync(_superadministrator, user.Id, """{"displayName":""}""")).StatusCode);
        // The owning tenant changes only by a user transfer (#93), and the Entra identity never does.
        foreach (var readOnlyField in new[] { "tenantId", "entraTenantId", "entraObjectId", "id" })
        {
            var refused = await PatchAsync(_superadministrator, user.Id, $$"""{"{{readOnlyField}}":"{{Guid.NewGuid()}}"}""");
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, readOnlyField);
        }
        using var plainJson = new StringContent("""{"displayName":"X"}""", Encoding.UTF8, "application/json");
        Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, (await _superadministrator.PatchAsync(url, plainJson, Ct)).StatusCode);

        var stale = await PatchAsync(_superadministrator, user.Id, """{"displayName":"X"}""", new EntityTagHeaderValue("\"stale\""));
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        var current = await _superadministrator.GetAsync(url, Ct);
        var matched = await PatchAsync(_superadministrator, user.Id, """{"displayName":"Alex W. Wilber"}""", current.Headers.ETag);
        Assert.AreEqual(HttpStatusCode.OK, matched.StatusCode, await matched.Content.ReadAsStringAsync(Ct));
        Assert.AreNotEqual(current.Headers.ETag, matched.Headers.ETag);
        Assert.AreEqual(user with { DisplayName = "Alex W. Wilber" }, await ReadAsync(user.Id));
    }

    [TestMethod]
    public async Task Creating_and_editing_users_is_audited_at_their_owning_tenant()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var created = await technician.PostAsJsonAsync("api/v1/users", NewUser(tree.Client, "Alex Wilber"), Signal9Json.Options, Ct);
        var user = (await created.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(HttpStatusCode.OK, (await PatchAsync(technician, user.Id, """{"displayName":"Alex W. Wilber"}""")).StatusCode);
        await AssignAsync(tree.Client, user.Id, "Viewer");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await PatchAsync(technician, user.Id, """{"displayName":"Tampered"}""")).StatusCode);

        var trail = (await administrator.GetFromJsonAsync<PagedResult<AuditEntryDto>>("api/v1/audit-entries?pageSize=100",
            Signal9Json.Options, Ct))!.Items;

        var creation = trail.Single(e => e.Action == AuditActions.UserCreated && e.TargetId == user.Id);
        var edit = trail.Single(e => e.Action == AuditActions.UserUpdated && e.TargetId == user.Id);
        foreach (var entry in new[] { creation, edit })
        {
            Assert.AreEqual(tree.Client, entry.TenantId);
            Assert.AreEqual("user", entry.TargetType);
            Assert.AreEqual(user.Id, entry.UserId);
            Assert.AreEqual("Alex W. Wilber", entry.UserDisplayName);
            Assert.AreEqual(AuditOutcome.Succeeded, entry.Outcome);
            Assert.AreEqual(AuditActorKind.Person, entry.Actor.Kind);
            Assert.IsFalse(entry.Actor.Superadministrator);
            // Names are resolved when read; the stored detail never copies them.
            Assert.DoesNotContain("Alex", entry.Detail ?? string.Empty);
        }
        Assert.AreEqual(1, trail.Count(e => e.Action == AuditActions.UserUpdated && e.TargetId == user.Id),
            "The refused edit changes nothing, so only the authorization denial records it.");
        Assert.IsTrue(trail.Any(e => e.Action == AuditActions.AuthorizationDenied && e.TenantId == tree.Client));
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

    private static CreateUserRequest NewUser(Guid tenantId, string displayName, EntraUser? person = null)
    {
        person ??= EntraUser.WithMfa();
        return new CreateUserRequest
        {
            TenantId = tenantId,
            EntraTenantId = person.TenantId,
            EntraObjectId = person.ObjectId,
            DisplayName = displayName,
        };
    }

    private async Task<UserDto> CreateUserAsync(Guid tenantId, EntraUser? person = null)
    {
        var response = await _superadministrator.PostAsJsonAsync("api/v1/users", NewUser(tenantId, "Alex Wilber", person),
            Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<UserDto> ReadAsync(Guid userId) =>
        (await _superadministrator.GetFromJsonAsync<UserDto>($"api/v1/users/{userId}", Signal9Json.Options, Ct))!;

    private async Task AssignAsync(Guid tenantId, Guid userId, string role)
    {
        var response = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenantId}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = userId, RoleId = BuiltInRoles.All.Single(r => r.Name == role).Id },
            Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<HttpResponseMessage> PatchAsync(HttpClient caller, Guid userId, string patch, EntityTagHeaderValue? ifMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"api/v1/users/{userId}")
        {
            Content = new StringContent(patch, Encoding.UTF8, "application/merge-patch+json"),
        };
        if (ifMatch is not null)
            request.Headers.IfMatch.Add(ifMatch);
        return await caller.SendAsync(request, Ct);
    }

    /// <summary>
    /// Just before the edit's UPDATE, tries to grant the user a role on another connection, giving up after a moment if
    /// the edit holds the user locked.
    /// </summary>
    private sealed class GrantDuringEdit(string connectionString, Guid userId, Guid tenantId, Guid roleId) : DbCommandInterceptor
    {
        private int _raced;

        public bool Blocked { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE \"Users\"", StringComparison.Ordinal) && Interlocked.Exchange(ref _raced, 1) == 0)
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var grant = new NpgsqlCommand($"""
                    SET lock_timeout = '500ms';
                    INSERT INTO "RoleAssignments" ("Id", "TenantId", "UserId", "RoleId", "CreatedAt")
                    VALUES ('{Guid.NewGuid()}', '{tenantId}', '{userId}', '{roleId}', now());
                    """, connection);
                try
                {
                    await grant.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
                {
                    Blocked = true;
                }
            }
            return result;
        }
    }

    public void Dispose()
    {
        _superadministrator.Dispose();
        _factory.Dispose();
    }
}
