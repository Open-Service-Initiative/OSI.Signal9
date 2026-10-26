using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.ApiApplications;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using OSI.Signal9.Contracts.Users;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Delegated API applications (#125, stories 81-83, 86-87): they act for a signed-in person who consented at auth, with
/// the person's access ∩ the application's scopes ∩ the application's subtree.
/// </summary>
[TestClass]
public sealed partial class DelegatedApiApplicationTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly HttpClient _superadministrator;
    private readonly TestContext _testContext;

    public DelegatedApiApplicationTests(TestContext testContext)
    {
        _testContext = testContext;
        _superadministrator = _factory.CreateClient();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task A_delegated_application_gets_the_persons_access_within_its_scopes_and_its_subtree()
    {
        var tree = await TreeAsync();
        var rootAdministrator = await PersonAsync(tree.Root, ("Administrator", tree.Root));
        var clientViewer = await PersonAsync(tree.Client, ("Viewer", tree.Client));
        var (application, secret) = await RegisterDelegatedAsync(tree.Msp, "openid", "tenants.read", "devices.read");

        using var asAdministrator = await DelegatedClientAsync(application, secret, rootAdministrator, "openid tenants.read");
        using var asViewer = await DelegatedClientAsync(application, secret, clientViewer, "openid tenants.read");
        var beyondItsScopes = await RefusedAsync(application, "openid tenants.write");

        CollectionAssert.AreEquivalent(new[] { tree.Msp, tree.Client }, await VisibleTenantsAsync(asAdministrator));
        CollectionAssert.AreEquivalent(new[] { tree.Client }, await VisibleTenantsAsync(asViewer));
        Assert.AreEqual(HttpStatusCode.NotFound, (await asAdministrator.GetAsync($"api/v1/tenants/{tree.Sibling}", Ct)).StatusCode);
        // OpenIddict refuses a scope the client may not use before it checks the redirect URI, so it shows the error.
        StringAssert.Contains(beyondItsScopes, "not allowed to use the specified scope");

        // Narrowing the application's scopes takes effect on its tokens' next request.
        var narrowed = await PatchAsync(application, """{"scopes":["openid","devices.read"]}""");
        Assert.AreEqual(HttpStatusCode.OK, narrowed.StatusCode, await narrowed.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.Forbidden, (await asAdministrator.GetAsync("api/v1/tenants", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Registering_a_delegated_application_needs_https_redirect_uris_and_no_role()
    {
        var tree = await TreeAsync();
        var mfa = await MfaSuperadministratorAsync();

        var created = await RegisterAsync(mfa, tree.Msp, Delegated("openid", "tenants.read"));
        var withRole = await RegisterAsync(mfa, tree.Msp, Delegated("tenants.read") with { RoleId = await RoleIdAsync("Viewer") });
        var plainHttp = await RegisterAsync(mfa, tree.Msp, Delegated("tenants.read") with { RedirectUris = ["http://client.example/callback"] });
        var fragment = await RegisterAsync(mfa, tree.Msp, Delegated("tenants.read") with { RedirectUris = ["https://client.example/cb#x"] });
        var none = await RegisterAsync(mfa, tree.Msp, Delegated("tenants.read") with { RedirectUris = [] });
        var agent = await RegisterAsync(mfa, tree.Msp, Delegated("agent"));
        var appOnlyWithoutRole = await RegisterAsync(mfa, tree.Msp, new CreateApiApplicationRequest
        {
            DisplayName = "Reporting",
            Scopes = ["tenants.read"],
            TokenEndpointAuthMethod = TokenEndpointAuthMethods.PrivateKeyJwt,
        });
        var appOnlyWithRedirect = await RegisterAsync(mfa, tree.Msp, new CreateApiApplicationRequest
        {
            DisplayName = "Reporting",
            RoleId = await RoleIdAsync("Viewer"),
            Scopes = ["tenants.read"],
            TokenEndpointAuthMethod = TokenEndpointAuthMethods.PrivateKeyJwt,
            RedirectUris = [CodeFlow.RedirectUri],
        });

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        var dto = (await created.Content.ReadFromJsonAsync<ApiApplicationDto>(Signal9Json.Options, Ct))!;
        Assert.AreEqual(ApiApplicationKinds.Delegated, dto.Kind);
        Assert.IsNull(dto.RoleId);
        CollectionAssert.AreEqual(new[] { CodeFlow.RedirectUri }, dto.RedirectUris.ToArray());
        Assert.IsTrue(dto.RequirePkce);
        Assert.IsFalse(dto.RequireNonce);
        foreach (var refused in new[] { withRole, plainHttp, fragment, none, agent, appOnlyWithoutRole, appOnlyWithRedirect })
            Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode, await refused.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Pkce_is_required_unless_turned_off_and_then_nonce_is()
    {
        var tree = await TreeAsync();
        var person = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));
        var mfa = await MfaSuperadministratorAsync();
        var (withPkce, _) = await RegisterDelegatedAsync(tree.Msp, "openid", "tenants.read");

        var withoutChallenge = await RefusedAsync(withPkce, "openid tenants.read", challengeMethod: null);
        var offWithoutNonce = await RegisterAsync(mfa, tree.Msp, Delegated("openid", "tenants.read") with { RequirePkce = false });
        var patchedOffWithoutNonce = await PatchAsync(withPkce, """{"requirePkce":false}""");
        var (withNonce, secret) = await RegisterDelegatedAsync(tree.Msp, Delegated("openid", "tenants.read") with
        {
            RequirePkce = false,
            RequireNonce = true,
        });
        var missingNonce = await RefusedAsync(withNonce, "openid tenants.read", challengeMethod: null, nonce: null);
        // Nonce protects only the ID token, which only an OpenID Connect request gets.
        var notOpenId = await RefusedAsync(withNonce, "tenants.read", challengeMethod: null);
        var flow = new CodeFlow(withNonce.ClientId);
        using var browser = new Browser(_factory.Auth.Server);
        var (redirect, _) = await browser.AuthorizeWithConsentAsync(
            flow.AuthorizeRequest("openid tenants.read", challengeMethod: null, nonce: "n-0S6_WzA2Mj"), _factory.Entra, person);
        var redeemed = await RedeemAsync(flow, flow.CodeFrom(redirect), secret, withVerifier: false);

        // Like a missing code challenge, a missing nonce is shown rather than sent to the redirect URI.
        StringAssert.Contains(withoutChallenge, "invalid_request");
        Assert.AreEqual(HttpStatusCode.BadRequest, offWithoutNonce.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, patchedOffWithoutNonce.StatusCode);
        StringAssert.Contains(missingNonce, "nonce");
        StringAssert.Contains(notOpenId, "nonce");
        Assert.AreEqual(HttpStatusCode.OK, redeemed.StatusCode, await redeemed.Content.ReadAsStringAsync(Ct));
        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(
            (await redeemed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id_token").GetString());
        Assert.AreEqual("n-0S6_WzA2Mj", idToken.Payload.Nonce);
    }

    [TestMethod]
    public async Task Pushed_authorization_requests_work_when_the_client_uses_them()
    {
        var tree = await TreeAsync();
        var person = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));
        var (application, secret) = await RegisterDelegatedAsync(tree.Msp, "openid", "tenants.read");
        using var auth = _factory.Auth.CreateClient();
        var discovery = await auth.GetFromJsonAsync<JsonElement>(".well-known/openid-configuration", Ct);
        var flow = new CodeFlow(application.ClientId);
        var parameters = flow.Parameters();
        parameters["client_secret"] = secret;

        var pushed = await auth.PostAsync("connect/par", new FormUrlEncodedContent(parameters), Ct);
        Assert.AreEqual(HttpStatusCode.Created, pushed.StatusCode, await pushed.Content.ReadAsStringAsync(Ct));
        var requestUri = (await pushed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("request_uri").GetString()!;
        using var browser = new Browser(_factory.Auth.Server);
        var (redirect, asked) = await browser.AuthorizeWithConsentAsync(
            QueryHelpers.AddQueryString("connect/authorize", new Dictionary<string, string?>
            {
                ["client_id"] = application.ClientId,
                ["request_uri"] = requestUri,
            }), _factory.Entra, person);
        var redeemed = await RedeemAsync(flow, flow.CodeFrom(redirect), secret);

        Assert.EndsWith("/connect/par", discovery.GetProperty("pushed_authorization_request_endpoint").GetString());
        Assert.IsFalse(discovery.GetProperty("mtls_endpoint_aliases").TryGetProperty("pushed_authorization_request_endpoint", out _));
        Assert.IsTrue(asked);
        Assert.AreEqual(HttpStatusCode.OK, redeemed.StatusCode, await redeemed.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task The_consent_screen_shows_the_applications_name_as_text_and_loads_nothing()
    {
        var tree = await TreeAsync();
        var person = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));
        var (application, _) = await RegisterDelegatedAsync(tree.Msp, Delegated("openid", "tenants.read") with
        {
            DisplayName = "<script>alert(1)</script>",
        });

        var page = await ConsentPageAsync(application, person, "openid tenants.read");

        var html = await page.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("<script", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.AreEqual("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual($"default-src 'none'; form-action 'self' {new Uri(CodeFlow.RedirectUri).GetLeftPart(UriPartial.Authority)}; frame-ancestors 'none'; base-uri 'none'",
            page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.AreEqual("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.IsTrue(page.Headers.CacheControl?.NoStore);
    }

    [TestMethod]
    public async Task Consent_is_asked_on_first_use_and_again_for_more_scopes_and_is_kept()
    {
        var tree = await TreeAsync();
        var person = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));
        var (application, _) = await RegisterDelegatedAsync(tree.Msp, "openid", "tenants.read", "devices.read");

        var unanswered = await AuthorizeAsync(application, person, "openid tenants.read", prompt: "none");
        var page = await ConsentPageAsync(application, person, "openid tenants.read");
        var first = await AuthorizeAsync(application, person, "openid tenants.read");
        var again = await AuthorizeAsync(application, person, "openid tenants.read");
        var more = await AuthorizeAsync(application, person, "openid tenants.read devices.read");
        var subset = await AuthorizeAsync(application, person, "openid devices.read");
        var silent = await AuthorizeAsync(application, person, "openid tenants.read", prompt: "none");
        var declined = await AuthorizeAsync(application, await PersonAsync(tree.Msp, ("Viewer", tree.Msp)), "openid tenants.read",
            accept: false);

        Assert.AreEqual("consent_required", CodeFlow.ErrorFrom(unanswered.Redirect));
        var html = await page.Content.ReadAsStringAsync(Ct);
        StringAssert.Contains(html, "Delegated reporting");
        StringAssert.Contains(html, "View tenants.");
        StringAssert.Contains(page.Headers.GetValues("Content-Security-Policy").Single(), "frame-ancestors 'none'");
        Assert.IsTrue(first.AskedForConsent);
        Assert.IsFalse(again.AskedForConsent);
        Assert.IsTrue(more.AskedForConsent);
        Assert.IsFalse(subset.AskedForConsent);
        Assert.IsFalse(silent.AskedForConsent);
        Assert.IsTrue(QueryHelpers.ParseQuery(silent.Redirect.Query).ContainsKey("code"), silent.Redirect.ToString());
        Assert.AreEqual("access_denied", CodeFlow.ErrorFrom(declined.Redirect));
    }

    [TestMethod]
    public async Task Consent_needs_the_persons_own_session()
    {
        var tree = await TreeAsync();
        var person = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));
        var (application, _) = await RegisterDelegatedAsync(tree.Msp, "openid", "tenants.read");
        using var browser = new Browser(_factory.Auth.Server);
        var flow = new CodeFlow(application.ClientId);
        var page = await browser.SendAuthorizeAsync(flow.AuthorizeRequest("openid tenants.read"), _factory.Entra, person);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);

        // The person's own browser, but a form posted from another site, without the page's antiforgery token.
        var forged = await browser.AnswerConsentAsync(page, accept: true, omitAntiforgery: true);

        Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode);
    }

    [TestMethod]
    public async Task The_dashboard_never_asks_for_consent_and_nobody_consents_for_anyone_else()
    {
        var tree = await TreeAsync();
        var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var viewer = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));
        var (application, _) = await RegisterDelegatedAsync(tree.Msp, "openid", "tenants.read");
        using var dashboard = new DashboardBrowser(_factory);

        var landing = await dashboard.SignInAsync(administrator);
        var byAdministrator = await AuthorizeAsync(application, administrator, "openid tenants.read");
        var byViewer = await AuthorizeAsync(application, viewer, "openid tenants.read");
        // There's nothing to turn consent off with.
        var patched = await PatchAsync(application, """{"consentType":"implicit"}""");
        var afterPatch = await AuthorizeAsync(application, await PersonAsync(tree.Msp, ("Viewer", tree.Msp)), "openid tenants.read");

        Assert.AreEqual(HttpStatusCode.OK, landing.StatusCode);
        Assert.AreEqual(Signal9WebFactory.BaseUri.Host, dashboard.Received[^1].Uri.Host);
        Assert.IsTrue(byAdministrator.AskedForConsent);
        Assert.IsTrue(byViewer.AskedForConsent);
        Assert.AreNotEqual(HttpStatusCode.InternalServerError, patched.StatusCode);
        Assert.IsTrue(afterPatch.AskedForConsent);
    }

    [TestMethod]
    public async Task Deployment_scopes_reach_only_superadministrators_and_mfa_scopes_need_the_dashboards_recent_mfa()
    {
        var tree = await TreeAsync();
        var leaf = await _factory.CreateTenantAsync(_superadministrator, "Leaf", parentTenantId: tree.Client);
        var administrator = await PersonAsync(tree.Msp, ("Administrator", tree.Msp));
        var (application, secret) = await RegisterDelegatedAsync(tree.Msp, "openid", "tenants.read", "tenants.delete", "deployment.read");
        using var api = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(DeploymentProbeController).Assembly)));
        async Task<HttpStatusCode> ProbeAsync(HttpClient client)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "test/deployment-probe");
            request.Headers.Authorization = client.DefaultRequestHeaders.Authorization;
            using var server = api.CreateClient();
            return (await server.SendAsync(request, Ct)).StatusCode;
        }

        using var asSuperadministrator = await DelegatedClientAsync(application, secret, _factory.Superadministrator(),
            "openid tenants.read deployment.read");
        using var administratorsBrowser = new Browser(_factory.Auth.Server);
        using var asAdministrator = await DelegatedClientAsync(application, secret, administrator,
            "openid tenants.read tenants.delete deployment.read", browser: administratorsBrowser);

        Assert.AreEqual(HttpStatusCode.OK, await ProbeAsync(asSuperadministrator));
        Assert.AreEqual(HttpStatusCode.Forbidden, await ProbeAsync(asAdministrator));
        // A Superadministrator is still held to the application's subtree.
        CollectionAssert.AreEquivalent(new[] { tree.Msp, tree.Client, leaf.Id }, await VisibleTenantsAsync(asSuperadministrator));

        // Deleting a tenant needs a recent MFA, through a delegated application as through the dashboard (RFC 9470).
        _factory.Time.Advance(TimeSpan.FromMinutes(16));
        using var later = await DelegatedClientAsync(application, secret, administrator, "openid tenants.read tenants.delete",
            browser: administratorsBrowser);
        var stale = await later.DeleteAsync($"api/v1/tenants/{leaf.Id}", Ct);
        Assert.AreEqual(HttpStatusCode.Unauthorized, stale.StatusCode);
        StringAssert.Contains(stale.Headers.WwwAuthenticate.ToString(), "insufficient_user_authentication");
        using var stepped = await DelegatedClientAsync(application, secret, administrator, "openid tenants.read tenants.delete",
            maxAge: 900, browser: administratorsBrowser);
        Assert.AreEqual(HttpStatusCode.NoContent, (await stepped.DeleteAsync($"api/v1/tenants/{leaf.Id}", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task A_superadministrator_through_a_delegated_application_acts_above_its_descendants_but_not_at_its_tenant()
    {
        var tree = await TreeAsync();
        var leaf = await _factory.CreateTenantAsync(_superadministrator, "Leaf", parentTenantId: tree.Client);
        var (application, secret) = await RegisterDelegatedAsync(tree.Msp, "openid", "tenants.read", "tenants.delete");
        using var asSuperadministrator = await DelegatedClientAsync(application, secret, _factory.Superadministrator(),
            "openid tenants.read tenants.delete");

        // Held to the application's subtree as an Administrator at its tenant, like a person assigned above it.
        var atItsTenant = await asSuperadministrator.DeleteAsync($"api/v1/tenants/{tree.Msp}", Ct);
        var beneathIt = await asSuperadministrator.DeleteAsync($"api/v1/tenants/{leaf.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, atItsTenant.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, beneathIt.StatusCode, await beneathIt.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Consent_grants_are_audited_at_the_applications_tenant()
    {
        var tree = await TreeAsync();
        var person = await PersonAsync(tree.Client, ("Viewer", tree.Client));
        var (application, _) = await RegisterDelegatedAsync(tree.Msp, "openid", "tenants.read", "devices.read");
        await AuthorizeAsync(application, person, "openid tenants.read");
        await AuthorizeAsync(application, person, "openid tenants.read");
        await AuthorizeAsync(application, person, "openid devices.read");
        using var siblingAdministrator = await _factory.CreatePersonClientAsync(await PersonAsync(tree.Sibling, ("Administrator", tree.Sibling)));
        using var rootAdministrator = await _factory.CreatePersonClientAsync(await PersonAsync(tree.Root, ("Administrator", tree.Root)));

        var grants = (await TrailAsync(rootAdministrator)).Where(e => e.Action == AuditActions.ApiApplicationConsentGranted).ToList();
        var beside = (await TrailAsync(siblingAdministrator)).Where(e => e.Action == AuditActions.ApiApplicationConsentGranted).ToList();

        Assert.HasCount(2, grants);
        Assert.IsTrue(grants.All(e => e.TenantId == tree.Msp && e.TargetType == "api-application" && e.TargetId == application.Id));
        Assert.IsTrue(grants.All(e => e.Actor.Kind == AuditActorKind.Person && e.Actor.Subject == $"{person.TenantId}:{person.ObjectId}"
            && e.Actor.ClientId == application.ClientId));
        Assert.IsTrue(grants.Any(e => e.Detail == "scopes: openid tenants.read devices.read"), string.Join(" | ", grants.Select(e => e.Detail)));
        Assert.IsEmpty(beside);
    }

    [TestMethod]
    public async Task Refresh_tokens_last_14_idle_days_and_90_days_in_all()
    {
        var tree = await TreeAsync();
        var person = await PersonAsync(tree.Msp, ("Viewer", tree.Msp));
        var (application, secret) = await RegisterDelegatedAsync(tree.Msp, "openid", "offline_access", "tenants.read");

        var used = await RefreshTokenAsync(application, secret, person);
        var idle = await RefreshTokenAsync(application, secret, person);

        // One chain is used every 13 days; the other lies unused for 15.
        async Task<string> RefreshedAsync(string token)
        {
            var refreshed = await RefreshAsync(application, secret, token);
            Assert.AreEqual(HttpStatusCode.OK, refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync(Ct));
            return (await refreshed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("refresh_token").GetString()!;
        }
        _factory.Time.Advance(TimeSpan.FromDays(13));
        used = await RefreshedAsync(used);
        _factory.Time.Advance(TimeSpan.FromDays(2));
        var afterIdle = await RefreshAsync(application, secret, idle);
        _factory.Time.Advance(TimeSpan.FromDays(11));
        used = await RefreshedAsync(used);
        for (var day = 39; day <= 78; day += 13)
        {
            _factory.Time.Advance(TimeSpan.FromDays(13));
            used = await RefreshedAsync(used);
        }
        _factory.Time.Advance(TimeSpan.FromDays(13));
        var pastCap = await RefreshAsync(application, secret, used);

        Assert.AreEqual(HttpStatusCode.BadRequest, afterIdle.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, pastCap.StatusCode, "day 91");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static string ApplicationsUrl(Guid tenantId) => $"api/v1/tenants/{tenantId}/api-applications";

    private static CreateApiApplicationRequest Delegated(params string[] scopes) => new()
    {
        DisplayName = "Delegated reporting",
        Kind = ApiApplicationKinds.Delegated,
        Scopes = scopes,
        TokenEndpointAuthMethod = TokenEndpointAuthMethods.ClientSecret,
        RedirectUris = [CodeFlow.RedirectUri],
    };

    private Task<HttpResponseMessage> RegisterAsync(HttpClient caller, Guid tenantId, CreateApiApplicationRequest request) =>
        caller.PostAsJsonAsync(ApplicationsUrl(tenantId), request, Signal9Json.Options, Ct);

    private Task<(ApiApplicationDto Application, string Secret)> RegisterDelegatedAsync(Guid tenantId, params string[] scopes) =>
        RegisterDelegatedAsync(tenantId, Delegated(scopes));

    private async Task<(ApiApplicationDto Application, string Secret)> RegisterDelegatedAsync(Guid tenantId, CreateApiApplicationRequest request)
    {
        var mfa = await MfaSuperadministratorAsync();
        var response = await RegisterAsync(mfa, tenantId, request);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var application = (await response.Content.ReadFromJsonAsync<ApiApplicationDto>(Signal9Json.Options, Ct))!;
        var secret = await mfa.PostAsJsonAsync($"{ApplicationsUrl(tenantId)}/{application.Id}/credentials",
            new CreateApiApplicationCredentialRequest(), Signal9Json.Options, Ct);
        Assert.AreEqual(HttpStatusCode.Created, secret.StatusCode, await secret.Content.ReadAsStringAsync(Ct));
        return (application, (await secret.Content.ReadFromJsonAsync<ApiApplicationCredentialDto>(Signal9Json.Options, Ct))!.Secret!);
    }

    private async Task<HttpResponseMessage> PatchAsync(ApiApplicationDto application, string json)
    {
        var mfa = await MfaSuperadministratorAsync();
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/merge-patch+json");
        return await mfa.PatchAsync($"{ApplicationsUrl(application.TenantId)}/{application.Id}", content, Ct);
    }

    private HttpClient? _mfaSuperadministrator;

    private async Task<HttpClient> MfaSuperadministratorAsync() =>
        _mfaSuperadministrator ??= await _factory.CreatePersonClientAsync(_factory.Superadministrator());

    /// <summary>One authorization request in a new browser, answering the consent screen if shown.</summary>
    private async Task<(Uri Redirect, bool AskedForConsent)> AuthorizeAsync(ApiApplicationDto application, EntraUser person,
        string scope, string? challengeMethod = "S256", string? nonce = "client-nonce", string? prompt = null, bool accept = true)
    {
        using var browser = new Browser(_factory.Auth.Server);
        var flow = new CodeFlow(application.ClientId);
        var request = flow.AuthorizeRequest(scope, challengeMethod, nonce);
        if (prompt is not null)
        {
            // prompt=none needs a session at auth: sign in first, then ask silently.
            if (prompt == "none")
                await browser.SendAuthorizeAsync(new CodeFlow(application.ClientId).AuthorizeRequest(scope), _factory.Entra, person);
            request = QueryHelpers.AddQueryString(request, "prompt", prompt);
        }
        return await browser.AuthorizeWithConsentAsync(request, _factory.Entra, person, accept);
    }

    /// <summary>An authorization request auth refuses outright, before any sign-in: the body of its error page.</summary>
    private async Task<string> RefusedAsync(ApiApplicationDto application, string scope, string? challengeMethod = "S256",
        string? nonce = "client-nonce")
    {
        using var browser = new Browser(_factory.Auth.Server);
        var response = await browser.GetAsync(new CodeFlow(application.ClientId).AuthorizeRequest(scope, challengeMethod, nonce));
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body);
        return body;
    }

    private async Task<HttpResponseMessage> ConsentPageAsync(ApiApplicationDto application, EntraUser person, string scope)
    {
        using var browser = new Browser(_factory.Auth.Server);
        var page = await browser.SendAuthorizeAsync(new CodeFlow(application.ClientId).AuthorizeRequest(scope), _factory.Entra, person);
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        return page;
    }

    private async Task<HttpResponseMessage> RedeemAsync(CodeFlow flow, string code, string secret, bool withVerifier = true)
    {
        using var auth = _factory.Auth.CreateClient();
        return await auth.PostAsync("connect/token", flow.Redemption(code, secret, withVerifier), Ct);
    }

    private async Task<JsonElement> TokensAsync(ApiApplicationDto application, string secret, EntraUser person, string scope,
        int? maxAge = null, Browser? browser = null)
    {
        using var ownBrowser = browser is null ? new Browser(_factory.Auth.Server) : null;
        browser ??= ownBrowser!;
        var flow = new CodeFlow(application.ClientId);
        var (redirect, _) = await browser.AuthorizeWithConsentAsync(flow.AuthorizeRequest(scope, maxAge: maxAge), _factory.Entra, person);
        var response = await RedeemAsync(flow, flow.CodeFrom(redirect), secret);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private async Task<HttpClient> DelegatedClientAsync(ApiApplicationDto application, string secret, EntraUser person, string scope,
        int? maxAge = null, Browser? browser = null)
    {
        var tokens = await TokensAsync(application, secret, person, scope, maxAge, browser);
        var client = _factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        return client;
    }

    private async Task<string> RefreshTokenAsync(ApiApplicationDto application, string secret, EntraUser person) =>
        (await TokensAsync(application, secret, person, "openid offline_access tenants.read")).GetProperty("refresh_token").GetString()!;

    private async Task<HttpResponseMessage> RefreshAsync(ApiApplicationDto application, string secret, string refreshToken)
    {
        using var auth = _factory.Auth.CreateClient();
        return await auth.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = application.ClientId,
            ["client_secret"] = secret,
        }), Ct);
    }

    private async Task<Guid[]> VisibleTenantsAsync(HttpClient caller)
    {
        var response = await caller.GetAsync("api/v1/tenants?pageSize=100", Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return [.. (await response.Content.ReadFromJsonAsync<PagedResult<TenantDto>>(Signal9Json.Options, Ct))!.Items.Select(t => t.Id)];
    }

    private async Task<List<AuditEntryDto>> TrailAsync(HttpClient caller)
    {
        var response = await caller.GetAsync("api/v1/audit-entries?pageSize=100", Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return [.. (await response.Content.ReadFromJsonAsync<PagedResult<AuditEntryDto>>(Signal9Json.Options, Ct))!.Items];
    }

    private sealed record Tree(Guid Root, Guid Msp, Guid Client, Guid Sibling);

    /// <summary>Root → MSP → Client, and Root → Sibling.</summary>
    private async Task<Tree> TreeAsync()
    {
        var root = await _factory.CreateTenantAsync(_superadministrator, "Root");
        var msp = await _factory.CreateTenantAsync(_superadministrator, "Msp", parentTenantId: root.Id);
        var client = await _factory.CreateTenantAsync(_superadministrator, "Client", parentTenantId: msp.Id);
        var sibling = await _factory.CreateTenantAsync(_superadministrator, "Sibling", parentTenantId: root.Id);
        return new Tree(root.Id, msp.Id, client.Id, sibling.Id);
    }

    /// <summary>A person with a user at <paramref name="owner"/> and these role assignments, who hasn't signed in yet.</summary>
    private async Task<EntraUser> PersonAsync(Guid owner, params (string Role, Guid Tenant)[] assignments)
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
        foreach (var (role, tenant) in assignments)
        {
            var assigned = await _superadministrator.PostAsJsonAsync($"api/v1/tenants/{tenant}/role-assignments",
                new CreateRoleAssignmentRequest { UserId = user.Id, RoleId = await RoleIdAsync(role) }, Signal9Json.Options, Ct);
            Assert.AreEqual(HttpStatusCode.Created, assigned.StatusCode, await assigned.Content.ReadAsStringAsync(Ct));
        }
        return person;
    }

    private List<RoleDto>? _roles;

    private async Task<Guid> RoleIdAsync(string name)
    {
        _roles ??= [.. (await _superadministrator.GetFromJsonAsync<PagedResult<RoleDto>>("api/v1/roles", Signal9Json.Options, Ct))!.Items];
        return _roles.Single(r => r.Name == name).Id;
    }

    public void Dispose()
    {
        _mfaSuperadministrator?.Dispose();
        _superadministrator.Dispose();
        _factory.Dispose();
    }
}
