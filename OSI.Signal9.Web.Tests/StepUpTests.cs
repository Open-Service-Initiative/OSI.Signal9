using System.Net;
using OSI.Signal9.Contracts.Tenants;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

/// <summary>
/// An action that needs a recent MFA answers with RFC 9470's step-up challenge (#120): the dashboard sends the person
/// through web's sign-in with <c>max_age</c>, back to the page they were on.
/// </summary>
[TestClass]
public sealed class StepUpTests : DashboardTestContext
{
    private static HttpResponseMessage StepUpChallenge()
    {
        var response = FakeHttp.Problem(HttpStatusCode.Unauthorized, "Recent MFA required");
        response.Headers.TryAddWithoutValidation("WWW-Authenticate",
            "Bearer error=\"insufficient_user_authentication\", error_description=\"Needs a recent MFA.\", max_age=900");
        return response;
    }

    [TestMethod]
    public void A_step_up_challenge_sends_the_person_to_sign_in_again_with_max_age()
    {
        var id = Guid.NewGuid();
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", TenantStatus.Active, id))),
            ("POST", _) => StepUpChallenge(),
            _ => null,
        });
        var app = RenderApp("/tenants");

        app.WaitForElement($"#suspend-{id}").Click();

        app.WaitForAssertion(() => Assert.AreEqual("http://localhost/bff/login?returnUrl=%2Ftenants&maxAge=900",
            Navigation.ToAbsoluteUri(LastNavigation.Uri).ToString()));
        Assert.IsTrue(LastNavigation.Options.ForceLoad);
        Assert.IsEmpty(app.FindAll(".error-bar"), "Signing in again isn't an error to show.");
    }

    [TestMethod]
    public void A_plain_401_is_not_a_step_up()
    {
        var id = Guid.NewGuid();
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", TenantStatus.Active, id))),
            ("POST", _) => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => null,
        });
        var app = RenderApp("/tenants");

        app.WaitForElement($"#suspend-{id}").Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Post)));
        Assert.DoesNotContain("maxAge", Navigation.Uri);
    }
}
