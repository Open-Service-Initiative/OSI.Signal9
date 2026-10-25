using System.Net;
using System.Text.Json;
using OSI.Signal9.Contracts.Enrollment;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class EnrollmentTokensPageTests : DashboardTestContext
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _tokenId = Guid.NewGuid();

    private string Collection => $"/api/v1/tenants/{_tenantId}/enrollment-tokens";

    private string TokenUrl => $"{Collection}/{_tokenId}";

    /// <summary>Signed in, with one tenant holding one active token; <paramref name="change"/> answers the rest.</summary>
    private void Serve(Func<HttpRequestMessage, HttpResponseMessage?>? change = null) =>
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", TenantStatus.Active, _tenantId))),
            ("GET", var path) when path == Collection =>
                FakeHttp.Json(HttpStatusCode.OK, Page(EnrollmentToken("Head office", _tenantId, _tokenId, maxUses: 1))),
            _ => change?.Invoke(request),
        });

    [TestMethod]
    public void Lists_the_tokens_of_the_first_tenant_and_its_descendants()
    {
        Serve();

        var app = RenderApp("/enrollment-tokens");

        app.WaitForAssertion(() => Assert.Contains("Head office", app.Markup));
        Assert.Contains("Single use", app.Find($"#uses-{_tokenId}").TextContent);
        Assert.IsTrue(Http.Requests.Any(r => r.PathAndQuery == $"{Collection}?page=1&pageSize=25"));
    }

    [TestMethod]
    public void Shows_how_often_and_when_each_token_was_last_used()
    {
        var lastUsed = new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.Zero);
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", TenantStatus.Active, _tenantId))),
            ("GET", var path) when path == Collection => FakeHttp.Json(HttpStatusCode.OK, Page(
                EnrollmentToken("Head office", _tenantId, _tokenId, EnrollmentTokenStatus.UsedUp, maxUses: 3, useCount: 3, lastUsedAt: lastUsed))),
            _ => null,
        });

        var app = RenderApp("/enrollment-tokens");

        app.WaitForAssertion(() => Assert.Contains("Head office", app.Markup));
        Assert.AreEqual($"3 · last {lastUsed.ToLocalTime():g}", app.Find($"#used-{_tokenId}").TextContent.Trim());
        Assert.Contains("UsedUp", app.Find($"#status-{_tokenId}").TextContent);
    }

    [TestMethod]
    public void A_token_never_used_says_so()
    {
        Serve();

        var app = RenderApp("/enrollment-tokens");

        app.WaitForAssertion(() => Assert.AreEqual("Never", app.Find($"#used-{_tokenId}").TextContent.Trim()));
    }

    [TestMethod]
    public void Issuing_posts_the_limits_and_shows_the_secret_once()
    {
        Serve(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == Collection
            ? FakeHttp.Json(HttpStatusCode.Created, EnrollmentToken("Branch rollout", _tenantId, secret: "s9e_new-secret"))
            : null);
        var app = RenderApp("/enrollment-tokens");

        app.WaitForElement("#create-token").Click();
        app.WaitForElement("fluent-dialog");
        app.Find("#token-name").Change("Branch rollout");
        app.Find("#token-single-use").Change(true);
        app.Find("#token-expires-days").Change("7");
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Post)));
        var sent = JsonSerializer.Deserialize<CreateEnrollmentTokenRequest>(
            Http.Requests.Single(r => r.Method == HttpMethod.Post).Body!, Signal9Json.Options)!;
        Assert.AreEqual("Branch rollout", sent.Name);
        Assert.AreEqual(1, sent.MaxUses);
        Assert.AreEqual(Time.GetUtcNow().AddDays(7), sent.ExpiresAt);
        app.WaitForAssertion(() => Assert.Contains("s9e_new-secret", app.Find("#token-secret").OuterHtml));
        Assert.Contains("won't be shown again", app.Find(".secret-bar").TextContent);
    }

    [TestMethod]
    public void Rotating_asks_first_then_shows_the_new_secret()
    {
        Serve(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == $"{TokenUrl}:rotate"
            ? FakeHttp.Json(HttpStatusCode.OK, EnrollmentToken("Head office", _tenantId, _tokenId, secret: "s9e_rotated"))
            : null);
        var app = RenderApp("/enrollment-tokens");

        app.WaitForElement($"#rotate-{_tokenId}").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        Assert.IsFalse(Http.Requests.Any(r => r.Method == HttpMethod.Post), "Nothing changes before the person confirms.");
        dialog.QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.Contains("s9e_rotated", app.Find("#token-secret").OuterHtml));
    }

    [TestMethod]
    public void Revoking_asks_first_then_shows_the_token_as_revoked()
    {
        Serve(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == $"{TokenUrl}:revoke"
            ? FakeHttp.Json(HttpStatusCode.OK, EnrollmentToken("Head office", _tenantId, _tokenId, EnrollmentTokenStatus.Revoked))
            : null);
        var app = RenderApp("/enrollment-tokens");

        app.WaitForElement($"#revoke-{_tokenId}").Click();
        app.WaitForElement("fluent-dialog").QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.Contains("Revoked", app.Find($"#status-{_tokenId}").TextContent));
        Assert.IsEmpty(app.FindAll($"#revoke-{_tokenId}"));
        Assert.IsEmpty(app.FindAll($"#rotate-{_tokenId}"));
    }

    [TestMethod]
    public void A_refusal_is_shown_in_an_error_message_bar()
    {
        Serve(request => request.Method == HttpMethod.Post
            ? FakeHttp.Problem(HttpStatusCode.Conflict, "Transition not allowed", "The enrollment token is revoked.")
            : null);
        var app = RenderApp("/enrollment-tokens");

        app.WaitForElement($"#revoke-{_tokenId}").Click();
        app.WaitForElement("fluent-dialog").QuerySelector("fluent-button[appearance=primary]")!.Click();

        Assert.Contains("Transition not allowed", app.WaitForElement(".error-bar").TextContent);
    }
}
