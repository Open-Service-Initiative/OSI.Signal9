using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// The dashboard's backend-for-frontend (ADR-0003, #112): a person signs in through <c>web</c> → <c>auth</c> → Entra,
/// and web calls <c>api</c> for them with certificate-bound tokens the browser never sees.
/// </summary>
[TestClass]
public sealed partial class BffTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public BffTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    private async Task<DashboardBrowser> SignedInAsync(EntraUser? user = null)
    {
        var browser = new DashboardBrowser(_factory);
        var landing = await browser.SignInAsync(user ?? EntraUser.WithMfa());
        Assert.AreEqual(HttpStatusCode.OK, landing.StatusCode, await landing.Content.ReadAsStringAsync(Ct));
        return browser;
    }

    private static StringContent TenantJson(string name) => new(JsonSerializer.Serialize(
        new CreateTenantRequest { Name = name, ContactEmail = "it@example.test", MaxDevices = 5 }, Signal9Json.Options),
        Encoding.UTF8, "application/json");

    [TestMethod]
    public async Task A_person_signs_in_through_web_auth_and_entra_and_uses_the_api_through_web()
    {
        var user = EntraUser.WithMfa();
        using var browser = await SignedInAsync(user);

        var me = await browser.GetAsync(BffRoutes.User);
        var tenants = await browser.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
        Assert.AreEqual($"{user.TenantId:D}:{user.ObjectId:D}", (await me.Content.ReadFromJsonAsync<BffUser>(Ct))!.Subject);
        Assert.AreEqual(HttpStatusCode.OK, tenants.StatusCode, await tenants.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task The_sign_in_returns_to_the_page_the_person_asked_for()
    {
        using var browser = new DashboardBrowser(_factory);

        await browser.SignInAsync(EntraUser.WithMfa(), returnUrl: "/devices?status=Online");

        Assert.AreEqual(new Uri(Signal9WebFactory.BaseUri, "/devices?status=Online"), browser.Received[^1].Uri);
    }

    [TestMethod]
    [DataRow("https://evil.example/")]
    [DataRow("//evil.example/")]
    [DataRow("/\\evil.example/")]
    public async Task Sign_in_never_returns_to_another_site(string returnUrl)
    {
        using var browser = new DashboardBrowser(_factory);

        var response = await browser.GetAsync($"{BffRoutes.Login}?returnUrl={Uri.EscapeDataString(returnUrl)}");

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [TestMethod]
    public async Task The_browser_never_receives_a_token()
    {
        using var browser = await SignedInAsync();
        await browser.GetAsync("api/v1/tenants");
        await browser.GetAsync(BffRoutes.User);

        // OpenIddict's access and identity tokens are compact JWS or JWE: a base64url JSON header, then dot-separated parts.
        foreach (var (uri, headers, body) in browser.Received)
        {
            Assert.IsFalse(CompactJwtPattern().IsMatch(headers), $"A token reached the browser from {uri}.");
            Assert.IsFalse(CompactJwtPattern().IsMatch(body), $"A token reached the browser from {uri}.");
            Assert.IsFalse(TokenFieldPattern().IsMatch(body), $"A token field reached the browser from {uri}.");
        }
    }

    [GeneratedRegex(@"eyJ[\w-]+\.[\w-]*\.")]
    private static partial Regex CompactJwtPattern();

    [GeneratedRegex("\"(access|refresh|id)_token\"")]
    private static partial Regex TokenFieldPattern();

    [TestMethod]
    public async Task The_session_cookie_is_host_locked_http_only_and_strict()
    {
        using var browser = await SignedInAsync();

        var session = browser.WebSetCookies.Where(c => c.StartsWith("__Host-Http-", StringComparison.Ordinal)).ToList();

        Assert.HasCount(1, session, string.Join(Environment.NewLine, browser.WebSetCookies));
        var attributes = session[0].Split(';').Skip(1).Select(a => a.Trim().ToLowerInvariant()).ToList();
        CollectionAssert.Contains(attributes, "secure");
        CollectionAssert.Contains(attributes, "httponly");
        CollectionAssert.Contains(attributes, "samesite=strict");
        CollectionAssert.Contains(attributes, "path=/");
        Assert.IsFalse(attributes.Any(a => a.StartsWith("domain", StringComparison.Ordinal)), session[0]);
        // Only the session cookie is left: the sign-in's correlation and nonce cookies are gone.
        Assert.AreEqual(1, browser.WebCookies.Count, string.Join(", ", browser.WebCookies.Keys));
    }

    [TestMethod]
    public async Task Web_calls_api_with_a_token_bound_to_its_own_certificate()
    {
        using var browser = await SignedInAsync();
        await browser.GetAsync("api/v1/tenants");
        var hop = _factory.Web.ApiRequests.Last(r => r.Uri.AbsolutePath == "/api/v1/tenants");

        // A token taken from web's hop to api is useless without web's certificate (RFC 8705 §3).
        using var withoutCertificate = _factory.CreateForwardedCertificateClient(null);
        using var withAnotherCertificate = _factory.CreateForwardedCertificateClient(TestCertificates.SelfSigned("thief"));
        foreach (var api in new[] { withoutCertificate, withAnotherCertificate })
        {
            api.DefaultRequestHeaders.Authorization = AuthenticationHeaderValue.Parse(hop.Authorization!);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await api.GetAsync("api/v1/tenants", Ct)).StatusCode);
        }
    }

    [TestMethod]
    public async Task Web_redeems_its_code_at_apis_mtls_alias_with_its_certificate()
    {
        using var browser = await SignedInAsync();

        var redemption = _factory.Web.ApiRequests.Single(r => r.Uri.AbsolutePath == "/connect/token");

        Assert.AreEqual(new Uri(Signal9AuthFactory.ApiUri).Host, redemption.Uri.Host);
        Assert.AreEqual(HttpMethod.Post, redemption.Method);
    }

    [TestMethod]
    public async Task Web_forwards_neither_the_browsers_cookies_nor_its_authorization_header()
    {
        using var browser = await SignedInAsync();

        await browser.FetchAsync(HttpMethod.Get, "api/v1/tenants",
            headers: h => h.Authorization = new AuthenticationHeaderValue("Bearer", "forged"));

        var hop = _factory.Web.ApiRequests.Last(r => r.Uri.AbsolutePath == "/api/v1/tenants");
        Assert.IsFalse(hop.Headers.ContainsKey("Cookie"), "web forwarded the browser's cookies to api.");
        Assert.AreNotEqual("Bearer forged", hop.Authorization);
        Assert.StartsWith("Bearer ", hop.Authorization!);
    }

    [TestMethod]
    public async Task State_changing_requests_need_the_csrf_header()
    {
        // Only a Superadministrator creates root tenants (#113).
        using var browser = await SignedInAsync(EntraUser.WithMfa() with
        {
            TenantId = _factory.Entra.DeployingTenantId,
            Roles = ["Superadministrator"],
        });

        var without = await browser.FetchAsync(HttpMethod.Post, "api/v1/tenants", TenantJson("Contoso"), csrfHeader: false);
        var withWrongValue = await browser.FetchAsync(HttpMethod.Post, "api/v1/tenants", TenantJson("Contoso"),
            csrfHeader: false, headers: h => h.Add(BffRoutes.CsrfHeader, "0"));
        var with = await browser.FetchAsync(HttpMethod.Post, "api/v1/tenants", TenantJson("Contoso"));
        var signOutWithout = await browser.FetchAsync(HttpMethod.Post, BffRoutes.Logout, csrfHeader: false);

        Assert.AreEqual(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.AreEqual("application/problem+json", without.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Forbidden, withWrongValue.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, with.StatusCode, await with.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(HttpStatusCode.Forbidden, signOutWithout.StatusCode);
        Assert.AreEqual(1, _factory.Web.ApiRequests.Count(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/v1/tenants"),
            "A request without the CSRF header reached api.");
    }

    [TestMethod]
    public async Task Without_a_session_web_answers_401_and_calls_nothing()
    {
        using var browser = new DashboardBrowser(_factory);

        var user = await browser.GetAsync(BffRoutes.User);
        var tenants = await browser.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.Unauthorized, user.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, tenants.StatusCode);
        Assert.IsEmpty(_factory.Web.ApiRequests);
    }

    [TestMethod]
    [DataRow("connect/token")]
    [DataRow("connect/introspect")]
    [DataRow("openapi/v1.json")]
    [DataRow("hubs/agents/negotiate")]
    [DataRow("api2/v1/tenants")]
    [DataRow("health")]
    public async Task Web_forwards_only_its_allow_list_to_api(string path)
    {
        using var browser = await SignedInAsync();
        var before = _factory.Web.ApiRequests.Count;

        var get = await browser.FetchAsync(HttpMethod.Get, path);
        var post = await browser.FetchAsync(HttpMethod.Post, path, new StringContent(""));

        Assert.AreEqual(HttpStatusCode.NotFound, get.StatusCode);
        Assert.IsTrue(post.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, post.StatusCode.ToString());
        Assert.AreEqual(before, _factory.Web.ApiRequests.Count, "web forwarded a path outside its allow-list.");
    }

    [TestMethod]
    [DataRow("api/v1/tenants")]
    [DataRow("hubs/dashboard/negotiate?negotiateVersion=1")]
    public async Task Web_forwards_the_allow_listed_paths_to_api(string path)
    {
        using var browser = await SignedInAsync();

        var response = await browser.FetchAsync(path.StartsWith("hubs", StringComparison.Ordinal) ? HttpMethod.Post : HttpMethod.Get, path);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual("/" + path.Split('?')[0], _factory.Web.ApiRequests.Last().Uri.AbsolutePath);
    }

    [TestMethod]
    public async Task Docs_are_forwarded_to_api_without_a_session()
    {
        using var browser = new DashboardBrowser(_factory);

        await browser.GetAsync("docs/openapi/v1.json");

        var hop = _factory.Web.ApiRequests.Single();
        Assert.AreEqual("/docs/openapi/v1.json", hop.Uri.AbsolutePath);
        Assert.IsNull(hop.Authorization);
    }

    [TestMethod]
    public async Task There_is_no_cors_on_web_or_api()
    {
        using var browser = await SignedInAsync();
        using var api = _factory.CreateAnonymousClient();

        foreach (var (send, name) in new (Func<HttpRequestMessage, Task<HttpResponseMessage>>, string)[]
                 {
                     (r => browser.FetchAsync(r.Method, r.RequestUri!.ToString().TrimStart('/'), headers: h => Copy(r, h)), "web"),
                     (r => api.SendAsync(r, Ct), "api"),
                 })
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/tenants");
            preflight.Headers.Add("Origin", "https://evil.example");
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", BffRoutes.CsrfHeader);

            var response = await send(preflight);

            Assert.IsFalse(response.Headers.Any(h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase)),
                $"{name} answered a CORS preflight: {response.Headers}");
        }

        static void Copy(HttpRequestMessage from, HttpRequestHeaders to)
        {
            foreach (var header in from.Headers)
                to.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    [TestMethod]
    public async Task Web_sends_a_strict_content_security_policy_that_allows_its_import_map()
    {
        using var browser = new DashboardBrowser(_factory);

        var page = await browser.GetAsync("");
        var html = await page.Content.ReadAsStringAsync(Ct);

        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, html);
        var policy = page.Headers.GetValues("Content-Security-Policy").Single();
        var directives = policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToDictionary(d => d.Split(' ')[0], d => d.Split(' ').Skip(1).ToArray());
        var importMap = ImportMapPattern().Match(html);
        Assert.IsTrue(importMap.Success, "The page has no import map.");
        var hash = $"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(importMap.Groups[1].Value)))}'";

        CollectionAssert.AreEquivalent(new[] { "'self'", "'wasm-unsafe-eval'", hash }, directives["script-src"]);
        CollectionAssert.AreEquivalent(new[] { "'self'" }, directives["default-src"]);
        CollectionAssert.AreEquivalent(new[] { "'self'" }, directives["style-src-elem"]);
        CollectionAssert.AreEquivalent(new[] { "'unsafe-inline'" }, directives["style-src-attr"]);
        CollectionAssert.AreEquivalent(new[] { "'self'" }, directives["connect-src"]);
        CollectionAssert.AreEquivalent(new[] { "'none'" }, directives["object-src"]);
        CollectionAssert.AreEquivalent(new[] { "'none'" }, directives["frame-ancestors"]);
        CollectionAssert.AreEquivalent(new[] { "'self'" }, directives["base-uri"]);
        // Nothing but the import map's hash, 'wasm-unsafe-eval' and the inline style attributes Fluent needs is relaxed,
        // and no other origin is named.
        var sources = directives.Values.SelectMany(v => v).ToList();
        Assert.IsFalse(sources.Any(s => s is "'unsafe-eval'" or "*" || s.Contains("://", StringComparison.Ordinal)
            || s.EndsWith(':') && s != "data:"), policy);
        Assert.AreEqual(1, sources.Count(s => s == "'unsafe-inline'"), policy);
        Assert.DoesNotContain("<script>", html, "An inline script other than the import map needs its own hash.");
    }

    [GeneratedRegex("<script type=\"importmap\">(.*?)</script>", RegexOptions.Singleline)]
    private static partial Regex ImportMapPattern();

    [TestMethod]
    [DataRow("")]
    [DataRow("devices")]
    [DataRow("tenants")]
    public async Task The_dashboard_pages_load_without_a_session(string path)
    {
        using var browser = new DashboardBrowser(_factory);

        var page = await browser.GetAsync(path);

        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("_framework/blazor.web.js", await page.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task A_session_works_on_another_web_replica_sharing_the_database()
    {
        using var browser = await SignedInAsync();
        await using var replica = _factory.CreateWebReplica();

        browser.UseWebReplica(replica);
        var me = await browser.GetAsync(BffRoutes.User);
        var tenants = await browser.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, tenants.StatusCode, await tenants.Content.ReadAsStringAsync(Ct));
        Assert.IsTrue(replica.ApiRequests.Any(r => r.Uri.AbsolutePath == "/api/v1/tenants"));
    }

    [TestMethod]
    public async Task Signing_out_ends_the_session_even_for_a_replayed_cookie()
    {
        using var browser = await SignedInAsync();
        var captured = browser.WebCookies.ToDictionary();

        var signOut = await browser.FetchAsync(HttpMethod.Post, BffRoutes.Logout);
        var afterwards = await browser.GetAsync(BffRoutes.User);
        browser.SetWebCookies(captured);
        var replayed = await browser.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.NoContent, signOut.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, afterwards.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, replayed.StatusCode);
    }

    [TestMethod]
    public async Task Web_rotates_its_certificate_and_keeps_working()
    {
        using (var first = await SignedInAsync())
            Assert.AreEqual(HttpStatusCode.OK, (await first.GetAsync("api/v1/tenants")).StatusCode);
        var before = _factory.Web.ApiRequests.Last().CertificateThumbprint;

        // The next certificate is published 14 days early and takes over when the first one's 90 days are up.
        _factory.Time.Advance(TimeSpan.FromDays(77));
        using (var during = await SignedInAsync())
            Assert.AreEqual(HttpStatusCode.OK, (await during.GetAsync("api/v1/tenants")).StatusCode);
        Assert.AreEqual(before, _factory.Web.ApiRequests.Last().CertificateThumbprint, "Switched before it activated.");

        _factory.Time.Advance(TimeSpan.FromDays(14));
        using var after = await SignedInAsync();
        var tenants = await after.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.OK, tenants.StatusCode, await tenants.Content.ReadAsStringAsync(Ct));
        Assert.AreNotEqual(before, _factory.Web.ApiRequests.Last().CertificateThumbprint);
    }

    public void Dispose() => _factory.Dispose();
}
