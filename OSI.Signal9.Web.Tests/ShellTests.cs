using System.Net;
using Bunit.TestDoubles;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.Contracts.Bff;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class ShellTests : DashboardTestContext
{
    [TestMethod]
    public void The_navigation_links_to_every_dashboard_page()
    {
        Http.SignedOut();

        var app = RenderApp();

        // The hamburger's drawer repeats the navigation for narrow screens.
        var links = app.FindAll("nav a[href]").Select(a => a.GetAttribute("href")).Distinct().ToList();
        CollectionAssert.AreEquivalent(new[] { "/", "/devices", "/agent-approvals", "/tenants", "/role-assignments", "/enrollment-tokens",
            "/api-applications", "/api-applications/connections", "/connected-applications", "/audit" }, links);
    }

    [TestMethod]
    public void A_signed_in_person_sees_their_name_and_can_sign_out()
    {
        Http.SignedInAs("Ada Lovelace");

        var app = RenderApp();

        app.WaitForAssertion(() => Assert.Contains("Ada Lovelace", app.Find("#login-display").TextContent));
        Assert.IsEmpty(app.FindAll("#sign-in"));
    }

    [TestMethod]
    public void Sign_in_goes_to_web_and_comes_back_to_the_current_page()
    {
        Http.SignedOut();
        var app = RenderApp("/devices?status=Online");

        app.WaitForElement("#login-display #sign-in").Click();

        Assert.AreEqual("http://localhost/bff/login?returnUrl=%2Fdevices%3Fstatus%3DOnline", Navigation.ToAbsoluteUri(LastNavigation.Uri).ToString());
        Assert.IsTrue(LastNavigation.Options.ForceLoad);
    }

    [TestMethod]
    public void Sign_out_posts_to_web_then_reloads_the_home_page()
    {
        Http.SignedInAs("Ada Lovelace", request =>
            request.RequestUri!.AbsolutePath == "/" + BffRoutes.Logout ? new HttpResponseMessage(HttpStatusCode.NoContent) : null);
        var app = RenderApp("/tenants");

        app.WaitForElement("#sign-out").Click();

        app.WaitForAssertion(() => Assert.AreEqual("http://localhost/", Navigation.ToAbsoluteUri(LastNavigation.Uri).ToString()));
        Assert.IsTrue(LastNavigation.Options.ForceLoad);
        var logout = Http.Requests.Single(r => r.PathAndQuery == "/" + BffRoutes.Logout);
        Assert.AreEqual(HttpMethod.Post, logout.Method);
        Assert.AreEqual(BffRoutes.CsrfHeaderValue, logout.Header(BffRoutes.CsrfHeader));
    }

    [TestMethod]
    [DataRow("/devices")]
    [DataRow("/tenants")]
    public void An_anonymous_visitor_is_asked_to_sign_in_rather_than_shown_an_error(string path)
    {
        Http.SignedOut();

        var app = RenderApp(path);

        app.WaitForElement("#sign-in-prompt");
        Assert.IsEmpty(app.FindAll(".error-bar"));
        Assert.IsFalse(Http.Requests.Any(r => r.PathAndQuery.StartsWith("/api/", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/devices")]
    [DataRow("/tenants")]
    public void Pages_render_no_script_or_style_elements_the_CSP_would_block(string path)
    {
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/devices" => FakeHttp.Json(HttpStatusCode.OK, Samples.Page(Samples.Device("WS-001"))),
            "/api/v1/tenants" => FakeHttp.Json(HttpStatusCode.OK, Samples.Page(Samples.Tenant("Contoso"))),
            _ => null,
        });

        var app = RenderApp(path);
        app.WaitForAssertion(() => Assert.Contains("Ada", app.Find("#login-display").TextContent));

        Assert.IsEmpty(app.FindAll("script"));
        // FluentLayout's own mobile rules are the one exception; app.css repeats them.
        var styles = app.FindAll("style").Select(s => s.TextContent).ToList();
        Assert.IsTrue(styles.All(s => s.Contains("container-type:inline-size", StringComparison.Ordinal)), string.Join("\n", styles));
        Assert.IsLessThanOrEqualTo(1, styles.Count);
    }

    [TestMethod]
    public void Home_offers_an_anonymous_visitor_a_sign_in_button()
    {
        Http.SignedOut();

        var app = RenderApp();

        app.WaitForElement("#home-sign-in");
    }
}
