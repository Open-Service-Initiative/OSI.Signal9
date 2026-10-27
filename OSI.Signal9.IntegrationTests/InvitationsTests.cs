using System.Net;
using System.Net.Http.Json;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Invitations;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Users;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Invitations (#121, stories 28-37): an Administrator offers one role at one tenant through the API, and the person
/// redeems the link at auth after signing in with Entra. Every test drives the public API and auth's pages, as an
/// Administrator, an invited person's browser or an auditor would.
/// </summary>
[TestClass]
public sealed class InvitationsTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public InvitationsTests(TestContext testContext)
    {
        _testContext = testContext;
        // The fixture's own client stands in for a Superadministrator.
        _superadministrator = _factory.CreateSuperadministratorClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task An_administrator_invites_with_one_role_at_one_tenant_and_sees_the_link_once()
    {
        var tree = await TreeAsync();
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var technician = await RoleIdAsync("Technician");

        var response = await administrator.PostAsJsonAsync($"api/v1/tenants/{tree.Client}/invitations",
            new CreateInvitationRequest { RoleId = technician }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.IsTrue(response.Headers.CacheControl?.NoStore, "A response carrying the link is never cached.");
        var invitation = (await response.Content.ReadFromJsonAsync<InvitationDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual($"/api/v1/tenants/{tree.Client}/invitations/{invitation.Id}", response.Headers.Location?.AbsolutePath);
        Assert.AreEqual(response.Headers.Location?.AbsolutePath, invitation.Url);
        Assert.AreEqual(tree.Client, invitation.TenantId);
        Assert.AreEqual(technician, invitation.RoleId);
        Assert.IsNull(invitation.UserId);
        Assert.IsNull(invitation.EntraTenantId);
        Assert.AreEqual(InvitationStatus.Pending, invitation.Status);
        Assert.AreEqual(_factory.Time.GetUtcNow().AddHours(72), invitation.ExpiresAt);

        // The link is on auth and carries only the opaque reference: nothing about the tenant, role or invitation.
        var link = new Uri(invitation.Link!);
        Assert.AreEqual(new Uri(Signal9AuthFactory.Issuer).Host, link.Host);
        Assert.AreEqual("https", link.Scheme);
        Assert.IsEmpty(link.Query);
        var reference = link.AbsolutePath.Split('/').Last();
        Assert.IsGreaterThanOrEqualTo(43, reference.Length, "At least 256 bits, base64url-encoded.");
        foreach (var detail in new[] { tree.Client, technician, invitation.Id })
            Assert.DoesNotContain(detail.ToString(), invitation.Link!);

        var fetched = await administrator.GetFromJsonAsync<InvitationDto>(invitation.Url, Signal9Json.Options, Ct);
        var listed = await administrator.GetFromJsonAsync<PagedResult<InvitationDto>>(
            $"api/v1/tenants/{tree.Msp}/invitations", Signal9Json.Options, Ct);
        Assert.IsNull(fetched!.Link, "The link is shown only when the invitation is created.");
        Assert.AreEqual(invitation.Id, listed!.Items.Single().Id, "The tenant view includes descendants.");
        Assert.IsNull(listed.Items.Single().Link);
        Assert.AreEqual(invitation.Url, listed.Items.Single().Url);
        var own = await administrator.GetFromJsonAsync<PagedResult<InvitationDto>>(
            $"api/v1/tenants/{tree.Msp}/invitations?includeDescendants=false", Signal9Json.Options, Ct);
        Assert.IsEmpty(own!.Items);
    }

    [TestMethod]
    public async Task An_invitation_can_be_pinned_to_a_directory_or_offered_to_an_existing_user()
    {
        var tree = await TreeAsync();
        var directory = Guid.NewGuid();
        var existing = await UserAsync(EntraUser.WithMfa(), tree.Client);
        var viewer = await RoleIdAsync("Viewer");

        var pinned = await InviteAsync(_superadministrator, tree.Client, new CreateInvitationRequest { RoleId = viewer, EntraTenantId = directory });
        var forUser = await InviteAsync(_superadministrator, tree.Msp, new CreateInvitationRequest { RoleId = viewer, UserId = existing.Id });

        Assert.AreEqual(directory, pinned.EntraTenantId);
        Assert.IsNull(pinned.UserId);
        Assert.AreEqual(existing.Id, forUser.UserId);
        Assert.AreEqual(tree.Msp, forUser.TenantId);
    }

    [TestMethod]
    [DataRow("""{}""", "RoleId")]
    [DataRow("""{"roleId":"00000000-0000-0000-0000-000000000001"}""", "RoleId")]
    [DataRow("""{"roleId":"{viewer}","userId":"00000000-0000-0000-0000-000000000001"}""", "UserId")]
    [DataRow("""{"roleId":"{viewer}","userId":"{user}","entraTenantId":"00000000-0000-0000-0000-000000000001"}""", "EntraTenantId")]
    public async Task An_invalid_invitation_is_refused_with_400(string body, string field)
    {
        var tree = await TreeAsync();
        var user = await UserAsync(EntraUser.WithMfa(), tree.Client);
        body = body.Replace("{viewer}", (await RoleIdAsync("Viewer")).ToString()).Replace("{user}", user.Id.ToString());

        var response = await _superadministrator.PostAsync($"api/v1/tenants/{tree.Client}/invitations",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Inviting_needs_role_assignment_access_within_your_own_role_and_subtree()
    {
        var tree = await TreeAsync();
        using var technician = await PersonAsync(tree.Msp, ("Technician", tree.Msp));
        using var administrator = await PersonAsync(tree.Client, ("Administrator", tree.Client));
        var outsider = await UserAsync(EntraUser.WithMfa(), tree.Sibling);
        var viewer = new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer") };

        var byTechnician = await technician.PostAsJsonAsync($"api/v1/tenants/{tree.Client}/invitations", viewer, Signal9Json.Options, Ct);
        var outsideSubtree = await administrator.PostAsJsonAsync($"api/v1/tenants/{tree.Msp}/invitations", viewer, Signal9Json.Options, Ct);
        var userOutside = await administrator.PostAsJsonAsync($"api/v1/tenants/{tree.Client}/invitations",
            viewer with { UserId = outsider.Id }, Signal9Json.Options, Ct);
        var listedOutside = await administrator.GetAsync($"api/v1/tenants/{tree.Msp}/invitations", Ct);

        Assert.AreEqual(HttpStatusCode.NotFound, byTechnician.StatusCode, "Technicians can't assign roles, so the tenant is out of their reach for it.");
        Assert.AreEqual(HttpStatusCode.NotFound, outsideSubtree.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, userOutside.StatusCode, "A user outside the caller's reach reads as missing.");
        Assert.AreEqual(HttpStatusCode.NotFound, listedOutside.StatusCode);
    }

    [TestMethod]
    public async Task An_invitation_cannot_offer_a_role_beyond_the_inviters_own_access()
    {
        var tree = await TreeAsync();
        var person = EntraUser.WithMfa();
        var user = await UserAsync(person, tree.Msp);
        await GrantAsync(user.Id, "Administrator", tree.Msp);
        // A client the Administrator delegated only role assignment scopes: it can't offer a role granting more.
        using var narrow = await _factory.CreatePersonClientAsync(person, "roleassignments.read", "roleassignments.write");

        var response = await narrow.PostAsJsonAsync($"api/v1/tenants/{tree.Client}/invitations",
            new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer") }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Role higher than your own", await response.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Redeeming_signs_a_new_person_in_with_Entra_and_grants_the_role_creating_their_user()
    {
        var tree = await TreeAsync();
        var invitation = await InviteAsync(_superadministrator, tree.Client, new CreateInvitationRequest { RoleId = await RoleIdAsync("Technician") });
        var person = EntraUser.WithMfa();
        using var browser = new Browser(_factory.Auth.Server);

        var page = await browser.SendAuthorizeAsync(LinkPath(invitation), _factory.Entra, person);
        Assert.HasCount(1, _factory.Entra.SignInsOf(person), "Redeeming starts with signing in at Entra.");
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync(Ct));
        var offer = await page.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Technician", offer);
        Assert.Contains("Client", offer);
        Assert.AreEqual("no-referrer", page.Headers.GetValues("Referrer-Policy").Single(), "The link mustn't leak onwards.");
        Assert.IsTrue(page.Headers.CacheControl?.NoStore);
        var redeemed = await AcceptAsync(browser, page);

        Assert.AreEqual(HttpStatusCode.OK, redeemed.StatusCode, await redeemed.Content.ReadAsStringAsync(Ct));
        var fetched = (await _superadministrator.GetFromJsonAsync<InvitationDto>(invitation.Url, Signal9Json.Options, Ct))!;
        Assert.AreEqual(InvitationStatus.Redeemed, fetched.Status);
        Assert.AreEqual(_factory.Time.GetUtcNow(), fetched.RedeemedAt);
        var user = (await _superadministrator.GetFromJsonAsync<UserDto>($"api/v1/users/{fetched.RedeemedByUserId}", Signal9Json.Options, Ct))!;
        Assert.AreEqual(tree.Client, user.TenantId, "A new user belongs to the invitation's tenant.");
        Assert.AreEqual(person.TenantId, user.EntraTenantId);
        Assert.AreEqual(person.ObjectId, user.EntraObjectId);
        var assignments = await _superadministrator.GetFromJsonAsync<PagedResult<RoleAssignmentDto>>(
            $"api/v1/tenants/{tree.Client}/role-assignments", Signal9Json.Options, Ct);
        Assert.AreEqual(await RoleIdAsync("Technician"), assignments!.Items.Single(a => a.UserId == user.Id).RoleId);

        // The person now has the access the invitation offered.
        using var invited = await _factory.CreatePersonClientAsync(person);
        Assert.AreEqual(HttpStatusCode.OK, (await invited.GetAsync($"api/v1/tenants/{tree.Client}", Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await invited.GetAsync($"api/v1/tenants/{tree.Msp}", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task An_existing_user_redeems_an_invitation_to_an_additional_role_assignment()
    {
        var tree = await TreeAsync();
        var person = EntraUser.WithMfa();
        var user = await UserAsync(person, tree.Msp);
        await GrantAsync(user.Id, "Viewer", tree.Msp);
        var invitation = await InviteAsync(_superadministrator, tree.Client,
            new CreateInvitationRequest { RoleId = await RoleIdAsync("Technician"), UserId = user.Id });

        var redeemed = await RedeemAsync(invitation, person);

        Assert.AreEqual(HttpStatusCode.OK, redeemed.StatusCode, await redeemed.Content.ReadAsStringAsync(Ct));
        var fetched = (await _superadministrator.GetFromJsonAsync<InvitationDto>(invitation.Url, Signal9Json.Options, Ct))!;
        Assert.AreEqual(user.Id, fetched.RedeemedByUserId);
        var owner = (await _superadministrator.GetFromJsonAsync<UserDto>($"api/v1/users/{user.Id}", Signal9Json.Options, Ct))!;
        Assert.AreEqual(tree.Msp, owner.TenantId, "An existing user keeps their owning tenant.");
        var assignments = await _superadministrator.GetFromJsonAsync<PagedResult<RoleAssignmentDto>>(
            $"api/v1/tenants/{tree.Client}/role-assignments", Signal9Json.Options, Ct);
        Assert.AreEqual(user.Id, assignments!.Items.Single().UserId);
    }

    [TestMethod]
    public async Task An_invitation_works_once()
    {
        var tree = await TreeAsync();
        var invitation = await InviteAsync(_superadministrator, tree.Client, new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer") });
        var first = EntraUser.WithMfa();
        var forwardedTo = EntraUser.WithMfa();

        Assert.AreEqual(HttpStatusCode.OK, (await RedeemAsync(invitation, first)).StatusCode);
        var again = await RedeemAsync(invitation, forwardedTo);

        Assert.AreEqual(HttpStatusCode.Gone, again.StatusCode);
        Assert.Contains("already been used", await again.Content.ReadAsStringAsync(Ct));
        var assignments = await _superadministrator.GetFromJsonAsync<PagedResult<RoleAssignmentDto>>(
            $"api/v1/tenants/{tree.Client}/role-assignments", Signal9Json.Options, Ct);
        Assert.HasCount(1, assignments!.Items);
    }

    [TestMethod]
    public async Task Concurrent_redemptions_of_one_invitation_grant_only_one_role_assignment()
    {
        var tree = await TreeAsync();
        var invitation = await InviteAsync(_superadministrator, tree.Client, new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer") });
        // Several people reach the offer page with the same link, then all accept at once.
        var browsers = Enumerable.Range(0, 5).Select(i => new Browser(_factory.Auth.Server, $"203.0.113.{i + 10}")).ToList();
        var pages = new List<HttpResponseMessage>();
        foreach (var browser in browsers)
            pages.Add(await browser.SendAuthorizeAsync(LinkPath(invitation), _factory.Entra, EntraUser.WithMfa()));
        Assert.IsTrue(pages.All(p => p.StatusCode == HttpStatusCode.OK));

        var results = await Task.WhenAll(browsers.Select((browser, i) => AcceptAsync(browser, pages[i])));
        browsers.ForEach(b => b.Dispose());

        Assert.AreEqual(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.IsTrue(results.Where(r => r.StatusCode != HttpStatusCode.OK).All(r => r.StatusCode == HttpStatusCode.Gone));
        var assignments = await _superadministrator.GetFromJsonAsync<PagedResult<RoleAssignmentDto>>(
            $"api/v1/tenants/{tree.Client}/role-assignments", Signal9Json.Options, Ct);
        Assert.HasCount(1, assignments!.Items);
        var users = await _superadministrator.GetFromJsonAsync<PagedResult<UserDto>>(
            $"api/v1/users?tenantId={tree.Client}", Signal9Json.Options, Ct);
        Assert.HasCount(1, users!.Items, "Only the redemption that won created a user.");
    }

    [TestMethod]
    public async Task An_invitation_expires_after_72_hours()
    {
        var tree = await TreeAsync();
        var invitation = await InviteAsync(_superadministrator, tree.Client, new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer") });
        using var browser = new Browser(_factory.Auth.Server);
        _factory.Time.Advance(TimeSpan.FromHours(72) - TimeSpan.FromSeconds(1));
        var page = await browser.SendAuthorizeAsync(LinkPath(invitation), _factory.Entra, EntraUser.WithMfa());
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, "Still redeemable just before 72 hours.");

        _factory.Time.Advance(TimeSpan.FromSeconds(1));
        var accepted = await AcceptAsync(browser, page);

        Assert.AreEqual(HttpStatusCode.Gone, accepted.StatusCode);
        Assert.Contains("expired", await accepted.Content.ReadAsStringAsync(Ct));
        var fetched = await _superadministrator.GetFromJsonAsync<InvitationDto>(invitation.Url, Signal9Json.Options, Ct);
        Assert.AreEqual(InvitationStatus.Expired, fetched!.Status);
    }

    [TestMethod]
    public async Task A_directory_pinned_invitation_refuses_people_from_other_directories()
    {
        var tree = await TreeAsync();
        var contoso = Guid.NewGuid();
        var invitation = await InviteAsync(_superadministrator, tree.Client,
            new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer"), EntraTenantId = contoso });
        var outsider = EntraUser.WithMfa();
        var insider = EntraUser.WithMfa() with { TenantId = contoso };

        var refused = await RedeemAsync(invitation, outsider);
        var accepted = await RedeemAsync(invitation, insider);

        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode, "A refusal doesn't use the invitation up.");
        var fetched = await _superadministrator.GetFromJsonAsync<InvitationDto>(invitation.Url, Signal9Json.Options, Ct);
        var redeemer = await _superadministrator.GetFromJsonAsync<UserDto>($"api/v1/users/{fetched!.RedeemedByUserId}", Signal9Json.Options, Ct);
        Assert.AreEqual(insider.ObjectId, redeemer!.EntraObjectId);
    }

    [TestMethod]
    public async Task An_invitation_is_never_matched_by_email()
    {
        var tree = await TreeAsync();
        var invited = EntraUser.WithMfa() with { Email = "alice@contoso.example" };
        var user = await UserAsync(invited, tree.Client);
        var invitation = await InviteAsync(_superadministrator, tree.Client,
            new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer"), UserId = user.Id });
        // Someone else in the same directory whose email, UPN and preferred username claim to be Alice's.
        var impostor = EntraUser.WithMfa() with { TenantId = invited.TenantId, Email = invited.Email };

        var refused = await RedeemAsync(invitation, impostor);

        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        var fetched = await _superadministrator.GetFromJsonAsync<InvitationDto>(invitation.Url, Signal9Json.Options, Ct);
        Assert.AreEqual(InvitationStatus.Pending, fetched!.Status);
        var users = await _superadministrator.GetFromJsonAsync<PagedResult<UserDto>>(
            $"api/v1/users?tenantId={tree.Client}", Signal9Json.Options, Ct);
        Assert.AreEqual(user.Id, users!.Items.Single().Id, "No user was created or linked for the impostor.");
        Assert.AreEqual(HttpStatusCode.OK, (await RedeemAsync(invitation, invited)).StatusCode);
    }

    [TestMethod]
    public async Task A_sign_in_Entra_refuses_redeems_nothing()
    {
        var tree = await TreeAsync();
        var invitation = await InviteAsync(_superadministrator, tree.Client, new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer") });

        var refused = await RedeemAsync(invitation, EntraUser.WithMfa() with { Amr = ["pwd"] });

        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("multi-factor", await refused.Content.ReadAsStringAsync(Ct));
        var fetched = await _superadministrator.GetFromJsonAsync<InvitationDto>(invitation.Url, Signal9Json.Options, Ct);
        Assert.AreEqual(InvitationStatus.Pending, fetched!.Status);
    }

    [TestMethod]
    public async Task Someone_who_already_has_the_role_leaves_the_invitation_unused()
    {
        var tree = await TreeAsync();
        var person = EntraUser.WithMfa();
        var user = await UserAsync(person, tree.Client);
        await GrantAsync(user.Id, "Viewer", tree.Client);
        var invitation = await InviteAsync(_superadministrator, tree.Client, new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer") });

        var response = await RedeemAsync(invitation, person);

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        var fetched = await _superadministrator.GetFromJsonAsync<InvitationDto>(invitation.Url, Signal9Json.Options, Ct);
        Assert.AreEqual(InvitationStatus.Pending, fetched!.Status);
    }

    [TestMethod]
    public async Task An_unknown_link_is_not_found_and_accepting_needs_the_page_auth_showed()
    {
        var tree = await TreeAsync();
        var invitation = await InviteAsync(_superadministrator, tree.Client, new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer") });
        using var browser = new Browser(_factory.Auth.Server);
        await browser.SendAuthorizeAsync(LinkPath(invitation), _factory.Entra, EntraUser.WithMfa());

        var unknown = await browser.GetAsync("invitations/s9i_" + new string('A', 43));
        var forged = await browser.PostAsync(new Uri(LinkPath(invitation), UriKind.Relative),
            new FormUrlEncodedContent(new Dictionary<string, string>()));

        Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode, "Without the page's antiforgery token (CSRF).");
        var fetched = await _superadministrator.GetFromJsonAsync<InvitationDto>(invitation.Url, Signal9Json.Options, Ct);
        Assert.AreEqual(InvitationStatus.Pending, fetched!.Status);
    }

    [TestMethod]
    public async Task Revoking_withdraws_an_invitation_for_good()
    {
        var tree = await TreeAsync();
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var invitation = await InviteAsync(administrator, tree.Client, new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer") });
        var redeemed = await InviteAsync(administrator, tree.Client, new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer") });
        Assert.AreEqual(HttpStatusCode.OK, (await RedeemAsync(redeemed, EntraUser.WithMfa())).StatusCode);

        var revoke = await administrator.PostAsync($"{invitation.Url}:revoke", null, Ct);
        var again = await administrator.PostAsync($"{invitation.Url}:revoke", null, Ct);
        var revokeRedeemed = await administrator.PostAsync($"{redeemed.Url}:revoke", null, Ct);
        var link = await RedeemAsync(invitation, EntraUser.WithMfa());

        Assert.AreEqual(HttpStatusCode.OK, revoke.StatusCode, await revoke.Content.ReadAsStringAsync(Ct));
        var revoked = (await revoke.Content.ReadFromJsonAsync<InvitationDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(InvitationStatus.Revoked, revoked.Status);
        Assert.AreEqual(_factory.Time.GetUtcNow(), revoked.RevokedAt);
        Assert.AreEqual(HttpStatusCode.Conflict, again.StatusCode);
        Assert.AreEqual("application/problem+json", again.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Conflict, revokeRedeemed.StatusCode);
        Assert.AreEqual(HttpStatusCode.Gone, link.StatusCode);
        Assert.Contains("withdrawn", await link.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.NotFound,
            (await administrator.PostAsync($"api/v1/tenants/{tree.Client}/invitations/{Guid.NewGuid()}:revoke", null, Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Creating_redeeming_refusing_and_revoking_are_audited_at_the_invitations_tenant()
    {
        var tree = await TreeAsync();
        using var mspAdministrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        using var siblingAdministrator = await PersonAsync(tree.Sibling, ("Administrator", tree.Sibling));
        var viewer = await RoleIdAsync("Viewer");
        var redeemed = await InviteAsync(mspAdministrator, tree.Client, new CreateInvitationRequest { RoleId = viewer, EntraTenantId = Guid.NewGuid() });
        var revoked = await InviteAsync(mspAdministrator, tree.Client, new CreateInvitationRequest { RoleId = viewer });
        var outsider = EntraUser.WithMfa();
        var person = EntraUser.WithMfa() with { TenantId = redeemed.EntraTenantId!.Value };
        Assert.AreEqual(HttpStatusCode.Forbidden, (await RedeemAsync(redeemed, outsider, clientIp: "198.51.100.20")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await RedeemAsync(redeemed, person, clientIp: "198.51.100.21")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await mspAdministrator.PostAsync($"{revoked.Url}:revoke", null, Ct)).StatusCode);

        var trail = await TrailAsync(mspAdministrator);
        var beside = await TrailAsync(siblingAdministrator);

        var redemption = trail.Where(e => e.TargetId == redeemed.Id).ToList();
        CollectionAssert.AreEqual(
            new[] { AuditActions.InvitationRedeemed, AuditActions.InvitationRedeemed, AuditActions.InvitationCreated },
            redemption.Select(e => e.Action).ToArray(), "Newest first.");
        var (succeeded, failed) = (redemption[0], redemption[1]);
        Assert.AreEqual(AuditOutcome.Succeeded, succeeded.Outcome);
        Assert.AreEqual(AuditActorKind.Person, succeeded.Actor.Kind);
        Assert.AreEqual($"{person.TenantId}:{person.ObjectId}", succeeded.Actor.Subject);
        Assert.AreEqual("198.51.100.21", succeeded.ClientIp);
        Assert.AreEqual(viewer, succeeded.RoleId);
        Assert.IsNotNull(succeeded.UserId);
        Assert.AreEqual(AuditOutcome.Failed, failed.Outcome);
        Assert.AreEqual($"{outsider.TenantId}:{outsider.ObjectId}", failed.Actor.Subject);
        Assert.AreEqual("OtherDirectory", failed.Detail);
        Assert.IsTrue(trail.Any(e => e.Action == AuditActions.RoleAssignmentCreated && e.UserId == succeeded.UserId
            && e.TenantId == tree.Client && e.Actor.Subject == succeeded.Actor.Subject), "The grant is audited as a role assignment too.");
        Assert.IsTrue(trail.Any(e => e.Action == AuditActions.UserCreated && e.UserId == succeeded.UserId && e.TargetId == succeeded.UserId
            && e.TenantId == tree.Client && e.Actor.Subject == succeeded.Actor.Subject), "So is the user it created (#117).");
        CollectionAssert.AreEqual(new[] { AuditActions.InvitationRevoked, AuditActions.InvitationCreated },
            trail.Where(e => e.TargetId == revoked.Id).Select(e => e.Action).ToArray());
        Assert.IsTrue(trail.Where(e => e.TargetType == "invitation").All(e => e.TenantId == tree.Client));
        Assert.IsFalse(beside.Any(e => e.TargetType == "invitation"));
    }

    [TestMethod]
    public async Task Deleting_a_tenant_revokes_its_invitations()
    {
        var tree = await TreeAsync();
        var site = await _factory.CreateTenantAsync(_superadministrator, "Site", parentTenantId: tree.Client);
        var invitation = await InviteAsync(_superadministrator, site.Id, new CreateInvitationRequest { RoleId = await RoleIdAsync("Viewer") });
        using var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));

        Assert.AreEqual(HttpStatusCode.NoContent, (await administrator.DeleteAsync($"api/v1/tenants/{site.Id}", Ct)).StatusCode);

        Assert.AreEqual(HttpStatusCode.NotFound, (await _superadministrator.GetAsync(invitation.Url, Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await RedeemAsync(invitation, EntraUser.WithMfa())).StatusCode);
    }

    // Helpers

    /// <summary>The link's path at auth: tests' browsers reach auth in memory, whatever its host name.</summary>
    private static string LinkPath(InvitationDto invitation) => new Uri(invitation.Link!).AbsolutePath.TrimStart('/');

    /// <summary>Opens the invitation's link as <paramref name="person"/>, signing in at Entra, and accepts the offer.</summary>
    private async Task<HttpResponseMessage> RedeemAsync(InvitationDto invitation, EntraUser person, string clientIp = "203.0.113.1")
    {
        using var browser = new Browser(_factory.Auth.Server, clientIp);
        var page = await browser.SendAuthorizeAsync(LinkPath(invitation), _factory.Entra, person);
        return page.StatusCode == HttpStatusCode.OK ? await AcceptAsync(browser, page) : page;
    }

    /// <summary>Submits auth's invitation page as the person would.</summary>
    private async Task<HttpResponseMessage> AcceptAsync(Browser browser, HttpResponseMessage page)
    {
        var html = await page.Content.ReadAsStringAsync(Ct);
        var form = System.Text.RegularExpressions.Regex.Match(html, "<form[^>]*action=\"([^\"]*)\"");
        Assert.IsTrue(form.Success, html);
        var fields = System.Text.RegularExpressions.Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\"")
            .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)));
        return await browser.PostAsync(new Uri(WebUtility.HtmlDecode(form.Groups[1].Value), UriKind.Relative), new FormUrlEncodedContent(fields));
    }

    private async Task<InvitationDto> InviteAsync(HttpClient caller, Guid tenantId, CreateInvitationRequest request)
    {
        var response = await caller.PostAsJsonAsync($"api/v1/tenants/{tenantId}/invitations", request, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<InvitationDto>(Signal9Json.Options, Ct))!;
    }

    private async Task<Guid> RoleIdAsync(string name)
    {
        var roles = await _superadministrator.GetFromJsonAsync<PagedResult<RoleDto>>("api/v1/roles", Signal9Json.Options, Ct);
        return roles!.Items.Single(r => r.Name == name).Id;
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

    /// <summary>A user for <paramref name="person"/>, owned by <paramref name="owner"/>.</summary>
    private async Task<UserDto> UserAsync(EntraUser person, Guid owner)
    {
        var created = await _superadministrator.PostAsJsonAsync("api/v1/users", new CreateUserRequest
        {
            TenantId = owner,
            EntraTenantId = person.TenantId,
            EntraObjectId = person.ObjectId,
            DisplayName = "Alex Wilber",
        }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        return (await created.Content.ReadFromJsonAsync<UserDto>(Signal9Json.Options, Ct))!;
    }

    private async Task GrantAsync(Guid userId, string role, Guid tenant)
    {
        var assigned = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenant}/role-assignments",
            new CreateRoleAssignmentRequest { UserId = userId, RoleId = await RoleIdAsync(role) }, Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, assigned.StatusCode, await assigned.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>A newly signed-in person, owned by <paramref name="owner"/>, holding these role assignments.</summary>
    private async Task<HttpClient> PersonAsync(Guid owner, params (string Role, Guid Tenant)[] assignments)
    {
        var person = EntraUser.WithMfa();
        var user = await UserAsync(person, owner);
        foreach (var (role, tenant) in assignments)
            await GrantAsync(user.Id, role, tenant);
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
