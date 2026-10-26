using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.Web.Bff;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Dashboard sessions stay alive through transient failures and end on schedule (#118, stories 48-50, 54): refresh
/// tokens rotate with a 30-second reuse leeway, web's last 12 hours with no sliding, and web refreshes once per session
/// across replicas, signing the person out only on <c>invalid_grant</c>.
/// </summary>
[TestClass]
public sealed class SessionRefreshTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public SessionRefreshTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    private async Task<DashboardBrowser> SignedInAsync(EntraUser? user = null)
    {
        var browser = new DashboardBrowser(_factory);
        // A person holding a role assignment, so the session survives its refreshes (#119).
        var landing = await browser.SignInAsync(user ?? (await _factory.AssignedPersonAsync()).User);
        Assert.AreEqual(HttpStatusCode.OK, landing.StatusCode, await landing.Content.ReadAsStringAsync(Ct));
        return browser;
    }

    /// <summary>
    /// Redeems a refresh token at api's mTLS token alias as web does, with web's certificate, and returns the status and
    /// the response's fields.
    /// </summary>
    private async Task<(HttpStatusCode Status, Dictionary<string, JsonElement> Body)> RedeemAsWebAsync(string refreshToken)
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
        return (response.StatusCode, body);
    }

    private static string Error(Dictionary<string, JsonElement> body) =>
        body.TryGetValue("error", out var error) ? error.GetString()! : "(none)";

    [TestMethod]
    public async Task Web_gets_an_opaque_refresh_token_that_rotates_on_every_use()
    {
        using var browser = await SignedInAsync();
        var first = _factory.Web.IssuedRefreshTokens.Single();

        var (status, body) = await RedeemAsWebAsync(first);

        Assert.AreEqual(HttpStatusCode.OK, status, Error(body));
        // A reference token: an opaque handle, not a JWT or JWE (which have dot-separated parts).
        Assert.DoesNotContain(".", first);
        var second = body["refresh_token"].GetString();
        Assert.IsFalse(string.IsNullOrEmpty(second));
        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public async Task A_rotated_refresh_token_still_works_for_30_seconds()
    {
        using var browser = await SignedInAsync();
        var first = _factory.Web.IssuedRefreshTokens.Single();
        await RedeemAsWebAsync(first);

        _factory.Time.Advance(TimeSpan.FromSeconds(29));
        var (status, body) = await RedeemAsWebAsync(first);

        Assert.AreEqual(HttpStatusCode.OK, status, Error(body));
    }

    [TestMethod]
    public async Task Reusing_a_rotated_refresh_token_after_the_leeway_revokes_the_chain()
    {
        using var browser = await SignedInAsync();
        var first = _factory.Web.IssuedRefreshTokens.Single();
        var (_, rotated) = await RedeemAsWebAsync(first);
        var second = rotated["refresh_token"].GetString()!;

        _factory.Time.Advance(TimeSpan.FromSeconds(31));
        var (reuse, reuseBody) = await RedeemAsWebAsync(first);
        var (latest, latestBody) = await RedeemAsWebAsync(second);

        Assert.AreEqual(HttpStatusCode.BadRequest, reuse);
        Assert.AreEqual("invalid_grant", Error(reuseBody));
        Assert.AreEqual(HttpStatusCode.BadRequest, latest, "The newest token in the chain survived its reuse.");
        Assert.AreEqual("invalid_grant", Error(latestBody));
    }

    [TestMethod]
    public async Task Webs_refresh_tokens_last_12_hours_without_sliding()
    {
        using var browser = await SignedInAsync();
        var token = _factory.Web.IssuedRefreshTokens.Single();

        // Rotating every 50 minutes never extends the 12 hours. The person stays active, so web keeps the session.
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(11.5); elapsed += TimeSpan.FromMinutes(50))
        {
            _factory.Time.Advance(TimeSpan.FromMinutes(50));
            await browser.GetAsync(BffRoutes.User);
            var (status, body) = await RedeemAsWebAsync(token);
            Assert.AreEqual(HttpStatusCode.OK, status, $"{Error(body)} after {elapsed}");
            token = body["refresh_token"].GetString()!;
        }
        _factory.Time.Advance(TimeSpan.FromMinutes(50));
        var (expired, expiredBody) = await RedeemAsWebAsync(token);

        Assert.AreEqual(HttpStatusCode.BadRequest, expired);
        Assert.AreEqual("invalid_grant", Error(expiredBody));
    }

    private string LastBearer(Signal9WebFactory? web = null) =>
        (web ?? _factory.Web).ApiRequests.Last(r => r.Uri.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)).Authorization!;

    [TestMethod]
    public async Task Web_refreshes_the_access_token_when_it_is_about_to_expire()
    {
        using var browser = await SignedInAsync();
        await browser.GetAsync("api/v1/tenants");
        var first = LastBearer();

        _factory.Time.Advance(TimeSpan.FromMinutes(5));
        await browser.GetAsync("api/v1/tenants");
        Assert.AreEqual(0, _factory.Web.Refreshes, "web refreshed a token with minutes left.");
        // Access tokens last 10 minutes.
        _factory.Time.Advance(TimeSpan.FromMinutes(4.5));
        var tenants = await browser.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.OK, tenants.StatusCode, await tenants.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(1, _factory.Web.Refreshes);
        Assert.AreNotEqual(first, LastBearer());
    }

    [TestMethod]
    public async Task Web_keeps_the_session_going_with_refresh_tokens()
    {
        using var browser = await SignedInAsync();

        for (var i = 0; i < 5; i++)
        {
            _factory.Time.Advance(TimeSpan.FromMinutes(20));
            var tenants = await browser.GetAsync("api/v1/tenants");
            Assert.AreEqual(HttpStatusCode.OK, tenants.StatusCode, await tenants.Content.ReadAsStringAsync(Ct));
        }

        Assert.AreEqual(5, _factory.Web.Refreshes);
    }

    /// <summary>Another tab or replica's view of the same browser session.</summary>
    private DashboardBrowser SameSession(DashboardBrowser browser, Signal9WebFactory web)
    {
        var copy = new DashboardBrowser(_factory, web);
        copy.SetWebCookies(browser.WebCookies);
        return copy;
    }

    [TestMethod]
    public async Task Concurrent_requests_across_two_web_replicas_refresh_once()
    {
        using var browser = await SignedInAsync();
        await using var replica = _factory.CreateWebReplica();
        _factory.Time.Advance(TimeSpan.FromMinutes(11));

        var tabs = Enumerable.Range(0, 8).Select(i => SameSession(browser, i % 2 == 0 ? _factory.Web : replica)).ToList();
        var responses = await Task.WhenAll(tabs.Select(tab => tab.GetAsync("api/v1/tenants")));

        foreach (var response in responses)
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(1, _factory.Web.Refreshes + replica.Refreshes);
        tabs.ForEach(tab => tab.Dispose());
    }

    public static IEnumerable<object[]> TransientFailures =>
    [
        ["network", (Func<HttpRequestMessage, HttpResponseMessage?>)(_ => throw new HttpRequestException("Connection refused"))],
        ["server error", (Func<HttpRequestMessage, HttpResponseMessage?>)(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = JsonContent.Create(new { error = "server_error", error_description = "Entra is unavailable." }),
        })],
        ["gateway page", (Func<HttpRequestMessage, HttpResponseMessage?>)(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>Bad gateway</html>", Encoding.UTF8, "text/html"),
        })],
    ];

    [TestMethod]
    [DynamicData(nameof(TransientFailures))]
    public async Task A_transient_failure_to_refresh_never_signs_the_person_out(string name,
        Func<HttpRequestMessage, HttpResponseMessage?> failure)
    {
        using var browser = await SignedInAsync();
        _factory.Time.Advance(TimeSpan.FromMinutes(11));
        _factory.Web.Fault = request => request.RequestUri!.AbsolutePath == "/connect/token" ? failure(request) : null;

        var during = await browser.GetAsync("api/v1/tenants");
        var me = await browser.GetAsync(BffRoutes.User);
        _factory.Web.Fault = null;
        var after = await browser.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, during.StatusCode, name);
        Assert.AreEqual("application/problem+json", during.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.OK, me.StatusCode, $"{name} signed the person out.");
        Assert.AreEqual(HttpStatusCode.OK, after.StatusCode, await after.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task A_failure_to_refresh_early_keeps_using_the_current_token()
    {
        using var browser = await SignedInAsync();
        _factory.Time.Advance(TimeSpan.FromMinutes(9.5));
        _factory.Web.Fault = request => request.RequestUri!.AbsolutePath == "/connect/token"
            ? throw new HttpRequestException("Connection refused") : null;

        var tenants = await browser.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.OK, tenants.StatusCode, await tenants.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Invalid_grant_signs_the_person_out()
    {
        using var browser = await SignedInAsync();
        // Someone replays web's first refresh token after it rotated: auth revokes the chain.
        var first = _factory.Web.IssuedRefreshTokens.Single();
        await RedeemAsWebAsync(first);
        _factory.Time.Advance(TimeSpan.FromSeconds(31));
        await RedeemAsWebAsync(first);
        _factory.Time.Advance(TimeSpan.FromMinutes(10));

        var tenants = await browser.GetAsync("api/v1/tenants");
        var me = await browser.GetAsync(BffRoutes.User);

        Assert.AreEqual(HttpStatusCode.Unauthorized, tenants.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.IsEmpty(browser.WebCookies, "web left the ended session's cookie in the browser.");
    }

    private static Func<HttpRequestMessage, HttpResponseMessage?> UnauthorizedApi(int times)
    {
        var remaining = times;
        return request => request.RequestUri!.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal)
                          && Interlocked.Decrement(ref remaining) >= 0
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : null;
    }

    [TestMethod]
    public async Task An_api_401_gets_one_refresh_and_one_retry()
    {
        using var browser = await SignedInAsync();
        _factory.Web.Fault = UnauthorizedApi(times: 1);

        var tenants = await browser.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.OK, tenants.StatusCode, await tenants.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual(1, _factory.Web.Refreshes);
        var hops = _factory.Web.ApiRequests.Where(r => r.Uri.AbsolutePath == "/api/v1/tenants").ToList();
        Assert.HasCount(2, hops);
        Assert.AreNotEqual(hops[0].Authorization, hops[1].Authorization);
    }

    [TestMethod]
    public async Task A_retried_request_sends_its_body_again()
    {
        using var browser = await SignedInAsync(EntraUser.WithMfa() with
        {
            TenantId = _factory.Entra.DeployingTenantId,
            Roles = ["Superadministrator"],
        });
        _factory.Web.Fault = UnauthorizedApi(times: 1);

        var created = await browser.FetchAsync(HttpMethod.Post, "api/v1/tenants", new StringContent(
            JsonSerializer.Serialize(new CreateTenantRequest { Name = "Contoso", ContactEmail = "it@example.test", MaxDevices = 5 },
                Signal9Json.Options), Encoding.UTF8, "application/json"));

        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync(Ct));
        Assert.AreEqual("Contoso", (await created.Content.ReadFromJsonAsync<TenantDto>(Signal9Json.Options, Ct))!.Name);
    }

    [TestMethod]
    public async Task A_second_api_401_is_returned_without_another_refresh()
    {
        using var browser = await SignedInAsync();
        _factory.Web.Fault = UnauthorizedApi(times: int.MaxValue);

        var tenants = await browser.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.Unauthorized, tenants.StatusCode);
        Assert.AreEqual(1, _factory.Web.Refreshes);
        Assert.AreEqual(2, _factory.Web.ApiRequests.Count(r => r.Uri.AbsolutePath == "/api/v1/tenants"));
    }

    [TestMethod]
    public async Task Activity_keeps_the_session_going_past_an_hour()
    {
        using var browser = await SignedInAsync();

        for (var i = 0; i < 3; i++)
        {
            _factory.Time.Advance(TimeSpan.FromMinutes(55));
            Assert.AreEqual(HttpStatusCode.OK, (await browser.GetAsync(BffRoutes.User)).StatusCode, $"after {(i + 1) * 55} minutes");
        }
        var tenants = await browser.GetAsync("api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.OK, tenants.StatusCode, await tenants.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Waits for web's revocation at auth to have been answered.</summary>
    private async Task RevokedAsync()
    {
        for (var waited = 0; !_factory.Web.AnsweredApiRequests.Any(u => u.AbsolutePath == "/connect/revoke") && waited < 100; waited++)
            await Task.Delay(100, Ct);
    }

    [TestMethod]
    public async Task An_hour_idle_ends_the_session_and_revokes_its_refresh_token_at_auth()
    {
        using var browser = await SignedInAsync();
        var refreshToken = _factory.Web.IssuedRefreshTokens.Single();

        _factory.Time.Advance(TimeSpan.FromMinutes(60));
        var me = await browser.GetAsync(BffRoutes.User);
        // The request or web's sweeper ends the session, whichever is first; the sweeper revokes in the background.
        await RevokedAsync();
        var (status, body) = await RedeemAsWebAsync(refreshToken);

        Assert.AreEqual(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, status, "The idle session's refresh token still works at auth.");
        Assert.AreEqual("invalid_grant", Error(body));
    }

    [TestMethod]
    public async Task An_idle_session_whose_person_never_returns_is_revoked_at_auth()
    {
        using var browser = await SignedInAsync();
        var refreshToken = _factory.Web.IssuedRefreshTokens.Single();

        _factory.Time.Advance(TimeSpan.FromMinutes(61));
        await RevokedAsync();
        var (status, body) = await RedeemAsWebAsync(refreshToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, status, "The idle session's refresh token still works at auth.");
        Assert.AreEqual("invalid_grant", Error(body));
    }

    [TestMethod]
    public async Task A_session_ends_after_12_hours_however_active()
    {
        using var browser = await SignedInAsync();

        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(11.5); elapsed += TimeSpan.FromMinutes(50))
        {
            _factory.Time.Advance(TimeSpan.FromMinutes(50));
            var tenants = await browser.GetAsync("api/v1/tenants");
            Assert.AreEqual(HttpStatusCode.OK, tenants.StatusCode, $"after {elapsed + TimeSpan.FromMinutes(50)}");
        }
        _factory.Time.Advance(TimeSpan.FromMinutes(20));

        Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync("api/v1/tenants")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await browser.GetAsync(BffRoutes.User)).StatusCode);
    }

    [TestMethod]
    public async Task Reaching_the_12_hour_cap_sends_the_person_back_to_entra()
    {
        using var browser = await SignedInAsync();

        // Signing in again within the cap reuses auth's own session, without Entra.
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(11.5); elapsed += TimeSpan.FromMinutes(50))
        {
            _factory.Time.Advance(TimeSpan.FromMinutes(50));
            await browser.SignInAsync(EntraUser.WithMfa());
        }
        Assert.AreEqual(1, _factory.Entra.SignIns, "auth's session didn't last.");
        _factory.Time.Advance(TimeSpan.FromMinutes(20));
        var landing = await browser.SignInAsync(EntraUser.WithMfa());

        Assert.AreEqual(HttpStatusCode.OK, landing.StatusCode);
        Assert.AreEqual(2, _factory.Entra.SignIns, "auth's session outlived the 12-hour cap.");
    }

    public void Dispose() => _factory.Dispose();
}
