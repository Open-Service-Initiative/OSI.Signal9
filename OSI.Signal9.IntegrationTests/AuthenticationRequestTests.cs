using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// How auth's authorization endpoint answers OpenID Connect's authentication request parameters (OpenID Connect Core
/// §3.1.2.1): <c>prompt=login</c> and <c>max_age</c> make the person authenticate at Entra again, and only an
/// authentication performed for that request satisfies them; <c>prompt=none</c> never shows anything. And a
/// Superadministrator's sign-in counts as fresh from Entra once only, however its cookie is replayed (story 59).
/// </summary>
[TestClass]
public sealed class AuthenticationRequestTests : IDisposable
{
    private const string ClientId = "authentication-request-app";
    private const string Scope = "openid tenants.read";

    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;
    private bool _seeded;

    public AuthenticationRequestTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    private async Task<Browser> BrowserAsync()
    {
        if (!_seeded)
            await _factory.SeedCodeClientAsync(ClientId, "tenants.read");
        _seeded = true;
        return new Browser(_factory.Auth.Server);
    }

    private static string With(string authorizeRequest, string name, string value) =>
        QueryHelpers.AddQueryString(authorizeRequest, name, value);

    private async Task<long> RedeemedAuthTimeAsync(CodeFlow flow, Uri redirect)
    {
        using var auth = _factory.Auth.CreateClient();
        var response = await auth.PostAsync("connect/token", flow.Redemption(flow.CodeFrom(redirect)), Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        var idToken = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id_token").GetString()!;
        var payload = JsonDocument.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(idToken.Split('.')[1]));
        return payload.RootElement.GetProperty("auth_time").GetInt64();
    }

    private static bool SentToEntra(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.Host == new Uri(FakeEntra.Instance).Host;

    [TestMethod]
    public async Task Prompt_login_makes_a_signed_in_person_authenticate_at_entra_again()
    {
        using var browser = await BrowserAsync();
        var person = EntraUser.WithMfa();
        await browser.AuthorizeAsync(new CodeFlow(ClientId).AuthorizeRequest(Scope), _factory.Entra, person);
        _factory.Time.Advance(TimeSpan.FromMinutes(1));

        var flow = new CodeFlow(ClientId);
        var redirect = await browser.AuthorizeAsync(With(flow.AuthorizeRequest(Scope), "prompt", "login"), _factory.Entra, person);

        Assert.AreEqual(2, _factory.Entra.SignIns, "The person authenticated at Entra again.");
        Assert.AreEqual("login", _factory.Entra.Prompts.Last());
        Assert.AreEqual(_factory.Time.GetUtcNow().ToUnixTimeSeconds(), await RedeemedAuthTimeAsync(flow, redirect));
    }

    [TestMethod]
    public async Task Prompt_login_is_refused_when_entra_does_not_authenticate_the_person_again()
    {
        using var browser = await BrowserAsync();
        var person = EntraUser.WithMfa() with { AuthenticatedAt = _factory.Time.GetUtcNow() };
        await browser.AuthorizeAsync(new CodeFlow(ClientId).AuthorizeRequest(Scope), _factory.Entra, person);
        _factory.Time.Advance(TimeSpan.FromMinutes(5));

        // An Entra that ignores prompt=login and answers from its old session.
        var flow = new CodeFlow(ClientId);
        var toEntra = await browser.GetAsync(With(flow.AuthorizeRequest(Scope), "prompt", "login"));
        Assert.IsTrue(SentToEntra(toEntra));
        var ignored = new UriBuilder(toEntra.Headers.Location!)
        {
            Query = toEntra.Headers.Location!.Query.TrimStart('?').Replace("prompt=login", "prompt=none", StringComparison.Ordinal),
        }.Uri;
        var (callback, form) = _factory.Entra.SignIn(ignored, person);
        var response = await browser.PostAsync(callback, form);
        while (response.StatusCode == HttpStatusCode.Redirect && !response.Headers.Location!.IsAbsoluteUri)
            response = await browser.GetAsync(response.Headers.Location.OriginalString);

        Assert.AreEqual("access_denied", CodeFlow.ErrorFrom(response.Headers.Location!));
    }

    [TestMethod]
    public async Task Max_age_zero_accepts_the_authentication_it_just_asked_entra_for()
    {
        using var browser = await BrowserAsync();
        var person = EntraUser.WithMfa();
        await browser.AuthorizeAsync(new CodeFlow(ClientId).AuthorizeRequest(Scope), _factory.Entra, person);
        _factory.Time.Advance(TimeSpan.FromSeconds(5));

        var flow = new CodeFlow(ClientId);
        var toEntra = await browser.GetAsync(flow.AuthorizeRequest(Scope, maxAge: 0));
        Assert.IsTrue(SentToEntra(toEntra));
        var (callback, form) = _factory.Entra.SignIn(toEntra.Headers.Location!, person);
        // auth_time has whole seconds, and the round trip through Entra takes time.
        _factory.Time.Advance(TimeSpan.FromSeconds(2));
        var response = await browser.PostAsync(callback, form);
        while (response.StatusCode == HttpStatusCode.Redirect && !response.Headers.Location!.IsAbsoluteUri)
            response = await browser.GetAsync(response.Headers.Location.OriginalString);

        Assert.IsFalse(string.IsNullOrEmpty(flow.CodeFrom(response.Headers.Location!)));
    }

    [TestMethod]
    public async Task Prompt_none_without_a_session_answers_login_required_without_going_to_entra()
    {
        using var browser = await BrowserAsync();

        var response = await browser.GetAsync(With(new CodeFlow(ClientId).AuthorizeRequest(Scope), "prompt", "none"));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.AreEqual("login_required", CodeFlow.ErrorFrom(response.Headers.Location!));
        Assert.AreEqual(0, _factory.Entra.SignIns);
    }

    [TestMethod]
    public async Task Prompt_none_answers_login_required_when_max_age_needs_a_new_authentication()
    {
        using var browser = await BrowserAsync();
        await browser.AuthorizeAsync(new CodeFlow(ClientId).AuthorizeRequest(Scope), _factory.Entra, EntraUser.WithMfa());
        _factory.Time.Advance(TimeSpan.FromMinutes(16));

        var response = await browser.GetAsync(With(new CodeFlow(ClientId).AuthorizeRequest(Scope, maxAge: 900), "prompt", "none"));

        Assert.AreEqual("login_required", CodeFlow.ErrorFrom(response.Headers.Location!));
        Assert.AreEqual(1, _factory.Entra.SignIns);
    }

    [TestMethod]
    public async Task Prompt_none_with_a_session_issues_a_code_silently()
    {
        using var browser = await BrowserAsync();
        await browser.AuthorizeAsync(new CodeFlow(ClientId).AuthorizeRequest(Scope), _factory.Entra, EntraUser.WithMfa());

        var flow = new CodeFlow(ClientId);
        var response = await browser.GetAsync(With(flow.AuthorizeRequest(Scope), "prompt", "none"));

        Assert.IsFalse(string.IsNullOrEmpty(flow.CodeFrom(response.Headers.Location!)));
        Assert.AreEqual(1, _factory.Entra.SignIns);
    }

    [TestMethod]
    public async Task Prompt_none_answers_login_required_when_a_superadministrator_must_be_re_read_from_entra()
    {
        using var browser = await BrowserAsync();
        await browser.AuthorizeAsync(new CodeFlow(ClientId).AuthorizeRequest(Scope), _factory.Entra, _factory.Superadministrator());

        var response = await browser.GetAsync(With(new CodeFlow(ClientId).AuthorizeRequest(Scope), "prompt", "none"));

        Assert.AreEqual("login_required", CodeFlow.ErrorFrom(response.Headers.Location!));
    }

    [TestMethod]
    public async Task Replaying_a_sign_in_cookie_from_before_its_code_was_issued_does_not_skip_entra()
    {
        using var browser = await BrowserAsync();
        var flow = new CodeFlow(ClientId);
        var toEntra = await browser.GetAsync(flow.AuthorizeRequest(Scope));
        var (callback, form) = _factory.Entra.SignIn(toEntra.Headers.Location!, _factory.Superadministrator());
        var back = await browser.PostAsync(callback, form);
        Assert.AreEqual(HttpStatusCode.Redirect, back.StatusCode);
        // The cookie straight from Entra's sign-in, before auth issues anything from it.
        var fromEntra = browser.SaveCookies();
        var issued = await browser.GetAsync(back.Headers.Location!.OriginalString);
        Assert.IsFalse(string.IsNullOrEmpty(flow.CodeFrom(issued.Headers.Location!)));

        browser.RestoreCookies(fromEntra);
        var replayed = await browser.GetAsync(new CodeFlow(ClientId).AuthorizeRequest(Scope));

        Assert.IsTrue(SentToEntra(replayed), "A Superadministrator goes back to Entra for every code.");
    }

    public void Dispose() => _factory.Dispose();
}
