using System.Net;
using System.Text.Json;
using Microsoft.FluentUI.AspNetCore.Components;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class TenantsPageTests : DashboardTestContext
{
    [TestMethod]
    public void Lists_the_first_page_of_tenants_from_the_API()
    {
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath == "/api/v1/tenants"
            ? FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso"), Tenant("Fabrikam", TenantStatus.Suspended)))
            : null);

        var app = RenderApp("/tenants");

        app.WaitForAssertion(() => Assert.Contains("Fabrikam", app.Markup));
        Assert.Contains("Contoso", app.Markup);
        Assert.IsTrue(Http.Requests.Any(r => r.PathAndQuery == "/api/v1/tenants?page=1&pageSize=25"));
    }

    [TestMethod]
    public void A_problem_response_is_shown_in_an_error_message_bar()
    {
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath == "/api/v1/tenants"
            ? FakeHttp.Problem(HttpStatusCode.InternalServerError, "Something broke")
            : null);

        var app = RenderApp("/tenants");

        Assert.Contains("Something broke", app.WaitForElement(".error-bar").TextContent);
    }

    [TestMethod]
    public void Suspend_posts_the_action_and_shows_the_new_status()
    {
        var id = Guid.NewGuid();
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", TenantStatus.Active, id))),
            ("POST", var path) when path == $"/api/v1/tenants/{id}:suspend" =>
                FakeHttp.Json(HttpStatusCode.OK, Tenant("Contoso", TenantStatus.Suspended, id)),
            _ => null,
        });
        var app = RenderApp("/tenants");

        app.WaitForElement($"#suspend-{id}").Click();

        app.WaitForAssertion(() => Assert.Contains("Suspended", app.Find($"#status-{id}").TextContent));
        Assert.IsEmpty(app.FindAll($"#suspend-{id}"));
    }

    [TestMethod]
    public void A_refused_transition_shows_the_conflict()
    {
        var id = Guid.NewGuid();
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", TenantStatus.Active, id))),
            ("POST", _) => FakeHttp.Problem(HttpStatusCode.Conflict, "Transition not allowed", "The tenant is Cancelled."),
            _ => null,
        });
        var app = RenderApp("/tenants");

        app.WaitForElement($"#suspend-{id}").Click();

        Assert.Contains("Transition not allowed", app.WaitForElement(".error-bar").TextContent);
    }

    [TestMethod]
    public void Create_posts_the_new_tenant_under_the_chosen_parent()
    {
        var parentId = Guid.NewGuid();
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", TenantStatus.Active, parentId))),
            ("POST", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.Created, Tenant("Contoso East")),
            _ => null,
        });
        var app = RenderApp("/tenants");

        app.WaitForElement("#create-tenant").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        dialog.QuerySelector("#tenant-name")!.Change("Contoso East");
        app.Find("#tenant-email").Change("east@contoso.example");
        app.Find("#tenant-max-devices").Change("50");
        var parentOption = app.Find($"#tenant-parent fluent-option[value='{parentId}']");
        app.Find("#tenant-parent").TriggerEvent("ondropdownchange",
            new DropdownEventArgs { Id = "tenant-parent", SelectedOptions = parentOption.Id! });

        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Post)));
        var created = JsonSerializer.Deserialize<CreateTenantRequest>(
            Http.Requests.Single(r => r.Method == HttpMethod.Post).Body!, Signal9Json.Options)!;
        Assert.AreEqual("Contoso East", created.Name);
        Assert.AreEqual("east@contoso.example", created.ContactEmail);
        Assert.AreEqual(50, created.MaxDevices);
        Assert.AreEqual(parentId, created.ParentTenantId);
        app.WaitForAssertion(() => Assert.Contains("Created Contoso East.", app.Find(".notice-bar").TextContent));
    }

    [TestMethod]
    public void Edit_sends_the_editable_fields_as_a_merge_patch()
    {
        var id = Guid.NewGuid();
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", TenantStatus.Active, id))),
            ("PATCH", _) => FakeHttp.Json(HttpStatusCode.OK, Tenant("Contoso Ltd", TenantStatus.Active, id)),
            _ => null,
        });
        var app = RenderApp("/tenants");

        app.WaitForElement($"#edit-{id}").Click();
        app.WaitForElement("#tenant-name").Change("Contoso Ltd");
        Assert.IsEmpty(app.FindAll("#tenant-parent"), "The parent changes by tenant transfer, not by editing.");
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.Contains("Contoso Ltd", app.Find("fluent-data-grid, table, [role=grid]").TextContent));
        var patch = Http.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.AreEqual($"/api/v1/tenants/{id}", patch.PathAndQuery);
        Assert.AreEqual("application/merge-patch+json", patch.ContentType);
        Assert.AreEqual("Contoso Ltd", JsonSerializer.Deserialize<UpdateTenantRequest>(patch.Body!, Signal9Json.Options)!.Name);
    }
}
