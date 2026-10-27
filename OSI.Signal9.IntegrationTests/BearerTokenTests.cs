using System.Net;
using System.Net.Http.Headers;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// api reads access tokens only from the Authorization header (RFC 6750 §2.1): a token in the query string leaks into
/// logs, history and Referer headers, so it's never accepted there or in a form body (RFC 6750 §2.3, RFC 9700 §4.3.2).
/// </summary>
[TestClass]
public sealed class BearerTokenTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public BearerTokenTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    /// <summary>A token that reads and writes tenants: the Superadministrator's.</summary>
    private Task<string> TokenAsync() => _factory.SuperadministratorTokenAsync();

    [TestMethod]
    public async Task A_token_in_the_authorization_header_is_accepted()
    {
        using var client = _factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync());

        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("api/v1/tenants", Ct)).StatusCode);
    }

    [TestMethod]
    public async Task A_token_in_the_query_string_is_refused_with_401()
    {
        using var client = _factory.CreateAnonymousClient();

        var response = await client.GetAsync($"api/v1/tenants?access_token={await TokenAsync()}", Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task A_token_in_a_form_body_is_refused_with_401()
    {
        using var client = _factory.CreateAnonymousClient();

        var response = await client.PostAsync("api/v1/tenants",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["access_token"] = await TokenAsync(), ["name"] = "Contoso" }), Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
