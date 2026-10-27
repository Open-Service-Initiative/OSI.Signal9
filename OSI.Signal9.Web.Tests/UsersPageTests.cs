using System.Net;
using System.Text.Json;
using Microsoft.FluentUI.AspNetCore.Components;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Users;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class UsersPageTests : DashboardTestContext
{
    private static readonly Guid ContosoId = Guid.NewGuid();
    private static readonly Guid EastId = Guid.NewGuid();
    private static readonly Guid MeganId = Guid.NewGuid();
    private static readonly Guid AlexId = Guid.NewGuid();

    /// <summary>Contoso and Contoso East, with Megan owned by Contoso and Alex by Contoso East.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage?> Directory(Func<HttpRequestMessage, HttpResponseMessage?>? writes = null) =>
        request => writes?.Invoke(request) ?? (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", id: ContosoId), Tenant("Contoso East", id: EastId))),
            ("GET", "/api/v1/users") => FakeHttp.Json(HttpStatusCode.OK, Page(User("Megan Bowen", ContosoId, MeganId), User("Alex Wilber", EastId, AlexId))),
            _ => null,
        };

    [TestMethod]
    public void Lists_users_with_their_owning_tenant()
    {
        Http.SignedInAs("Ada", Directory());

        var app = RenderApp("/users");

        app.WaitForAssertion(() => Assert.Contains("Megan Bowen", app.Markup));
        var rows = app.FindAll("tr").Select(r => r.TextContent).ToList();
        Assert.Contains("Contoso East", rows.Single(t => t.Contains("Alex Wilber")));
        Assert.IsTrue(Http.Requests.Any(r => r.PathAndQuery.StartsWith("/api/v1/users?page=1&pageSize=", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Add_posts_the_owning_tenant_entra_identity_and_name()
    {
        var entraTenantId = Guid.NewGuid();
        var entraObjectId = Guid.NewGuid();
        Http.SignedInAs("Ada", Directory(request => request.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.Created, User("Patti Fernandez", EastId))
            : null));
        var app = RenderApp("/users");

        app.WaitForElement("#add-user").Click();
        app.WaitForElement("fluent-dialog");
        Choose(app, "user-tenant", EastId);
        app.Find("#user-entra-tenant").Change(entraTenantId.ToString());
        app.Find("#user-entra-object").Change(entraObjectId.ToString());
        app.Find("#user-display-name").Change("Patti Fernandez");
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Post)));
        var post = Http.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.AreEqual("/api/v1/users", post.PathAndQuery);
        var sent = JsonSerializer.Deserialize<CreateUserRequest>(post.Body!, Signal9Json.Options)!;
        Assert.AreEqual(EastId, sent.TenantId);
        Assert.AreEqual(entraTenantId, sent.EntraTenantId);
        Assert.AreEqual(entraObjectId, sent.EntraObjectId);
        Assert.AreEqual("Patti Fernandez", sent.DisplayName);
        app.WaitForAssertion(() => Assert.Contains("Added Patti Fernandez.", app.Find(".notice-bar").TextContent));
    }

    [TestMethod]
    public void Edit_sends_the_display_name_as_a_merge_patch()
    {
        Http.SignedInAs("Ada", Directory(request => request.Method == HttpMethod.Patch
            ? FakeHttp.Json(HttpStatusCode.OK, User("Alex W. Wilber", EastId, AlexId))
            : null));
        var app = RenderApp("/users");

        app.WaitForElement($"#edit-{AlexId}").Click();
        app.WaitForElement("#user-display-name").Change("Alex W. Wilber");
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Patch)));
        var patch = Http.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.AreEqual($"/api/v1/users/{AlexId}", patch.PathAndQuery);
        Assert.AreEqual("application/merge-patch+json", patch.ContentType);
        Assert.AreEqual("Alex W. Wilber", JsonSerializer.Deserialize<UpdateUserRequest>(patch.Body!, Signal9Json.Options)!.DisplayName);
        app.WaitForAssertion(() => Assert.Contains("Saved Alex W. Wilber.", app.Find(".notice-bar").TextContent));
        Assert.Contains("Alex W. Wilber", app.Find("fluent-data-grid, table, [role=grid]").TextContent);
    }

    [TestMethod]
    public void A_refused_edit_shows_why()
    {
        Http.SignedInAs("Ada", Directory(request => request.Method == HttpMethod.Patch
            ? FakeHttp.Problem(HttpStatusCode.Forbidden, "User holds role assignments",
                "Editing a user who holds role assignments needs authority to assign roles at every tenant where they hold one.")
            : null));
        var app = RenderApp("/users");

        app.WaitForElement($"#edit-{MeganId}").Click();
        app.WaitForElement("#user-display-name").Change("Tampered");
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.Contains("authority to assign roles", app.Find(".error-bar").TextContent));
        Assert.DoesNotContain("Tampered", app.Find("fluent-data-grid, table, [role=grid]").TextContent);
    }

    private static void Choose(IRenderedComponent<OSI.Signal9.Web.Client.Routes> app, string selectId, Guid value)
    {
        var option = app.WaitForElement($"#{selectId} fluent-option[value='{value}']");
        app.Find($"#{selectId}").TriggerEvent("ondropdownchange", new DropdownEventArgs { Id = selectId, SelectedOptions = option.Id! });
    }
}
