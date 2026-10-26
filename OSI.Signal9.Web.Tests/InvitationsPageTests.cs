using System.Net;
using System.Text.Json;
using Microsoft.FluentUI.AspNetCore.Components;
using OSI.Signal9.Contracts.Invitations;
using OSI.Signal9.Contracts.Serialization;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class InvitationsPageTests : DashboardTestContext
{
    private static readonly Guid ContosoId = Guid.NewGuid();
    private static readonly Guid TechnicianId = Guid.NewGuid();
    private static readonly Guid ViewerId = Guid.NewGuid();
    private static readonly Guid MeganId = Guid.NewGuid();
    private static readonly Guid PendingId = Guid.NewGuid();
    private static readonly Guid PinnedDirectory = Guid.NewGuid();

    private static string Collection => $"/api/v1/tenants/{ContosoId}/invitations";

    /// <summary>Signed in, with Contoso holding one pending invitation for Megan; <paramref name="writes"/> answers the rest.</summary>
    private void Serve(Func<HttpRequestMessage, HttpResponseMessage?>? writes = null) =>
        Http.SignedInAs("Ada", request => writes?.Invoke(request) ?? (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", id: ContosoId))),
            ("GET", "/api/v1/users") => FakeHttp.Json(HttpStatusCode.OK, Page(User("Megan Bowen", ContosoId, MeganId))),
            ("GET", "/api/v1/roles") => FakeHttp.Json(HttpStatusCode.OK, Page(Role("Technician", TechnicianId), Role("Viewer", ViewerId))),
            ("GET", var path) when path == Collection =>
                FakeHttp.Json(HttpStatusCode.OK, Page(Invitation(ContosoId, ViewerId, PendingId, userId: MeganId))),
            _ => null,
        });

    [TestMethod]
    public void Lists_the_invitations_of_the_first_tenant_by_role_and_person()
    {
        Serve();

        var app = RenderApp("/invitations");

        app.WaitForAssertion(() => Assert.Contains("Viewer", app.Find($"#role-{PendingId}").TextContent));
        Assert.Contains("Megan Bowen", app.Find($"#for-{PendingId}").TextContent);
        Assert.Contains("Pending", app.Find($"#status-{PendingId}").TextContent);
        Assert.IsTrue(Http.Requests.Any(r => r.PathAndQuery == $"{Collection}?page=1&pageSize=25"));
    }

    [TestMethod]
    public void Inviting_posts_one_role_and_the_optional_pin_then_shows_the_link_once_with_a_copy_button()
    {
        const string link = "https://auth.signal9.test/invitations/s9i_new-reference";
        Serve(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == Collection
            ? FakeHttp.Json(HttpStatusCode.Created, Invitation(ContosoId, TechnicianId, entraTenantId: PinnedDirectory, link: link))
            : null);
        var app = RenderApp("/invitations");

        app.WaitForElement("#create-invitation").Click();
        app.WaitForElement("fluent-dialog");
        Choose(app, "invitation-role", TechnicianId);
        app.Find("#invitation-directory").Change(PinnedDirectory.ToString());
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Post)));
        var sent = JsonSerializer.Deserialize<CreateInvitationRequest>(
            Http.Requests.Single(r => r.Method == HttpMethod.Post).Body!, Signal9Json.Options)!;
        Assert.AreEqual(TechnicianId, sent.RoleId);
        Assert.AreEqual(PinnedDirectory, sent.EntraTenantId);
        Assert.IsNull(sent.UserId, "Anyone may redeem it unless a user is chosen.");
        app.WaitForAssertion(() => Assert.Contains(link, app.Find("#invitation-link").OuterHtml));
        Assert.Contains("won't be shown again", app.Find(".secret-bar").TextContent);

        app.Find("#copy-link").Click();
        Assert.AreEqual(link, JSInterop.Invocations.Single(i => i.Identifier == "navigator.clipboard.writeText").Arguments.Single());
    }

    [TestMethod]
    public void Inviting_an_existing_user_sends_their_id()
    {
        Serve(request => request.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.Created, Invitation(ContosoId, ViewerId, userId: MeganId, link: "https://auth.signal9.test/invitations/s9i_x"))
            : null);
        var app = RenderApp("/invitations");

        app.WaitForElement("#create-invitation").Click();
        app.WaitForElement("fluent-dialog");
        Choose(app, "invitation-role", ViewerId);
        Choose(app, "invitation-user", MeganId);
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Post)));
        var sent = JsonSerializer.Deserialize<CreateInvitationRequest>(
            Http.Requests.Single(r => r.Method == HttpMethod.Post).Body!, Signal9Json.Options)!;
        Assert.AreEqual(MeganId, sent.UserId);
        Assert.IsNull(sent.EntraTenantId);
    }

    [TestMethod]
    public void A_directory_pin_that_is_not_a_directory_id_is_refused_rather_than_dropped()
    {
        Serve();
        var app = RenderApp("/invitations");

        app.WaitForElement("#create-invitation").Click();
        app.WaitForElement("fluent-dialog");
        Choose(app, "invitation-role", ViewerId);
        app.Find("#invitation-directory").Change("contoso.onmicrosoft.com");
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForElement("#invitation-directory-error");
        Assert.IsFalse(Http.Requests.Any(r => r.Method == HttpMethod.Post), "An unpinned invitation would let anyone in.");
    }

    [TestMethod]
    public void Revoking_asks_first_then_shows_the_invitation_as_revoked()
    {
        Serve(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == $"{Collection}/{PendingId}:revoke"
            ? FakeHttp.Json(HttpStatusCode.OK, Invitation(ContosoId, ViewerId, PendingId, InvitationStatus.Revoked, userId: MeganId))
            : null);
        var app = RenderApp("/invitations");

        app.WaitForElement($"#revoke-{PendingId}").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        Assert.IsFalse(Http.Requests.Any(r => r.Method == HttpMethod.Post), "Nothing changes before the person confirms.");
        dialog.QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.Contains("Revoked", app.Find($"#status-{PendingId}").TextContent));
        Assert.IsEmpty(app.FindAll($"#revoke-{PendingId}"));
    }

    [TestMethod]
    public void A_refusal_is_shown_in_an_error_message_bar()
    {
        Serve(request => request.Method == HttpMethod.Post
            ? FakeHttp.Problem(HttpStatusCode.Forbidden, "Role higher than your own", "You can offer only roles within your own access.")
            : null);
        var app = RenderApp("/invitations");

        app.WaitForElement("#create-invitation").Click();
        app.WaitForElement("fluent-dialog");
        Choose(app, "invitation-role", TechnicianId);
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        Assert.Contains("Role higher than your own", app.WaitForElement(".error-bar").TextContent);
        Assert.IsEmpty(app.FindAll("#invitation-link"));
    }

    private static void Choose(IRenderedComponent<OSI.Signal9.Web.Client.Routes> app, string selectId, Guid value)
    {
        var option = app.WaitForElement($"#{selectId} fluent-option[value='{value}']");
        app.Find($"#{selectId}").TriggerEvent("ondropdownchange", new DropdownEventArgs { Id = selectId, SelectedOptions = option.Id! });
    }
}
