using System.Net;
using System.Text.Json;
using Microsoft.FluentUI.AspNetCore.Components;
using OSI.Signal9.Contracts.ApiApplications;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class ApiApplicationsPageTests : DashboardTestContext
{
    private static readonly Guid ContosoId = Guid.NewGuid();
    private static readonly Guid EastId = Guid.NewGuid();
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly Guid TechnicianId = Guid.NewGuid();
    private static readonly Guid ReportingId = Guid.NewGuid();

    private static readonly RoleDto Viewer = new(ViewerId, "Viewer", "Read-only.", true, ["tenants.read", "devices.read"]);
    private static readonly RoleDto Technician = new(TechnicianId, "Technician", "Operates devices.", true,
        ["tenants.read", "devices.read", "devices.write", "scripts.execute"]);

    private static string ApplicationsPath(Guid tenantId) => $"/api/v1/tenants/{tenantId}/api-applications";

    /// <summary>Contoso and Contoso East, two roles, and a Reporting application at Contoso.</summary>
    private Func<HttpRequestMessage, HttpResponseMessage?> Directory(Func<HttpRequestMessage, HttpResponseMessage?>? writes = null) =>
        request => writes?.Invoke(request) ?? (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", id: ContosoId), Tenant("Contoso East", id: EastId))),
            ("GET", "/api/v1/roles") => FakeHttp.Json(HttpStatusCode.OK, Page(Viewer, Technician)),
            ("GET", var path) when path == ApplicationsPath(ContosoId) => FakeHttp.Json(HttpStatusCode.OK,
                Page(ApiApplication("Reporting", ContosoId, ViewerId, TokenEndpointAuthMethods.ClientSecret, ReportingId, "tenants.read"))),
            ("GET", var path) when path == ApplicationsPath(EastId) => FakeHttp.Json(HttpStatusCode.OK,
                Page(ApiApplication("Backup vendor", EastId, TechnicianId, TokenEndpointAuthMethods.PrivateKeyJwt))),
            ("GET", var path) when path == $"{ApplicationsPath(ContosoId)}/{ReportingId}/credentials" => FakeHttp.Json(HttpStatusCode.OK,
                Page(new ApiApplicationCredentialDto(Guid.NewGuid(), ApiApplicationCredentialTypes.Secret, null, null, Now, Now.AddDays(90)))),
            _ => null,
        };

    [TestMethod]
    public void Lists_the_applications_at_the_chosen_tenant_with_their_role_and_method()
    {
        Http.SignedInAs("Ada", Directory());

        var app = RenderApp("/api-applications");

        app.WaitForAssertion(() => Assert.Contains("Reporting", app.Markup));
        var row = app.FindAll("tr").Select(r => r.TextContent).Single(t => t.Contains("Reporting"));
        Assert.Contains("Viewer", row);
        Assert.Contains("Client secret", row);

        Choose(app, "application-tenant", EastId.ToString());

        app.WaitForAssertion(() => Assert.Contains("Backup vendor", Grid(app)));
        Assert.DoesNotContain("Reporting", Grid(app));
    }

    [TestMethod]
    public void Register_posts_the_name_role_method_and_the_roles_scopes_that_need_no_recent_mfa()
    {
        Http.SignedInAs("Ada", Directory(request => request.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.Created, ApiApplication("Backup", ContosoId, TechnicianId, TokenEndpointAuthMethods.PrivateKeyJwt))
            : null));
        var app = RenderApp("/api-applications");

        app.WaitForElement("#register-application").Click();
        app.WaitForElement("fluent-dialog");
        app.Find("#application-name").Change("Backup");
        Choose(app, "application-role", TechnicianId.ToString());
        Choose(app, "application-method", TokenEndpointAuthMethods.PrivateKeyJwt);
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Post)));
        var post = Http.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.AreEqual(ApplicationsPath(ContosoId), post.PathAndQuery);
        var request = JsonSerializer.Deserialize<CreateApiApplicationRequest>(post.Body!, Signal9Json.Options)!;
        Assert.AreEqual("Backup", request.DisplayName);
        Assert.AreEqual(TechnicianId, request.RoleId);
        Assert.AreEqual(TokenEndpointAuthMethods.PrivateKeyJwt, request.TokenEndpointAuthMethod);
        CollectionAssert.AreEquivalent(new[] { "tenants.read", "devices.read", "devices.write" }, request.Scopes.ToArray());
        app.WaitForAssertion(() => Assert.Contains("Registered Backup.", app.Find(".notice-bar").TextContent));
    }

    [TestMethod]
    public void A_new_secret_is_shown_once()
    {
        Http.SignedInAs("Ada", Directory(request => request.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.Created, new ApiApplicationCredentialDto(Guid.NewGuid(), ApiApplicationCredentialTypes.Secret,
                null, null, Now, Now.AddDays(180), "s3cr3t-value"))
            : null));
        var app = RenderApp("/api-applications");

        app.WaitForElement($"#credentials-{ReportingId}").Click();
        app.WaitForElement("#add-credential").Click();
        app.WaitForElement("fluent-dialog").QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.Contains("s3cr3t-value", app.Find(".secret-bar").TextContent));
        var post = Http.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.AreEqual($"{ApplicationsPath(ContosoId)}/{ReportingId}/credentials", post.PathAndQuery);
        StringAssert.Contains(app.Find(".secret-bar").TextContent, "won't be shown again");
    }

    [TestMethod]
    public void Remove_asks_first_then_deletes_the_application()
    {
        Http.SignedInAs("Ada", Directory(request => request.Method == HttpMethod.Delete
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : null));
        var app = RenderApp("/api-applications");

        app.WaitForElement($"#remove-{ReportingId}").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        Assert.Contains("Reporting", dialog.TextContent);
        Assert.IsFalse(Http.Requests.Any(r => r.Method == HttpMethod.Delete), "Nothing is removed before the person confirms.");

        dialog.QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Delete
            && r.PathAndQuery == $"{ApplicationsPath(ContosoId)}/{ReportingId}")));
        app.WaitForAssertion(() => Assert.Contains("Removed Reporting.", app.Find(".notice-bar").TextContent));
    }

    private static string Grid(IRenderedComponent<OSI.Signal9.Web.Client.Routes> app) =>
        app.Find("fluent-data-grid, table, [role=grid]").TextContent;

    private static void Choose(IRenderedComponent<OSI.Signal9.Web.Client.Routes> app, string selectId, string value)
    {
        var option = app.WaitForElement($"#{selectId} fluent-option[value='{value}']");
        app.Find($"#{selectId}").TriggerEvent("ondropdownchange", new DropdownEventArgs { Id = selectId, SelectedOptions = option.Id! });
    }
}
