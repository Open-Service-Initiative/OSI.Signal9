using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class AuthenticationStateTests : DashboardTestContext
{
    private Task<AuthenticationState> GetStateAsync() =>
        Services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();

    [TestMethod]
    public async Task A_session_at_web_is_a_signed_in_person()
    {
        Http.SignedInAs("Ada Lovelace");

        var user = (await GetStateAsync()).User;

        Assert.IsTrue(user.Identity?.IsAuthenticated);
        Assert.AreEqual("Ada Lovelace", user.Identity?.Name);
        Assert.AreEqual("subject-1", user.FindFirst("sub")?.Value);
        Assert.AreEqual("/bff/user", Http.Requests.Single().PathAndQuery);
    }

    [TestMethod]
    public async Task A_401_from_web_is_an_anonymous_visitor()
    {
        Http.SignedOut();

        var user = (await GetStateAsync()).User;

        Assert.IsFalse(user.Identity?.IsAuthenticated);
    }

    [TestMethod]
    public async Task Web_failing_is_an_anonymous_visitor_not_a_crash()
    {
        Http.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        Assert.IsFalse((await GetStateAsync()).User.Identity?.IsAuthenticated);

        Http.Respond = _ => throw new HttpRequestException("web is down");
        await Services.GetRequiredService<Client.Auth.BffAuthenticationStateProvider>().RefreshAsync();

        Assert.IsFalse((await GetStateAsync()).User.Identity?.IsAuthenticated);
    }
}
