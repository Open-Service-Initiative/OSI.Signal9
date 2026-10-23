using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.Contracts.Bff;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class CsrfHeaderTests : DashboardTestContext
{
    [TestMethod]
    [DataRow("GET")]
    [DataRow("POST")]
    [DataRow("PATCH")]
    [DataRow("DELETE")]
    public async Task Every_request_to_web_carries_the_CSRF_header(string method)
    {
        Http.Respond = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
        var client = Services.GetRequiredService<HttpClient>();

        await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), "api/v1/anything"));

        var request = Http.Requests.Single();
        Assert.AreEqual(BffRoutes.CsrfHeaderValue, request.Header(BffRoutes.CsrfHeader));
        Assert.AreEqual("http://localhost/api/v1/anything", request.Uri.ToString());
    }
}
