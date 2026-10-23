using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// People sign in at <c>auth</c> through the operator's multi-tenant Entra app registration, with MFA mandatory
/// (stories 38-44), and auth issues its own authorization code to a confidential client (#109).
/// </summary>
[TestClass]
public sealed class EntraSignInTests : IDisposable
{
    private readonly Signal9ApiFactory _factory;
    private readonly TestContext _testContext;

    public EntraSignInTests(TestContext testContext)
    {
        _testContext = testContext;
        _factory = new Signal9ApiFactory();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    private const string ClientId = "dashboard-like-app";

    private bool _seeded;

    private async Task<(Browser Browser, CodeFlow Flow)> StartAsync()
    {
        if (!_seeded)
            await _factory.SeedCodeClientAsync(ClientId, "tenants.read");
        _seeded = true;
        return (new Browser(_factory.Auth.Server), new CodeFlow(ClientId));
    }

    private async Task<HttpResponseMessage> RedeemAsync(CodeFlow flow, string code)
    {
        using var auth = _factory.Auth.CreateClient();
        return await auth.PostAsync("connect/token", flow.Redemption(code), Ct);
    }

    private async Task<string> SignInAsync(EntraUser user)
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;
        var code = flow.CodeFrom(await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra, user));
        var response = await RedeemAsync(flow, code);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("access_token").GetString()!;
    }

    private bool _resourceServerSeeded;

    /// <summary>Introspects as the api resource server, the token's audience, which sees all of its claims.</summary>
    private async Task<JsonElement> IntrospectAsync(string token)
    {
        if (!_resourceServerSeeded)
            await _factory.SeedClientAsync("signal9-api");
        _resourceServerSeeded = true;
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

    private async Task<HttpStatusCode> CallApiAsync(string token)
    {
        using var api = _factory.CreateAnonymousClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await api.GetAsync("api/v1/tenants", Ct)).StatusCode;
    }

    [TestMethod]
    [DataRow("pwd mfa")]
    [DataRow("rsa mfa")]
    [DataRow("ngcmfa mfa")]
    public async Task Confidential_client_completes_authorization_code_with_pkce_through_entra(string amr)
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;

        var redirect = await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra,
            EntraUser.WithMfa() with { Amr = amr.Split(' ') });
        var response = await RedeemAsync(flow, flow.CodeFrom(redirect));
        var tokens = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, tokens.ToString());
        Assert.IsTrue(tokens.TryGetProperty("id_token", out _));
        Assert.AreEqual(HttpStatusCode.OK, await CallApiAsync(tokens.GetProperty("access_token").GetString()!));
    }

    [TestMethod]
    public async Task A_signed_in_person_is_not_sent_to_entra_again()
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;
        flow.CodeFrom(await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra, EntraUser.WithMfa()));

        var second = new CodeFlow(ClientId);
        var response = await browser.GetAsync(second.AuthorizeRequest());

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.IsFalse(string.IsNullOrEmpty(second.CodeFrom(response.Headers.Location!)));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("plain")]
    public async Task Authorization_requires_an_s256_code_challenge(string? method)
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;

        var response = await browser.GetAsync(flow.AuthorizeRequest(challengeMethod: method));

        // OpenIddict checks the challenge before the redirect URI, so it shows the error rather than redirecting.
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_request", await response.Content.ReadAsStringAsync(Ct));
    }

    [TestMethod]
    public async Task Redemption_needs_the_code_verifier()
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;
        var code = flow.CodeFrom(await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra, EntraUser.WithMfa()));

        var response = await RedeemAsync(new CodeFlow(ClientId), code);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("invalid_grant", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task Reusing_a_code_fails_and_revokes_everything_issued_from_it()
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;
        var code = flow.CodeFrom(await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra, EntraUser.WithMfa()));
        var first = await (await RedeemAsync(flow, code)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        var token = first.GetProperty("access_token").GetString()!;
        var beforeReuse = await CallApiAsync(token);

        var reuse = await RedeemAsync(flow, code);

        Assert.AreEqual(HttpStatusCode.OK, beforeReuse);
        Assert.AreEqual(HttpStatusCode.BadRequest, reuse.StatusCode);
        Assert.AreEqual("invalid_grant", (await reuse.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.AreEqual(HttpStatusCode.Unauthorized, await CallApiAsync(token));
    }

    [TestMethod]
    public async Task Authorization_codes_last_5_minutes()
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;
        var stale = flow.CodeFrom(await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra, EntraUser.WithMfa()));
        var freshFlow = new CodeFlow(ClientId);
        var fresh = freshFlow.CodeFrom((await browser.GetAsync(freshFlow.AuthorizeRequest())).Headers.Location!);

        _factory.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var beforeExpiry = await RedeemAsync(freshFlow, fresh);
        _factory.Time.Advance(TimeSpan.FromSeconds(2));
        var afterExpiry = await RedeemAsync(flow, stale);

        Assert.AreEqual(HttpStatusCode.OK, beforeExpiry.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, afterExpiry.StatusCode);
        Assert.AreEqual("invalid_grant", (await afterExpiry.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task People_are_identified_by_directory_and_object_id_never_email()
    {
        var person = EntraUser.WithMfa();
        var renamed = person with { Email = "alex.renamed@contoso.example" };
        var recycledEmail = person with { ObjectId = Guid.NewGuid() };
        var otherDirectory = person with { TenantId = Guid.NewGuid() };

        var subject = (await IntrospectAsync(await SignInAsync(person))).GetProperty("sub").GetString();
        var renamedSubject = (await IntrospectAsync(await SignInAsync(renamed))).GetProperty("sub").GetString();
        var recycledSubject = (await IntrospectAsync(await SignInAsync(recycledEmail))).GetProperty("sub").GetString();
        var otherDirectorySubject = (await IntrospectAsync(await SignInAsync(otherDirectory))).GetProperty("sub").GetString();

        Assert.AreEqual(subject, renamedSubject);
        Assert.AreNotEqual(subject, recycledSubject);
        Assert.AreNotEqual(subject, otherDirectorySubject);
        Assert.DoesNotContain("contoso", subject!);
    }

    [TestMethod]
    [DataRow("pwd")]
    [DataRow("wia")]
    [DataRow("rsa")]
    [DataRow("wiaormfa")]
    [DataRow("pwd rsa")]
    [DataRow("wia rsa")]
    public async Task Sign_in_without_mfa_is_rejected(string amr)
    {
        var (browser, flow) = await StartAsync();
        using var session = browser;

        var redirect = await browser.AuthorizeAsync(flow.AuthorizeRequest(), _factory.Entra,
            EntraUser.WithMfa() with { Amr = amr.Split(' ') });

        Assert.AreEqual("access_denied", CodeFlow.ErrorFrom(redirect));
        // Nothing was signed in: the next request goes back to Entra.
        var retry = await browser.GetAsync(flow.AuthorizeRequest());
        Assert.AreEqual(new Uri(FakeEntra.Instance).Host, retry.Headers.Location?.Host);
    }

    [TestMethod]
    public async Task Allow_list_restricts_sign_in_to_trusted_directories()
    {
        var trusted = Guid.NewGuid();
        using var factory = new Signal9ApiFactory(settings: new Dictionary<string, string?>
        {
            ["Entra:AllowedTenantIds:0"] = trusted.ToString(),
        });
        await factory.SeedCodeClientAsync(ClientId, "tenants.read");
        using var trustedBrowser = new Browser(factory.Auth.Server);
        using var untrustedBrowser = new Browser(factory.Auth.Server);
        var flow = new CodeFlow(ClientId);

        var fromTrusted = await trustedBrowser.AuthorizeAsync(flow.AuthorizeRequest(), factory.Entra,
            EntraUser.WithMfa() with { TenantId = trusted });
        var fromUntrusted = await untrustedBrowser.AuthorizeAsync(flow.AuthorizeRequest(), factory.Entra,
            EntraUser.WithMfa());

        Assert.IsFalse(string.IsNullOrEmpty(flow.CodeFrom(fromTrusted)));
        Assert.AreEqual("access_denied", CodeFlow.ErrorFrom(fromUntrusted));
    }

    [TestMethod]
    public async Task Signing_in_without_role_assignments_creates_no_user_record()
    {
        var before = await CountRowsAsync();

        await SignInAsync(EntraUser.WithMfa() with { Roles = ["Superadministrator"] });

        CollectionAssert.AreEquivalent(before, await CountRowsAsync());
    }

    [TestMethod]
    public void The_app_registration_never_authenticates_with_a_client_secret()
    {
        using var factory = new Signal9ApiFactory(settings: new Dictionary<string, string?>
        {
            ["Entra:ClientCredentials:0:SourceType"] = "ClientSecret",
            ["Entra:ClientCredentials:0:ClientSecret"] = "a-secret-that-must-never-be-used",
        });

        var exception = Assert.Throws<Exception>(() => factory.Auth.Server);

        Assert.Contains("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rows in every table but the authorization server's own (applications, authorizations, tokens) and the stored
    /// keys: signing in must leave the rest of the system untouched.
    /// </summary>
    private async Task<string[]> CountRowsAsync()
    {
        await using var connection = new NpgsqlConnection(_factory.ConnectionString);
        await connection.OpenAsync(Ct);
        List<string> tables = [];
        await using (var command = new NpgsqlCommand("""
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public' AND table_type = 'BASE TABLE'
              AND table_name NOT LIKE 'OpenIddict%' AND table_name <> '__EFMigrationsHistory'
            """, connection))
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
                tables.Add(reader.GetString(0));
        }

        List<string> counts = [];
        foreach (var table in tables.Where(t => !t.Contains("Key", StringComparison.Ordinal)))
        {
            await using var count = new NpgsqlCommand($"""SELECT count(*) FROM "{table}" """, connection);
            counts.Add($"{table}={await count.ExecuteScalarAsync(Ct)}");
        }
        return [.. counts];
    }

    public void Dispose() => _factory.Dispose();
}
