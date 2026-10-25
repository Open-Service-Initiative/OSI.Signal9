using System.Net;
using OSI.Signal9.Contracts.Audit;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class AuditPageTests : DashboardTestContext
{
    [TestMethod]
    public void Shows_the_trail_the_API_returns_newest_first()
    {
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath == "/api/v1/audit-entries"
            ? FakeHttp.Json(HttpStatusCode.OK, Page(
                AuditEntry(AuditActions.CommandCreated, deviceId: Guid.NewGuid(), deviceName: "WS-001", detail: "RestartMachine"),
                AuditEntry(AuditActions.AuthorizationDenied, tenantName: null, outcome: AuditOutcome.Denied, superadministrator: true)))
            : null);

        var app = RenderApp("/audit");

        app.WaitForAssertion(() => Assert.Contains("command.created", app.Markup));
        var rows = app.FindAll("tr").Select(r => r.TextContent)
            .Where(t => t.Contains("command.created") || t.Contains("authorization.denied")).ToList();
        Assert.HasCount(2, rows);
        Assert.Contains("command.created", rows[0], "In the order the API returned them.");
        Assert.Contains("WS-001", rows[0]);
        Assert.Contains("RestartMachine", rows[0]);
        Assert.Contains("Alex Wilber", rows[0]);
        Assert.Contains("Contoso", rows[0]);
        Assert.Contains("Denied", rows[1]);
        Assert.Contains("Deployment", rows[1]);
        Assert.Contains("Superadministrator", rows[1]);
        Assert.IsTrue(Http.Requests.Any(r => r.PathAndQuery == "/api/v1/audit-entries?page=1&pageSize=25"));
    }

    [TestMethod]
    public void An_entry_without_resolved_names_shows_ids_only()
    {
        var deviceId = Guid.NewGuid();
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath == "/api/v1/audit-entries"
            ? FakeHttp.Json(HttpStatusCode.OK, Page(AuditEntry(AuditActions.CommandCreated, tenantName: null, deviceId: deviceId, actorName: null)))
            : null);

        var app = RenderApp("/audit");

        app.WaitForAssertion(() => Assert.Contains(deviceId.ToString(), app.Markup));
        Assert.Contains("tid:oid", app.Markup);
    }

    [TestMethod]
    public void A_problem_response_is_shown_in_an_error_message_bar()
    {
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath == "/api/v1/audit-entries"
            ? FakeHttp.Problem(HttpStatusCode.Forbidden, "Forbidden", "The token lacks audit.read.")
            : null);

        var app = RenderApp("/audit");

        Assert.Contains("The token lacks audit.read.", app.WaitForElement(".error-bar").TextContent);
    }
}
