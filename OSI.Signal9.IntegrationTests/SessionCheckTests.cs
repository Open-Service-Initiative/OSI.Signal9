using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Auth;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;
using OSI.Signal9.Web.Bff;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Entra stays the authority over people, and access ends when it should (#119, stories 27, 51-53, 59): every refresh
/// re-checks the person's session, Entra is asked about them hourly, signing out revokes every token, and Entra's
/// front-channel logout ends the session by its <c>sid</c>.
/// </summary>
[TestClass]
public sealed class SessionCheckTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public SessionCheckTests(TestContext testContext)
    {
        _testContext = testContext;
        _superadministrator = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    private static EntraUser WithSession() => EntraUser.WithMfa() with { SessionId = $"sid-{Guid.NewGuid():N}" };

    private async Task<DashboardBrowser> SignedInAsync(EntraUser user)
    {
        var browser = new DashboardBrowser(_factory);
        var landing = await browser.SignInAsync(user);
        Assert.AreEqual(HttpStatusCode.OK, landing.StatusCode, await landing.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("api/v1/tenants")).StatusCode);
        return browser;
    }

    private async Task<(HttpStatusCode Status, string Error)> RedeemAsWebAsync(string refreshToken)
    {
        var certificate = _factory.Web.Services.GetRequiredService<WebClientCertificate>().Current;
        using var api = _factory.CreateForwardedCertificateClient(certificate);
        using var response = await api.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = WebClientRegistration.ClientId,
        }), Ct);
        var body = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(await response.Content.ReadAsStringAsync(Ct))!;
        return (response.StatusCode, body.TryGetValue("error", out var error) ? error.GetString()! : "(none)");
    }

    /// <summary>Waits for something a background job does, such as the hourly Entra check.</summary>
    private async Task EventuallyAsync(Func<Task<bool>> condition, string what)
    {
        for (var waited = 0; waited < 100; waited++)
        {
            if (await condition())
                return;
            await Task.Delay(100, Ct);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary>Lets an hour pass, less the check's own interval, and waits for auth to ask Entra about the person.</summary>
    private async Task EntraCheckAsync(int refreshesBefore)
    {
        _factory.Time.Advance(TimeSpan.FromMinutes(56));
        await EventuallyAsync(() => Task.FromResult(_factory.Entra.Refreshes > refreshesBefore), "the hourly Entra check");
    }

    /// <summary>
    /// Waits for the Entra check that claimed the person's sessions to finish with them after Entra couldn't answer: it
    /// hands them back as due, for its next run. Only that says the run is over. Asking Entra can take more than one
    /// call: the outbound resilience handler retries network errors and 5xx after a backoff on the fixture's clock, so
    /// fixture time passes a second at a time meanwhile, and Microsoft.Identity.Web asks again after
    /// <c>invalid_client</c> with its credential reloaded.
    /// </summary>
    private async Task HandedBackAsync(EntraUser user)
    {
        var subject = PersonSubject.Format(user.TenantId, user.ObjectId);
        for (var waited = 0; waited < 100; waited++)
        {
            await using (var scope = _factory.Services.CreateAsyncScope())
            {
                var due = _factory.Time.GetUtcNow().UtcDateTime - (EntraSessionCheck.Every - EntraSessionCheck.Interval);
                if (await scope.ServiceProvider.GetRequiredService<Signal9DbContext>().SignInSessions
                        .AnyAsync(s => s.Subject == subject && s.EntraCheckedAt <= due, Ct))
                    return;
            }
            _factory.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(100, Ct);
        }
        Assert.Fail("Timed out waiting for the Entra check to hand the session back.");
    }

    /// <summary>The audit trail, read through the public API by a client seeing every tenant, with a token from now.</summary>
    private async Task<List<AuditEntryDto>> AuditAsync(string action)
    {
        using var auditor = await _factory.CreateClientAsync(OSI.Signal9.Server.Identity.Scopes.Audit.Read);
        return [.. (await auditor.GetFromJsonAsync<PagedResult<AuditEntryDto>>("api/v1/audit-entries?pageSize=100",
            Signal9Json.Options, Ct))!.Items.Where(e => e.Action == action)];
    }

    // Every refresh re-checks the session (story 27).

    [TestMethod]
    public async Task A_refresh_needs_the_person_to_still_hold_a_role_assignment()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var withRole = await SignedInAsync(assigned.User);
        var withRoleToken = _factory.Web.IssuedRefreshTokens.Last();
        using var withoutRole = new DashboardBrowser(_factory);
        await withoutRole.SignInAsync(WithSession());
        var withoutRoleToken = _factory.Web.IssuedRefreshTokens.Last();

        var (kept, keptError) = await RedeemAsWebAsync(withRoleToken);
        var (refused, error) = await RedeemAsWebAsync(withoutRoleToken);

        Assert.AreEqual(HttpStatusCode.OK, kept, keptError);
        Assert.AreEqual(HttpStatusCode.BadRequest, refused);
        Assert.AreEqual("invalid_grant", error);
    }

    [TestMethod]
    public async Task Removing_a_users_last_role_assignment_ends_their_session_on_the_next_request()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);
        var refreshToken = _factory.Web.IssuedRefreshTokens.Last();

        var revoked = await _superadministrator.DeleteAsync(assigned.Assignment, Ct);
        var next = await browser.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.NoContent, revoked.StatusCode, await revoked.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.Unauthorized, next.StatusCode);
        Assert.AreEqual("invalid_grant", (await RedeemAsWebAsync(refreshToken)).Error);
        var entry = (await AuditAsync(AuditActions.SessionRevoked)).Single();
        Assert.AreEqual(assigned.TenantId, entry.TenantId);
        Assert.AreEqual("No role assignment left", entry.Detail);
    }

    [TestMethod]
    public async Task Removing_one_of_several_role_assignments_keeps_the_session()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        var other = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{assigned.TenantId}/role-assignments",
            new OSI.Signal9.Contracts.Roles.CreateRoleAssignmentRequest { UserId = assigned.UserId, RoleId = BuiltInRoles.Technician.Id },
            Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, other.StatusCode, await other.Content.ReadAsStringAsync(Ct));
        using var browser = await SignedInAsync(assigned.User);

        await _superadministrator.DeleteAsync(assigned.Assignment, Ct);
        _factory.Time.Advance(TimeSpan.FromMinutes(11));

        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("api/v1/tenants")).StatusCode);
        Assert.AreEqual(1, _factory.Web.Refreshes);
    }

    // The hourly forced refresh at Entra (story 52).

    [TestMethod]
    [DataRow("invalid_grant")]
    [DataRow("interaction_required")]
    public async Task Entra_refusing_the_person_ends_their_session_within_an_hour(string error)
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);

        _factory.Entra.Refuse(assigned.User, error);
        await EntraCheckAsync(0);

        await EventuallyAsync(async () => (await browser.GetAsync("api/v1/tenants")).StatusCode == HttpStatusCode.Unauthorized,
            "the session to end");
        var entry = (await AuditAsync(AuditActions.SessionRevoked)).Single();
        Assert.AreEqual(assigned.TenantId, entry.TenantId);
        Assert.AreEqual($"Entra refused the person: {error}", entry.Detail);
        Assert.AreEqual(WebClientRegistration.ClientId, entry.Actor.ClientId);
    }

    [TestMethod]
    public async Task The_entra_check_forces_a_refresh_at_entra_with_its_credential()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);

        await EntraCheckAsync(0);
        // Another hour: asked again, never served from MSAL's cache of access tokens.
        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        await browser.GetAsync("api/v1/tenants");
        await EntraCheckAsync(1);

        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("api/v1/tenants")).StatusCode);
        Assert.AreEqual(2, _factory.Entra.Refreshes);
        // Only what signing in needs: no API permission at Entra.
        foreach (var scope in _factory.Entra.RefreshScopes)
            CollectionAssert.IsSubsetOf(scope.Split(' '), new[] { "openid", "profile", "offline_access" }, scope);
    }

    [TestMethod]
    public async Task Entra_is_asked_at_most_once_an_hour()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);

        for (var i = 0; i < 10; i++)
        {
            _factory.Time.Advance(TimeSpan.FromMinutes(5));
            await browser.GetAsync("api/v1/tenants");
        }
        // A check now, whatever the background runs have got to.
        await using (var scope = _factory.Auth.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<EntraSessionChecker>().RunAsync(Ct);

        Assert.AreEqual(0, _factory.Entra.Refreshes, "auth asked Entra within the hour.");
    }

    public static IEnumerable<object[]> TransientEntraFailures =>
    [
        ["network", (Func<HttpRequestMessage, HttpResponseMessage?>)(_ => throw new HttpRequestException("Connection refused"))],
        ["server error", (Func<HttpRequestMessage, HttpResponseMessage?>)(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = JsonContent.Create(new { error = "temporarily_unavailable", error_description = "AADSTS90033" }),
        })],
        ["invalid_client", (Func<HttpRequestMessage, HttpResponseMessage?>)(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = JsonContent.Create(new { error = "invalid_client", error_description = "AADSTS7000215" }),
        })],
    ];

    [TestMethod]
    [DynamicData(nameof(TransientEntraFailures))]
    public async Task Any_other_answer_from_entra_keeps_the_session_and_entra_is_asked_again(string failure,
        Func<HttpRequestMessage, HttpResponseMessage?> fault)
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);

        _factory.Entra.RefreshFault = fault;
        await EntraCheckAsync(0);
        await HandedBackAsync(assigned.User);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("api/v1/tenants")).StatusCode, failure);
        _factory.Entra.RefreshFault = null;
        _factory.Entra.Refuse(assigned.User);
        var before = _factory.Entra.Refreshes;
        _factory.Time.Advance(EntraSessionCheckInterval);
        await EventuallyAsync(() => Task.FromResult(_factory.Entra.Refreshes > before), "Entra to be asked again");

        await EventuallyAsync(async () => (await browser.GetAsync("api/v1/tenants")).StatusCode == HttpStatusCode.Unauthorized,
            "the session to end");
    }

    private static readonly TimeSpan EntraSessionCheckInterval = EntraSessionCheck.Interval;

    // Superadministrator is re-read at every session check (story 59).

    [TestMethod]
    public async Task A_superadministrators_role_removal_takes_effect_at_the_next_session_check()
    {
        var other = await _factory.CreateTenantAsync(_superadministrator, "Elsewhere");
        var assigned = await _factory.AssignedPersonAsync(_factory.Superadministrator() with { SessionId = $"sid-{Guid.NewGuid():N}" });
        using var browser = await SignedInAsync(assigned.User);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync($"api/v1/tenants/{other.Id}")).StatusCode);

        _factory.Entra.SetRoles(assigned.User);
        await EntraCheckAsync(0);

        await EventuallyAsync(async () => (await browser.GetAsync($"api/v1/tenants/{other.Id}")).StatusCode == HttpStatusCode.NotFound,
            "the Superadministrator role to go");
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync($"api/v1/tenants/{assigned.TenantId}")).StatusCode,
            "The session ended rather than losing the role.");
    }

    [TestMethod]
    public async Task A_superadministrator_without_role_assignments_keeps_their_session_while_entra_says_so()
    {
        using var browser = await SignedInAsync(_factory.Superadministrator() with { SessionId = $"sid-{Guid.NewGuid():N}" });

        _factory.Time.Advance(TimeSpan.FromMinutes(11));

        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("api/v1/tenants")).StatusCode);
        Assert.AreEqual(1, _factory.Web.Refreshes);
    }

    // One person in several directories: their home account and a guest account elsewhere share MSAL's home account, but
    // are different people to Entra and Signal9 (ADR-0006, story 58).

    private async Task<SignInSession> SessionOfAsync(EntraUser user)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SignInSessions>();
        return (await sessions.FindByUserAsync(PersonSubject.Format(user.TenantId, user.ObjectId), Ct)).Single().Session;
    }

    [TestMethod]
    public async Task Entra_refusing_a_guest_account_ends_only_that_directorys_session()
    {
        var home = await _factory.AssignedPersonAsync(WithSession());
        var guest = await _factory.AssignedPersonAsync(home.User.GuestIn(Guid.NewGuid()));
        using var homeBrowser = await SignedInAsync(home.User);
        using var guestBrowser = await SignedInAsync(guest.User);

        _factory.Entra.Refuse(guest.User);
        await EntraCheckAsync(0);

        await EventuallyAsync(async () => (await guestBrowser.GetAsync("api/v1/tenants")).StatusCode == HttpStatusCode.Unauthorized,
            "the guest account's session to end");
        Assert.AreEqual(HttpStatusCode.OK, (await homeBrowser.GetAsync("api/v1/tenants")).StatusCode,
            "Entra refusing the guest account ended the home account's session.");
    }

    [TestMethod]
    public async Task Superadministrator_comes_only_from_the_deploying_directorys_session()
    {
        var home = await _factory.AssignedPersonAsync(WithSession());
        var guest = await _factory.AssignedPersonAsync(home.User.GuestIn(_factory.Entra.DeployingTenantId));
        using var homeBrowser = await SignedInAsync(home.User);
        using var guestBrowser = await SignedInAsync(guest.User);

        // The guest account in the deploying directory becomes a Superadministrator; the home account isn't one.
        _factory.Entra.SetRoles(guest.User, Superadministrator.AppRole);
        await EntraCheckAsync(0);

        await EventuallyAsync(async () => (await SessionOfAsync(guest.User)).Superadministrator,
            "the deploying directory's session to become a Superadministrator's");
        Assert.IsFalse((await SessionOfAsync(home.User)).Superadministrator,
            "The home directory's session got the deploying directory's Superadministrator role.");
    }

    [TestMethod]
    public async Task Entra_answering_for_someone_else_ends_the_session()
    {
        var assigned = await _factory.AssignedPersonAsync(_factory.Superadministrator() with { SessionId = $"sid-{Guid.NewGuid():N}" });
        using var browser = await SignedInAsync(assigned.User);

        // Another object in the same directory, under the same home account: not the person the session is for.
        _factory.Entra.AnswerAs(assigned.User, assigned.User with { ObjectId = Guid.NewGuid(), Home = assigned.User.HomeAccount });
        await EntraCheckAsync(0);

        await EventuallyAsync(async () => (await browser.GetAsync("api/v1/tenants")).StatusCode == HttpStatusCode.Unauthorized,
            "the session to end");
        var entry = (await AuditAsync(AuditActions.SessionRevoked)).Single();
        Assert.AreEqual("Entra answered for someone else", entry.Detail);
    }

    // Revoking an authorization revokes its tokens (OpenIddict 7.x workaround).

    [TestMethod]
    public async Task Revoking_an_authorization_revokes_all_of_its_tokens()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);
        _factory.Time.Advance(TimeSpan.FromMinutes(11));
        await browser.GetAsync("api/v1/tenants");

        await using var scope = _factory.Auth.Services.CreateAsyncScope();
        var authorizations = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
        var tokens = scope.ServiceProvider.GetRequiredService<IOpenIddictTokenManager>();
        var subject = PersonSubject.Format(assigned.User.TenantId, assigned.User.ObjectId);
        var authorization = await authorizations.FindBySubjectAsync(subject, Ct).SingleAsync(Ct);
        var id = (await authorizations.GetIdAsync(authorization, Ct))!;
        Assert.IsTrue(await authorizations.TryRevokeAsync(authorization, Ct));

        var statuses = new List<string?>();
        await foreach (var token in tokens.FindByAuthorizationIdAsync(id, Ct))
            statuses.Add(await tokens.GetStatusAsync(token, Ct));
        Assert.IsGreaterThanOrEqualTo(3, statuses.Count, "Expected the access and refresh tokens of two grants.");
        CollectionAssert.AreEqual(statuses.Select(_ => Statuses.Revoked).ToList(),
            statuses.Select(s => s == Statuses.Redeemed ? Statuses.Revoked : s).ToList(), string.Join(", ", statuses));
        Assert.DoesNotContain(Statuses.Valid, statuses);
    }

    // Explicit sign-out (story 53).

    [TestMethod]
    public async Task Signing_out_revokes_every_token_behind_the_session()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);
        var accessToken = _factory.Web.ApiRequests.Last(r => r.Uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            .Authorization!["Bearer ".Length..];
        var refreshToken = _factory.Web.IssuedRefreshTokens.Last();

        var signedOut = await browser.FetchAsync(HttpMethod.Post, BffRoutes.Logout);

        Assert.AreEqual(HttpStatusCode.NoContent, signedOut.StatusCode);
        var certificate = _factory.Web.Services.GetRequiredService<WebClientCertificate>().Current;
        using var api = _factory.CreateForwardedCertificateClient(certificate);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await api.GetAsync("api/v1/tenants", Ct)).StatusCode,
            "The access token still works after sign-out.");
        Assert.AreEqual("invalid_grant", (await RedeemAsWebAsync(refreshToken)).Error);
        var entry = (await AuditAsync(AuditActions.SignOut)).Single();
        Assert.AreEqual(assigned.TenantId, entry.TenantId);
        Assert.AreEqual(PersonSubject.Format(assigned.User.TenantId, assigned.User.ObjectId), entry.Actor.Subject);
        Assert.AreEqual(AuditActorKind.Person, entry.Actor.Kind);
    }

    // Entra's front-channel logout (story 51).

    private static string FrontChannelLogout(EntraUser user, string? sid = null) =>
        $"{OSI.Signal9.Auth.FrontChannelLogout.Path}?iss={Uri.EscapeDataString($"{FakeEntra.Instance}{user.TenantId}/v2.0")}" +
        $"&sid={Uri.EscapeDataString(sid ?? user.SessionId!)}";

    [TestMethod]
    public async Task Entras_front_channel_logout_ends_the_session_by_sid_without_a_cookie()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);
        var refreshToken = _factory.Web.IssuedRefreshTokens.Last();

        // Entra's hidden frame: a cross-site request, so auth's cookies don't come along.
        using var entra = _factory.Auth.CreateClient();
        var logout = await entra.GetAsync(FrontChannelLogout(assigned.User), Ct);

        Assert.AreEqual(HttpStatusCode.OK, logout.StatusCode, await logout.Content.ReadAsStringAsync(Ct));
        Assert.Contains("no-store", logout.Headers.CacheControl!.ToString());
        Assert.AreEqual("default-src 'none'; frame-ancestors https://login.microsoftonline.com",
            string.Join("", logout.Headers.GetValues("Content-Security-Policy")));
        Assert.IsFalse(logout.Headers.Contains("X-Frame-Options"), "Entra must be able to frame the page.");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/v1/tenants")).StatusCode);
        Assert.AreEqual("invalid_grant", (await RedeemAsWebAsync(refreshToken)).Error);
        var entry = (await AuditAsync(AuditActions.SessionRevoked)).Single();
        Assert.AreEqual(assigned.TenantId, entry.TenantId);
        Assert.AreEqual("Front-channel logout from Entra", entry.Detail);
    }

    [TestMethod]
    public async Task Front_channel_logout_is_scoped_to_the_directory_in_its_issuer()
    {
        var sid = $"sid-{Guid.NewGuid():N}";
        var assigned = await _factory.AssignedPersonAsync(EntraUser.WithMfa() with { SessionId = sid });
        using var browser = await SignedInAsync(assigned.User);
        var elsewhere = EntraUser.WithMfa() with { SessionId = sid };

        using var entra = _factory.Auth.CreateClient();
        var logout = await entra.GetAsync(FrontChannelLogout(elsewhere), Ct);

        Assert.AreEqual(HttpStatusCode.OK, logout.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("api/v1/tenants")).StatusCode);
        Assert.IsEmpty(await AuditAsync(AuditActions.SessionRevoked));
    }

    [TestMethod]
    public async Task After_front_channel_logout_auths_own_sign_in_no_longer_counts()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);
        using var entra = _factory.Auth.CreateClient();
        await entra.GetAsync(FrontChannelLogout(assigned.User), Ct);

        // Entra signed the person out, so signing in again gets a new Entra session.
        var landing = await browser.SignInAsync(assigned.User with { SessionId = $"sid-{Guid.NewGuid():N}" });

        Assert.AreEqual(HttpStatusCode.OK, landing.StatusCode);
        Assert.AreEqual(2, _factory.Entra.SignIns, "auth's sign-in from the ended Entra session was reused.");
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("api/v1/tenants")).StatusCode);
    }

    [TestMethod]
    [DataRow(null, "sid-1")]
    [DataRow("https://login.microsoftonline.com/organizations/v2.0", "sid-1")]
    [DataRow("https://evil.example/00000000-0000-0000-0000-000000000001/v2.0", "sid-1")]
    [DataRow("https://login.microsoftonline.com/0f0d1a4b-7c7e-4f4e-9c3e-2a1b3c4d5e6f/v2.0", null)]
    public async Task Front_channel_logout_needs_entras_issuer_and_a_sid(string? iss, string? sid)
    {
        using var entra = _factory.Auth.CreateClient();
        var query = string.Join('&', new[] { ("iss", iss), ("sid", sid) }
            .Where(p => p.Item2 is not null).Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2!)}"));

        var logout = await entra.GetAsync($"{OSI.Signal9.Auth.FrontChannelLogout.Path}?{query}", Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, logout.StatusCode);
        Assert.AreEqual("application/problem+json", logout.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task A_sign_in_without_a_sid_is_logged()
    {
        var assigned = await _factory.AssignedPersonAsync(EntraUser.WithMfa() with { SessionId = null });

        using var browser = await SignedInAsync(assigned.User);

        Assert.Contains(m => m.Contains("carried no sid", StringComparison.Ordinal), _factory.Auth.Logs);
    }

    // The seam for live-connection teardown.

    [TestMethod]
    public async Task Sessions_and_their_tokens_can_be_found_by_user_and_by_sid()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);
        using var other = await SignedInAsync((await _factory.AssignedPersonAsync(WithSession())).User);
        var subject = PersonSubject.Format(assigned.User.TenantId, assigned.User.ObjectId);

        await using var scope = _factory.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SignInSessions>();
        var byUser = await sessions.FindByUserAsync(subject, Ct);
        var bySid = await sessions.FindByEntraSessionAsync(assigned.User.TenantId, assigned.User.SessionId!, Ct);

        var session = byUser.Single();
        Assert.AreEqual(session.Session.AuthorizationId, bySid.Single().Session.AuthorizationId);
        Assert.AreEqual(WebClientRegistration.ClientId, session.Session.ClientId);
        Assert.IsGreaterThanOrEqualTo(2, session.TokenIds.Count, "The access and refresh tokens.");

        await browser.FetchAsync(HttpMethod.Post, BffRoutes.Logout);
        Assert.IsEmpty(await sessions.FindByUserAsync(subject, Ct), "A revoked session is still listed.");
    }

    public void Dispose()
    {
        _superadministrator.Dispose();
        _factory.Dispose();
    }
}
