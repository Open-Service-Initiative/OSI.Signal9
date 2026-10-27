using System.Net;
using System.Text.Json;
using Microsoft.FluentUI.AspNetCore.Components;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class RoleAssignmentsPageTests : DashboardTestContext
{
    private static readonly Guid ContosoId = Guid.NewGuid();
    private static readonly Guid EastId = Guid.NewGuid();
    private static readonly Guid AdministratorId = Guid.NewGuid();
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly Guid MeganId = Guid.NewGuid();
    private static readonly Guid AlexId = Guid.NewGuid();

    /// <summary>Contoso and Contoso East, two users, the built-in roles, and Megan as Contoso's Administrator.</summary>
    private Func<HttpRequestMessage, HttpResponseMessage?> Directory(Func<HttpRequestMessage, HttpResponseMessage?>? writes = null) =>
        request => writes?.Invoke(request) ?? (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", id: ContosoId), Tenant("Contoso East", id: EastId))),
            ("GET", "/api/v1/users") => FakeHttp.Json(HttpStatusCode.OK, Page(User("Megan Bowen", ContosoId, MeganId), User("Alex Wilber", EastId, AlexId))),
            ("GET", "/api/v1/roles") => FakeHttp.Json(HttpStatusCode.OK, Page(Role("Administrator", AdministratorId), Role("Viewer", ViewerId))),
            ("GET", var path) when path == $"/api/v1/tenants/{ContosoId}/role-assignments" =>
                FakeHttp.Json(HttpStatusCode.OK, Page(RoleAssignment(ContosoId, MeganId, AdministratorId))),
            ("GET", var path) when path == $"/api/v1/tenants/{EastId}/role-assignments" =>
                FakeHttp.Json(HttpStatusCode.OK, Page(RoleAssignment(EastId, AlexId, ViewerId))),
            _ => null,
        };

    [TestMethod]
    public void Lists_the_role_assignments_at_the_chosen_tenant_by_name()
    {
        Http.SignedInAs("Ada", Directory());

        var app = RenderApp("/role-assignments");

        app.WaitForAssertion(() => Assert.Contains("Megan Bowen", app.Markup));
        var row = app.FindAll("tr").Select(r => r.TextContent).Single(t => t.Contains("Megan Bowen"));
        Assert.Contains("Administrator", row);

        ChooseTenant(app, EastId);

        app.WaitForAssertion(() => Assert.Contains("Alex Wilber", app.Find("fluent-data-grid, table, [role=grid]").TextContent));
        Assert.DoesNotContain("Megan Bowen", app.Find("fluent-data-grid, table, [role=grid]").TextContent);
    }

    [TestMethod]
    public void Grant_posts_the_chosen_user_and_role_at_the_tenant()
    {
        Http.SignedInAs("Ada", Directory(request => request.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.Created, RoleAssignment(ContosoId, AlexId, ViewerId))
            : null));
        var app = RenderApp("/role-assignments");

        app.WaitForElement("#grant-role").Click();
        app.WaitForElement("fluent-dialog");
        Choose(app, "assignment-user", AlexId);
        Choose(app, "assignment-role", ViewerId);
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Post)));
        var post = Http.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.AreEqual($"/api/v1/tenants/{ContosoId}/role-assignments", post.PathAndQuery);
        var request = JsonSerializer.Deserialize<CreateRoleAssignmentRequest>(post.Body!, Signal9Json.Options)!;
        Assert.AreEqual(AlexId, request.UserId);
        Assert.AreEqual(ViewerId, request.RoleId);
        app.WaitForAssertion(() => Assert.Contains("Gave Alex Wilber the Viewer role.", app.Find(".notice-bar").TextContent));
    }

    [TestMethod]
    public void Revoke_asks_first_then_deletes_the_assignment()
    {
        var assignmentId = Guid.NewGuid();
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", var path) when path == $"/api/v1/tenants/{ContosoId}/role-assignments" =>
                FakeHttp.Json(HttpStatusCode.OK, Page(RoleAssignment(ContosoId, MeganId, AdministratorId, assignmentId))),
            ("DELETE", _) => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => Directory()(request),
        });
        var app = RenderApp("/role-assignments");

        app.WaitForElement($"#revoke-{assignmentId}").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        Assert.Contains("Megan Bowen", dialog.TextContent);
        Assert.IsFalse(Http.Requests.Any(r => r.Method == HttpMethod.Delete), "Nothing is revoked before the person confirms.");

        dialog.QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Delete
            && r.PathAndQuery == $"/api/v1/tenants/{ContosoId}/role-assignments/{assignmentId}")));
        app.WaitForAssertion(() => Assert.Contains("Revoked Megan Bowen's Administrator role.", app.Find(".notice-bar").TextContent));
    }

    [TestMethod]
    public void A_refused_revocation_shows_why()
    {
        var assignmentId = Guid.NewGuid();
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", var path) when path == $"/api/v1/tenants/{ContosoId}/role-assignments" =>
                FakeHttp.Json(HttpStatusCode.OK, Page(RoleAssignment(ContosoId, MeganId, AdministratorId, assignmentId))),
            ("DELETE", _) => FakeHttp.Problem(HttpStatusCode.Conflict, "Last Administrator", "Grant another Administrator first."),
            _ => Directory()(request),
        });
        var app = RenderApp("/role-assignments");

        app.WaitForElement($"#revoke-{assignmentId}").Click();
        app.WaitForElement("fluent-dialog").QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.Contains("Grant another Administrator first.", app.Find(".error-bar").TextContent));
        Assert.Contains("Megan Bowen", app.Find("fluent-data-grid, table, [role=grid]").TextContent);
    }

    private static void ChooseTenant(IRenderedComponent<OSI.Signal9.Web.Client.Routes> app, Guid tenantId) =>
        Choose(app, "assignment-tenant", tenantId);

    private static void Choose(IRenderedComponent<OSI.Signal9.Web.Client.Routes> app, string selectId, Guid value)
    {
        var option = app.WaitForElement($"#{selectId} fluent-option[value='{value}']");
        app.Find($"#{selectId}").TriggerEvent("ondropdownchange", new DropdownEventArgs { Id = selectId, SelectedOptions = option.Id! });
    }
}
