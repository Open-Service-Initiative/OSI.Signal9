using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Sensitive actions need MFA within the last 15 minutes, judged from the access token's <c>auth_time</c> and
/// <c>amr</c> (stories 45, 63, #120). A delegated token without it gets RFC 9470's step-up challenge, and repeating
/// authorization code with <c>max_age</c> sends the person back to Entra for a fresh sign-in.
/// </summary>
[TestClass]
public sealed class RecentMfaTests : IDisposable
{
    private const string ClientId = "step-up-app";

    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public RecentMfaTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    private bool _seeded;

    private async Task<(Browser Browser, CodeFlow Flow)> StartAsync()
    {
        if (!_seeded)
        {
            await _factory.SeedCodeClientAsync(ClientId, "tenants.read", "tenants.delete");
            await _factory.SeedClientAsync("signal9-api");
        }
        _seeded = true;
        return (new Browser(_factory.Auth.Server), new CodeFlow(ClientId));
    }

    private async Task<string> RedeemAsync(CodeFlow flow, Uri redirect)
    {
        using var auth = _factory.Auth.CreateClient();
        var response = await auth.PostAsync("connect/token", flow.Redemption(flow.CodeFrom(redirect)), Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("access_token").GetString()!;
    }

    /// <summary>The token's claims, as api's resource server introspects them.</summary>
    private async Task<JsonElement> IntrospectAsync(string token)
    {
        using var api = _factory.CreateAnonymousClient();
        var response = await api.PostAsync("connect/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "signal9-api",
            ["client_secret"] = Signal9ApiFactory.ClientSecret,
            ["token"] = token,
        }), Ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    private static string WithMaxAge(string authorizeRequest, int seconds) =>
        QueryHelpers.AddQueryString(authorizeRequest, "max_age", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [TestMethod]
    public async Task Access_tokens_say_when_and_how_the_person_authenticated_at_entra()
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;
        var authenticatedAt = _factory.Time.GetUtcNow().AddMinutes(-3);

        var token = await RedeemAsync(flow, await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra,
            EntraUser.WithMfa() with { AuthenticatedAt = authenticatedAt }));
        var claims = await IntrospectAsync(token);

        Assert.AreEqual(authenticatedAt.ToUnixTimeSeconds(), claims.GetProperty("auth_time").GetInt64());
        CollectionAssert.AreEqual(new[] { "pwd", "mfa" },
            claims.GetProperty("amr").EnumerateArray().Select(value => value.GetString()).ToArray());
    }

    [TestMethod]
    public async Task A_sign_in_that_does_not_say_when_the_person_authenticated_is_rejected()
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;

        var redirect = await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra,
            EntraUser.WithMfa() with { SendsAuthTime = false });

        Assert.AreEqual("access_denied", CodeFlow.ErrorFrom(redirect));
    }

    [TestMethod]
    public async Task Max_age_within_the_last_authentication_issues_a_code_without_going_back_to_entra()
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;
        await RedeemAsync(flow, await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra, EntraUser.WithMfa()));
        _factory.Time.Advance(TimeSpan.FromMinutes(14));

        var again = new CodeFlow(ClientId);
        var response = await browser.GetAsync(WithMaxAge(again.AuthorizeRequest(), 900));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.IsFalse(string.IsNullOrEmpty(again.CodeFrom(response.Headers.Location!)));
    }

    [TestMethod]
    public async Task Max_age_past_the_last_authentication_forces_a_fresh_sign_in_at_entra()
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;
        var user = EntraUser.WithMfa();
        var first = await IntrospectAsync(await RedeemAsync(flow,
            await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra, user)));
        _factory.Time.Advance(TimeSpan.FromMinutes(16));

        var stepUp = new CodeFlow(ClientId);
        var token = await RedeemAsync(stepUp,
            await browser.AuthorizeAsync(WithMaxAge(stepUp.AuthorizeRequest(), 900), _factory.Entra, user));
        var claims = await IntrospectAsync(token);

        Assert.AreEqual(2, _factory.Entra.SignIns);
        Assert.AreEqual("login", _factory.Entra.Prompts.Last());
        Assert.AreEqual(_factory.Time.GetUtcNow().ToUnixTimeSeconds(), claims.GetProperty("auth_time").GetInt64());
        Assert.IsGreaterThan(first.GetProperty("auth_time").GetInt64(), claims.GetProperty("auth_time").GetInt64());
    }

    [TestMethod]
    public async Task A_forced_sign_in_that_entra_did_not_make_fresh_is_rejected()
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;
        var user = EntraUser.WithMfa() with { AuthenticatedAt = _factory.Time.GetUtcNow() };
        await RedeemAsync(flow, await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra, user));
        _factory.Time.Advance(TimeSpan.FromMinutes(16));

        // An Entra that ignores prompt=login and answers from its old session.
        var stale = new StaleEntraBrowser(browser, _factory.Entra, user);
        var stepUp = new CodeFlow(ClientId);
        var redirect = await stale.AuthorizeAsync(WithMaxAge(stepUp.AuthorizeRequest(), 900));

        Assert.AreEqual("access_denied", CodeFlow.ErrorFrom(redirect));
    }

    private EntraUser Superadministrator(DateTimeOffset authenticatedAt) =>
        _factory.Superadministrator() with { AuthenticatedAt = authenticatedAt };

    private async Task<HttpResponseMessage> DeleteTenantAsync(string token, Guid tenantId)
    {
        using var api = _factory.CreateAnonymousClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await api.DeleteAsync($"api/v1/tenants/{tenantId}", Ct);
    }

    private const string SessionCookie = "__Host-Http-signal9";

    private async Task<long> CountDashboardSessionsAsync()
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("""SELECT count(*) FROM "DashboardSessions" """, connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private static void AssertStepUpChallenge(HttpResponseMessage response)
    {
        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.Single();
        Assert.AreEqual("Bearer", challenge.Scheme);
        StringAssert.Contains(challenge.Parameter, "error=\"insufficient_user_authentication\"");
        StringAssert.Contains(challenge.Parameter, "max_age=900");
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [TestMethod]
    public async Task Mfa_older_than_15_minutes_gets_the_step_up_challenge_and_a_fresh_sign_in_succeeds()
    {
        using var fixture = _factory.CreateClient();
        var tenant = await _factory.CreateTenantAsync(fixture, "Contoso");
        var (browser, flow) = await StartAsync();
        using var session = browser;
        var person = Superadministrator(authenticatedAt: _factory.Time.GetUtcNow().AddMinutes(-16));
        var stale = await RedeemAsync(flow, await browser.AuthorizeAsync(
            flow.AuthorizeRequest("openid tenants.read tenants.delete"), _factory.Entra, person));

        var refused = await DeleteTenantAsync(stale, tenant.Id);
        var stepUp = new CodeFlow(ClientId);
        var fresh = await RedeemAsync(stepUp, await browser.AuthorizeAsync(
            WithMaxAge(stepUp.AuthorizeRequest("openid tenants.read tenants.delete"), 900), _factory.Entra, person));
        var deleted = await DeleteTenantAsync(fresh, tenant.Id);

        AssertStepUpChallenge(refused);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Mfa_exactly_15_minutes_old_is_still_recent()
    {
        using var fixture = _factory.CreateClient();
        var tenant = await _factory.CreateTenantAsync(fixture, "Contoso");
        using var person = await _factory.CreatePersonClientAsync(
            Superadministrator(authenticatedAt: _factory.Time.GetUtcNow().AddMinutes(-15)), "tenants.delete");

        var response = await person.DeleteAsync($"api/v1/tenants/{tenant.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Recent_mfa_is_judged_on_every_request_as_the_token_ages()
    {
        using var fixture = _factory.CreateClient();
        var tenant = await _factory.CreateTenantAsync(fixture, "Contoso");
        using var person = await _factory.CreatePersonClientAsync(
            Superadministrator(authenticatedAt: _factory.Time.GetUtcNow().AddMinutes(-10)), "tenants.delete");
        _factory.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        AssertStepUpChallenge(await person.DeleteAsync($"api/v1/tenants/{tenant.Id}", Ct));
    }

    [TestMethod]
    public async Task An_application_acting_as_itself_is_refused_without_a_step_up_challenge()
    {
        // No person to send through MFA, and no MFA-approved grant yet (#63): it fails closed.
        using var application = await _factory.CreateClientAsync("tenants.delete");
        using var fixture = _factory.CreateClient();
        var tenant = await _factory.CreateTenantAsync(fixture, "Contoso");

        var response = await application.DeleteAsync($"api/v1/tenants/{tenant.Id}", Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.IsFalse(response.Headers.WwwAuthenticate.ToString().Contains("insufficient_user_authentication", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task The_api_reference_shows_which_operations_need_a_recent_mfa()
    {
        using var client = _factory.CreateAnonymousClient();
        var document = await client.GetFromJsonAsync<JsonElement>("docs/openapi/v1.json", Ct);

        var delete = document.GetProperty("paths").GetProperty("/api/v1/tenants/{tenantId}").GetProperty("delete");
        Assert.ContainsSingle(delete.GetProperty("x-signal9-requirements").EnumerateArray()
            .Where(r => r.GetProperty("name").GetString() == "recent-mfa"));
        StringAssert.Contains(delete.GetProperty("description").GetString(), "15 minutes");
        Assert.IsTrue(delete.GetProperty("responses").TryGetProperty("401", out var unauthorized));
        StringAssert.Contains(unauthorized.GetProperty("description").GetString(), "insufficient_user_authentication");
    }

    [TestMethod]
    public async Task The_dashboard_steps_up_through_web_and_then_the_action_succeeds()
    {
        using var fixture = _factory.CreateClient();
        var tenant = await _factory.CreateTenantAsync(fixture, "Contoso");
        var person = Superadministrator(authenticatedAt: _factory.Time.GetUtcNow().AddMinutes(-16));
        using var browser = new DashboardBrowser(_factory);
        Assert.AreEqual(HttpStatusCode.OK, (await browser.SignInAsync(person, "/tenants")).StatusCode);
        var sessionCookie = browser.WebCookies[SessionCookie];

        var refused = await browser.FetchAsync(HttpMethod.Delete, $"api/v1/tenants/{tenant.Id}");
        var stillSignedIn = await browser.GetAsync(OSI.Signal9.Contracts.Bff.BffRoutes.User);
        var landing = await browser.SignInAsync(person, "/tenants", maxAge: 900);
        var deleted = await browser.FetchAsync(HttpMethod.Delete, $"api/v1/tenants/{tenant.Id}");

        // web hands the dashboard api's challenge, without ending or refreshing the session.
        AssertStepUpChallenge(refused);
        Assert.AreEqual(HttpStatusCode.OK, stillSignedIn.StatusCode);
        // web asked auth for max_age=900, and auth made Entra authenticate the person again.
        Assert.AreEqual(HttpStatusCode.OK, landing.StatusCode);
        Assert.AreEqual(2, _factory.Entra.SignIns);
        Assert.AreEqual("login", _factory.Entra.Prompts.Last());
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(Ct));
        // The step-up replaced the session it came from.
        Assert.AreNotEqual(sessionCookie, browser.WebCookies[SessionCookie]);
        Assert.AreEqual(1L, await CountDashboardSessionsAsync());
    }

    /// <summary>Walks a sign-in at an Entra that ignores <c>prompt=login</c> and reports the old authentication time.</summary>
    private sealed class StaleEntraBrowser(Browser browser, FakeEntra entra, EntraUser user)
    {
        public async Task<Uri> AuthorizeAsync(string authorizeRequest)
        {
            var response = await browser.GetAsync(authorizeRequest);
            Assert.AreEqual(new Uri(FakeEntra.Instance).Host, response.Headers.Location!.Host);
            var ignored = new UriBuilder(response.Headers.Location)
            {
                Query = response.Headers.Location.Query.TrimStart('?').Replace("prompt=login", "prompt=none", StringComparison.Ordinal),
            }.Uri;
            var (callback, form) = entra.SignIn(ignored, user);
            response = await browser.PostAsync(callback, form);
            while (response.StatusCode == HttpStatusCode.Redirect && !response.Headers.Location!.IsAbsoluteUri)
                response = await browser.GetAsync(response.Headers.Location.OriginalString);
            Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
            return response.Headers.Location!;
        }
    }

    public void Dispose() => _factory.Dispose();
}
