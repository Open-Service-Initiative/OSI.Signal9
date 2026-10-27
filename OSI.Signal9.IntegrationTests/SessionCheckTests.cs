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
        _superadministrator = _factory.CreateSuperadministratorClient();
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

    /// <summary>
    /// Moves the clock on by <paramref name="by"/> and waits for the run of the Entra check that this starts, which asks
    /// Entra about everyone due, to finish.
    /// </summary>
    private async Task EntraCheckAfterAsync(TimeSpan by)
    {
        var run = _factory.Jobs.NextRunAsync(nameof(EntraSessionCheck));
        _factory.Time.Advance(by);
        await run.SignalledAsync("the Entra check's run");
    }

    /// <summary>
    /// Lets an hour pass, less the check's own interval, a run of the Entra check at a time, waiting for each: the one
    /// at 55 minutes asks Entra about everyone who signed in at the start.
    /// </summary>
    private async Task EntraCheckAsync()
    {
        for (var elapsed = TimeSpan.Zero; elapsed < EntraSessionCheck.Every - EntraSessionCheck.Interval; elapsed += EntraSessionCheck.Interval)
            await EntraCheckAfterAsync(EntraSessionCheck.Interval);
    }

    /// <summary>The audit trail, read through the public API by the Superadministrator, who sees every tenant.</summary>
    private async Task<List<AuditEntryDto>> AuditAsync(string action)
    {
        return [.. (await _superadministrator.GetFromJsonAsync<PagedResult<AuditEntryDto>>("api/v1/audit-entries?pageSize=100",
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
        await EntraCheckAsync();

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/v1/tenants")).StatusCode);
        Assert.HasCount(1, _factory.Entra.RefreshesOf(assigned.User));
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

        await EntraCheckAsync();
        // Another hour: asked again, never served from MSAL's cache of access tokens.
        _factory.Time.Advance(TimeSpan.FromMinutes(1));
        await browser.GetAsync("api/v1/tenants");
        await EntraCheckAsync();

        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("api/v1/tenants")).StatusCode);
        Assert.HasCount(2, _factory.Entra.RefreshesOf(assigned.User));
        // Only what signing in needs: no API permission at Entra.
        foreach (var scope in _factory.Entra.RefreshScopes)
            CollectionAssert.IsSubsetOf(scope.Split(' '), new[] { "openid", "profile", "offline_access" }, scope);
    }

    [TestMethod]
    public async Task Entra_is_asked_at_most_once_an_hour()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);

        // Every run of the check within the hour, with the person active throughout.
        for (var minutes = 5; minutes <= 50; minutes += 5)
        {
            await EntraCheckAfterAsync(TimeSpan.FromMinutes(5));
            Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("api/v1/tenants")).StatusCode);
            Assert.IsEmpty(_factory.Entra.RefreshesOf(assigned.User), $"auth asked Entra after {minutes} minutes.");
        }
        await EntraCheckAfterAsync(TimeSpan.FromMinutes(5));

        Assert.HasCount(1, _factory.Entra.RefreshesOf(assigned.User), "auth didn't ask Entra within the hour.");
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

        _factory.Entra.FailRefreshes(assigned.User, fault);
        // Every run within the hour, then the one at 55 minutes, which asks Entra.
        for (var minutes = 5; minutes < 55; minutes += 5)
            await EntraCheckAfterAsync(EntraSessionCheck.Interval);
        var failing = _factory.Jobs.NextRunAsync(nameof(EntraSessionCheck));
        _factory.Time.Advance(EntraSessionCheck.Interval);
        // Asking takes more than one call: the outbound resilience handler retries network errors and 5xx after a
        // backoff on the fixture's clock, within its total timeout of 30 seconds; MSAL then tries once more, through the
        // handler again; and Microsoft.Identity.Web asks again after invalid_client with its credential reloaded. After
        // each call that begins a try, a minute (short of the next run) ends that try's retries.
        for (var asks = 1; ; asks++)
        {
            var nextAsk = _factory.Entra.RefreshedAsync(assigned.User, asks);
            if (await Task.WhenAny(failing, nextAsk).SignalledAsync("auth asking Entra, or the run ending") == failing)
                break;
            _factory.Time.Advance(TimeSpan.FromMinutes(1));
        }
        await failing.SignalledAsync("the Entra check's run");
        var asked = _factory.Entra.RefreshesOf(assigned.User).Count;

        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("api/v1/tenants")).StatusCode, failure);
        // Logs name sessions by Signal9's own IDs, never the person's Entra object or directory ID.
        Assert.DoesNotContain(m => m.Contains(assigned.User.ObjectId.ToString(), StringComparison.OrdinalIgnoreCase)
            || m.Contains(assigned.User.TenantId.ToString(), StringComparison.OrdinalIgnoreCase), _factory.Auth.Logs, failure);
        _factory.Entra.FailRefreshes(assigned.User, null);
        _factory.Entra.Refuse(assigned.User);
        await EntraCheckAfterAsync(EntraSessionCheck.Interval);

        Assert.HasCount(asked + 1, _factory.Entra.RefreshesOf(assigned.User), "Entra wasn't asked again at the next run.");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/v1/tenants")).StatusCode);
    }

    // Superadministrator is re-read at every session check (story 59).

    [TestMethod]
    public async Task A_superadministrators_role_removal_takes_effect_at_the_next_session_check()
    {
        var other = await _factory.CreateTenantAsync(_superadministrator, "Elsewhere");
        var assigned = await _factory.AssignedPersonAsync(_factory.Superadministrator() with { SessionId = $"sid-{Guid.NewGuid():N}" });
        using var browser = await SignedInAsync(assigned.User);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync($"api/v1/tenants/{other.Id}")).StatusCode);

        _factory.Entra.SetRoles(assigned.User);
        await EntraCheckAsync();

        Assert.AreEqual(HttpStatusCode.NotFound, (await browser.GetAsync($"api/v1/tenants/{other.Id}")).StatusCode,
            "The Superadministrator role outlived the check.");
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

    [TestMethod]
    public async Task Entra_refusing_a_guest_account_ends_only_that_directorys_session()
    {
        var home = await _factory.AssignedPersonAsync(WithSession());
        var guest = await _factory.AssignedPersonAsync(home.User.GuestIn(Guid.NewGuid()));
        using var homeBrowser = await SignedInAsync(home.User);
        using var guestBrowser = await SignedInAsync(guest.User);

        _factory.Entra.Refuse(guest.User);
        await EntraCheckAsync();

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await guestBrowser.GetAsync("api/v1/tenants")).StatusCode,
            "Entra refusing the guest account left its session.");
        Assert.AreEqual(HttpStatusCode.OK, (await homeBrowser.GetAsync("api/v1/tenants")).StatusCode,
            "Entra refusing the guest account ended the home account's session.");
    }

    [TestMethod]
    public async Task Superadministrator_comes_only_from_the_deploying_directorys_session()
    {
        var elsewhere = await _factory.CreateTenantAsync(_superadministrator, "Elsewhere");
        var home = await _factory.AssignedPersonAsync(WithSession());
        var guest = await _factory.AssignedPersonAsync(home.User.GuestIn(_factory.Entra.DeployingTenantId));
        using var homeBrowser = await SignedInAsync(home.User);
        using var guestBrowser = await SignedInAsync(guest.User);

        // The guest account in the deploying directory becomes a Superadministrator; the home account isn't one.
        _factory.Entra.SetRoles(guest.User, Superadministrator.AppRole);
        await EntraCheckAsync();

        // Their access tokens have expired by now, so each next request refreshes with the role as the check found it.
        Assert.AreEqual(HttpStatusCode.OK, (await guestBrowser.GetAsync($"api/v1/tenants/{elsewhere.Id}")).StatusCode,
            "The deploying directory's session didn't become a Superadministrator's.");
        Assert.AreEqual(HttpStatusCode.NotFound, (await homeBrowser.GetAsync($"api/v1/tenants/{elsewhere.Id}")).StatusCode,
            "The home directory's session got the deploying directory's Superadministrator role.");
    }

    [TestMethod]
    public async Task Entra_answering_for_someone_else_ends_the_session()
    {
        var assigned = await _factory.AssignedPersonAsync(_factory.Superadministrator() with { SessionId = $"sid-{Guid.NewGuid():N}" });
        using var browser = await SignedInAsync(assigned.User);

        // Another object in the same directory, under the same home account: not the person the session is for.
        _factory.Entra.AnswerAs(assigned.User, assigned.User with { ObjectId = Guid.NewGuid(), Home = assigned.User.HomeAccount });
        await EntraCheckAsync();

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/v1/tenants")).StatusCode);
        var entry = (await AuditAsync(AuditActions.SessionRevoked)).Single();
        Assert.AreEqual("Entra answered for someone else", entry.Detail);
    }

    // Revoking an authorization revokes its tokens (OpenIddict 7.x workaround).

    [TestMethod]
    public async Task Ending_a_session_revokes_the_tokens_of_every_grant_behind_it()
    {
        var assigned = await _factory.AssignedPersonAsync(WithSession());
        using var browser = await SignedInAsync(assigned.User);
        var first = LastAccessToken();
        Assert.AreEqual(HttpStatusCode.OK, await CallApiAsWebAsync(first));
        _factory.Time.Advance(TimeSpan.FromMinutes(11));
        Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync("api/v1/tenants")).StatusCode);
        var refreshed = LastAccessToken();
        Assert.AreNotEqual(first, refreshed, "web didn't refresh.");
        var refreshToken = _factory.Web.IssuedRefreshTokens.Last();

        // Entra's front-channel logout ends the session at auth alone, by revoking its authorization.
        using var entra = _factory.Auth.CreateClient();
        await entra.GetAsync(FrontChannelLogout(assigned.User), Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, await CallApiAsWebAsync(refreshed), "The refreshed access token still works.");
        Assert.AreEqual("invalid_grant", (await RedeemAsWebAsync(refreshToken)).Error);
    }

    /// <summary>Calls api with an access token web holds, presenting web's certificate, as the token is bound to it.</summary>
    private async Task<HttpStatusCode> CallApiAsWebAsync(string accessToken)
    {
        var certificate = _factory.Web.Services.GetRequiredService<WebClientCertificate>().Current;
        using var api = _factory.CreateForwardedCertificateClient(certificate);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return (await api.GetAsync("api/v1/tenants", Ct)).StatusCode;
    }

    private string LastAccessToken() =>
        _factory.Web.ApiRequests.Last(r => r.Uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)).Authorization!["Bearer ".Length..];

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
        Assert.AreEqual("default-src 'none'; form-action 'none'; frame-ancestors https://login.microsoftonline.com; base-uri 'none'",
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
        Assert.HasCount(2, _factory.Entra.SignInsOf(assigned.User), "auth's sign-in from the ended Entra session was reused.");
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

    public void Dispose()
    {
        _superadministrator.Dispose();
        _factory.Dispose();
    }
}
