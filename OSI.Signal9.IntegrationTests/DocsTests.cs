using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// The public API reference (#135): web serves Scalar at <c>/docs</c> over the OpenAPI document api generates, to
/// anyone (story 132), and "try it" goes through the person's own session (story 133).
/// </summary>
[TestClass]
public sealed partial class DocsTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public DocsTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Docs_serve_scalar_without_signing_in()
    {
        using var browser = new DashboardBrowser(_factory);

        var redirect = await browser.GetAsync("docs");
        var response = await browser.GetAsync("docs/");
        var html = await response.Content.ReadAsStringAsync(Ct);

        Assert.AreEqual(new Uri(Signal9WebFactory.BaseUri, "docs/"), new Uri(new Uri(Signal9WebFactory.BaseUri, "docs"), redirect.Headers.Location!));
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, html);
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(html, "initialize(");
        Assert.IsEmpty(_factory.Web.ApiRequests, "The page itself is web's; only the document comes from api.");
    }

    [TestMethod]
    public async Task The_page_runs_only_its_own_nonced_scripts_and_styles_from_web()
    {
        using var browser = new DashboardBrowser(_factory);

        var first = await PageAsync(browser);
        var second = await PageAsync(browser);

        foreach (var (html, csp) in new[] { first, second })
        {
            var nonce = NonceIn(csp);
            // Every script and the injected stylesheet carry this response's nonce (the stylesheet reads the meta).
            var scripts = ScriptTag().Matches(html);
            Assert.IsNotEmpty(scripts);
            foreach (Match script in scripts)
                StringAssert.Contains(script.Value, $"nonce=\"{nonce}\"");
            Assert.AreEqual(1, Regex.Count(html, "property=\"csp-nonce\""), html);
            StringAssert.Contains(html, $"<meta property=\"csp-nonce\" content=\"{nonce}\">");
            StringAssert.Contains(csp, $"style-src-elem 'self' 'nonce-{nonce}'");
            // Nothing from another origin: no CDN bundle, font CDN, telemetry, AI agent or request proxy.
            Assert.IsFalse(Regex.IsMatch(html, "(src|href)=\"(https?:)?//"), html);
            foreach (var host in new[] { "cdn.jsdelivr.net", "fonts.scalar.com", "proxy.scalar.com", "api.scalar.com" })
                Assert.DoesNotContain(host, html);
            StringAssert.Contains(csp, "connect-src 'self'");
        }
        Assert.AreNotEqual(NonceIn(first.Csp), NonceIn(second.Csp), "The nonce must be fresh on every response.");
    }

    [TestMethod]
    public async Task The_page_loads_apis_document_through_web_and_tries_calls_against_web()
    {
        using var browser = new DashboardBrowser(_factory);
        var html = await (await browser.GetAsync("docs/")).Content.ReadAsStringAsync(Ct);

        var response = await browser.GetAsync("docs/openapi/v1.json");
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        StringAssert.Contains(html, "\"url\":\"docs/openapi/v1.json\"");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(document.GetProperty("paths").TryGetProperty("/api/v1/tenants", out _));
        // A relative server: "try it" calls whichever host served the document, so on web they use the session.
        Assert.AreEqual("/", document.GetProperty("servers").EnumerateArray().Single().GetProperty("url").GetString());
        Assert.IsNull(_factory.Web.ApiRequests.Single().Authorization);
    }

    [TestMethod]
    [DataRow("docs/scalar.js")]
    [DataRow("docs/scalar.aspnetcore.js")]
    [DataRow("docs/scalar-config.js")]
    public async Task The_pages_scripts_are_served_by_web(string path)
    {
        using var browser = new DashboardBrowser(_factory);

        var response = await browser.GetAsync(path);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.IsEmpty(_factory.Web.ApiRequests);
    }

    [TestMethod]
    public async Task Try_it_sends_webs_csrf_header()
    {
        using var browser = new DashboardBrowser(_factory);
        var html = await (await browser.GetAsync("docs/")).Content.ReadAsStringAsync(Ct);

        var module = await (await browser.GetAsync("docs/scalar-config.js")).Content.ReadAsStringAsync(Ct);

        StringAssert.Contains(html, "'/docs/scalar-config.js'");
        StringAssert.Contains(module, $"request.headers.set('{BffRoutes.CsrfHeader}', '{BffRoutes.CsrfHeaderValue}')");
    }

    [TestMethod]
    public async Task Try_it_acts_as_the_signed_in_person_and_no_further()
    {
        using var superadministratorClient = _factory.CreateSuperadministratorClient();
        var tenant = await _factory.CreateTenantAsync(superadministratorClient, "Contoso");
        using var admin = await _factory.CreateApplicationClientAsync(tenant.Id, Scopes.Tenants.Read);
        using var person = new DashboardBrowser(_factory);
        await person.SignInAsync(EntraUser.WithMfa());
        using var superadministrator = new DashboardBrowser(_factory);
        await superadministrator.SignInAsync(
            EntraUser.WithMfa() with { TenantId = _factory.Entra.DeployingTenantId, Roles = ["Superadministrator"] });

        // As Scalar sends it from /docs: same origin, with the session cookie, web's CSRF header and whatever token
        // someone typed into its authentication panel, here one that could read the tenant.
        Task<HttpResponseMessage> TryIt(DashboardBrowser browser) => browser.FetchAsync(HttpMethod.Get,
            $"api/v1/tenants/{tenant.Id}", headers: h => h.Authorization = admin.DefaultRequestHeaders.Authorization);

        var beyondAccess = await TryIt(person);
        var personsToken = _factory.Web.ApiRequests.Last().Authorization;
        var withinAccess = await TryIt(superadministrator);

        Assert.AreEqual(HttpStatusCode.NotFound, beyondAccess.StatusCode);
        Assert.AreNotEqual(admin.DefaultRequestHeaders.Authorization!.ToString(), personsToken);
        Assert.AreEqual(HttpStatusCode.OK, withinAccess.StatusCode);
    }

    [TestMethod]
    public async Task Try_it_without_signing_in_reaches_nothing()
    {
        var superadministrators = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await _factory.SuperadministratorTokenAsync());
        using var browser = new DashboardBrowser(_factory);

        var response = await browser.FetchAsync(HttpMethod.Get, "api/v1/tenants",
            headers: h => h.Authorization = superadministrators);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.IsEmpty(_factory.Web.ApiRequests);
    }

    private async Task<(string Html, string Csp)> PageAsync(DashboardBrowser browser)
    {
        var response = await browser.GetAsync("docs/");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadAsStringAsync(Ct), response.Headers.GetValues("Content-Security-Policy").Single());
    }

    private static string NonceIn(string csp)
    {
        var match = ScriptNonce().Match(csp);
        Assert.IsTrue(match.Success, csp);
        return match.Groups[1].Value;
    }

    [GeneratedRegex("<script[^>]*>")]
    private static partial Regex ScriptTag();

    [GeneratedRegex("script-src [^;]*'nonce-([A-Za-z0-9+/=_-]+)'")]
    private static partial Regex ScriptNonce();

    public void Dispose() => _factory.Dispose();
}
