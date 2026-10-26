using System.Net;
using Microsoft.FluentUI.AspNetCore.Components;
using OSI.Signal9.Contracts.ApiApplications;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class ConnectedApplicationsPageTests : DashboardTestContext
{
    private static readonly Guid ContosoId = Guid.NewGuid();
    private static readonly Guid EastId = Guid.NewGuid();
    private static readonly Guid ReportingId = Guid.NewGuid();
    private static readonly Guid BackupId = Guid.NewGuid();

    private static readonly ConnectionDto Reporting = Connection("Reporting", ContosoId, ReportingId, "Alex Wilber", "tenants.read", "devices.read");
    private static readonly ConnectionDto Backup = Connection("Backup vendor", EastId, BackupId, null, "devices.read");

    [TestMethod]
    public void A_person_sees_the_applications_connected_to_their_account()
    {
        Http.SignedInAs("Ada", Mine());

        var app = RenderApp("/connected-applications");

        app.WaitForAssertion(() => Assert.Contains("Reporting", app.Markup));
        var row = app.FindAll("tr").Select(r => r.TextContent).Single(t => t.Contains("Reporting"));
        Assert.Contains("tenants.read", row);
        Assert.Contains("devices.read", row);
        Assert.IsTrue(Http.Requests.Any(r => r.PathAndQuery == "/api/v1/me/connections?page=1&pageSize=25"));
    }

    [TestMethod]
    public void A_person_revokes_a_connection_after_confirming()
    {
        Http.SignedInAs("Ada", Mine(request => request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : null));
        var app = RenderApp("/connected-applications");

        app.WaitForElement($"#revoke-{Reporting.Id}").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        Assert.Contains("Reporting", dialog.TextContent);
        Assert.IsFalse(Http.Requests.Any(r => r.Method == HttpMethod.Delete), "Nothing is revoked before the person confirms.");
        dialog.QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Delete && r.PathAndQuery == Reporting.Url)));
        app.WaitForAssertion(() => Assert.Contains("Revoked Reporting.", app.Find(".notice-bar").TextContent));
        Assert.DoesNotContain("tenants.read", Grid(app));
    }

    [TestMethod]
    public void An_administrator_sees_who_is_connected_across_the_chosen_tenants_subtree_and_revokes()
    {
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", id: ContosoId), Tenant("Contoso East", id: EastId))),
            ("GET", var path) when path == $"/api/v1/tenants/{ContosoId}/connections" => FakeHttp.Json(HttpStatusCode.OK, Page(Reporting, Backup)),
            ("GET", var path) when path == $"/api/v1/tenants/{EastId}/connections" => FakeHttp.Json(HttpStatusCode.OK, Page(Backup)),
            ("DELETE", _) => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => null,
        });

        var app = RenderApp("/api-applications/connections");

        app.WaitForAssertion(() => Assert.Contains("Backup vendor", app.Markup));
        var reporting = app.FindAll("tr").Select(r => r.TextContent).Single(t => t.Contains("Reporting"));
        Assert.Contains("Alex Wilber", reporting);
        Assert.Contains("Contoso", reporting);
        // A person the administrator can't see is named by subject.
        Assert.Contains(Backup.Subject, app.FindAll("tr").Select(r => r.TextContent).Single(t => t.Contains("Backup vendor")));
        Assert.IsTrue(Http.Requests.Any(r => r.PathAndQuery == $"/api/v1/tenants/{ContosoId}/connections?page=1&pageSize=25"));

        app.WaitForElement($"#revoke-{Backup.Id}").Click();
        app.WaitForElement("fluent-dialog").QuerySelector("fluent-button[appearance=primary]")!.Click();
        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Delete && r.PathAndQuery == Backup.Url)));

        Choose(app, "connection-tenant", EastId.ToString());
        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.PathAndQuery == $"/api/v1/tenants/{EastId}/connections?page=1&pageSize=25")));
    }

    private static Func<HttpRequestMessage, HttpResponseMessage?> Mine(Func<HttpRequestMessage, HttpResponseMessage?>? writes = null) =>
        request => writes?.Invoke(request) ?? (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/me/connections") => FakeHttp.Json(HttpStatusCode.OK, Page(Reporting)),
            _ => null,
        };

    private static ConnectionDto Connection(string application, Guid tenantId, Guid applicationId, string? person, params string[] scopes)
    {
        var id = Guid.NewGuid();
        return new(id, $"/api/v1/tenants/{tenantId}/api-applications/{applicationId}/connections/{id}", tenantId, applicationId,
            application, $"{Guid.NewGuid()}:{Guid.NewGuid()}", person is null ? null : Guid.NewGuid(), person, scopes, Now);
    }

    private static string Grid(IRenderedComponent<OSI.Signal9.Web.Client.Routes> app) =>
        app.Find("fluent-data-grid, table, [role=grid]").TextContent;

    private static void Choose(IRenderedComponent<OSI.Signal9.Web.Client.Routes> app, string selectId, string value)
    {
        var option = app.WaitForElement($"#{selectId} fluent-option[value='{value}']");
        app.Find($"#{selectId}").TriggerEvent("ondropdownchange", new DropdownEventArgs { Id = selectId, SelectedOptions = option.Id! });
    }
}
