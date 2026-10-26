using System.Net;
using System.Text.Json;
using Microsoft.FluentUI.AspNetCore.Components;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Contracts.Tenants;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class TenantTransfersPageTests : DashboardTestContext
{
    private static readonly Guid ContosoId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid FabrikamId = Guid.NewGuid();

    private static readonly TenantTransferDto OutgoingPending = Transfer("Contoso Client", "Fabrikam", TenantTransferStatus.Pending);
    private static readonly TenantTransferDto OutgoingAccepted = Transfer("Contoso Site", "Northwind", TenantTransferStatus.Accepted);
    private static readonly TenantTransferDto IncomingPending = Transfer("Litware", "Contoso", TenantTransferStatus.Pending);
    private static readonly TenantTransferDto IncomingDeclined = Transfer("Adatum", "Contoso", TenantTransferStatus.Declined);

    /// <summary>Contoso and its client, with two transfers out of Contoso and two into it.</summary>
    private Func<HttpRequestMessage, HttpResponseMessage?> Transfers(Func<HttpRequestMessage, HttpResponseMessage?>? writes = null) =>
        request => writes?.Invoke(request) ?? (request.Method.Method, request.RequestUri!.PathAndQuery) switch
        {
            ("GET", var path) when path.StartsWith("/api/v1/tenants?") =>
                FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", id: ContosoId), Tenant("Contoso Client", id: ClientId))),
            ("GET", var path) when path.StartsWith($"/api/v1/tenants/{ContosoId}/transfers?direction=Outgoing") =>
                FakeHttp.Json(HttpStatusCode.OK, Page(OutgoingPending, OutgoingAccepted)),
            ("GET", var path) when path.StartsWith($"/api/v1/tenants/{ContosoId}/transfers?direction=Incoming") =>
                FakeHttp.Json(HttpStatusCode.OK, Page(IncomingPending, IncomingDeclined)),
            ("GET", var path) when path.StartsWith($"/api/v1/tenants/{ClientId}/transfers?") =>
                FakeHttp.Json(HttpStatusCode.OK, Page<TenantTransferDto>()),
            _ => null,
        };

    [TestMethod]
    public void Lists_pending_accepted_and_declined_transfers_on_both_sides()
    {
        Http.SignedInAs("Ada", Transfers());

        var app = RenderApp("/tenant-transfers");

        app.WaitForAssertion(() => Assert.Contains("Litware", app.Find("#incoming-transfers").TextContent));
        var outgoing = app.Find("#outgoing-transfers").TextContent;
        Assert.Contains("Contoso Client", outgoing);
        Assert.Contains("Fabrikam", outgoing);
        Assert.Contains("Pending", outgoing);
        Assert.Contains("Accepted", outgoing);
        var incoming = app.Find("#incoming-transfers").TextContent;
        Assert.Contains("Declined", incoming);
        Assert.DoesNotContain("Contoso Client", incoming);
    }

    [TestMethod]
    public void Accept_asks_first_then_accepts_the_incoming_transfer()
    {
        Http.SignedInAs("Ada", Transfers(request => request.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.OK, IncomingPending with { Status = TenantTransferStatus.Accepted })
            : null));
        var app = RenderApp("/tenant-transfers");

        app.WaitForElement($"#accept-{IncomingPending.Id}").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        Assert.Contains("Litware", dialog.TextContent);
        Assert.IsFalse(Http.Requests.Any(r => r.Method == HttpMethod.Post), "Nothing is accepted before the person confirms.");

        dialog.QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Post
            && r.PathAndQuery == $"/api/v1/tenants/{IncomingPending.TenantId}/transfers/{IncomingPending.Id}:accept")));
        app.WaitForAssertion(() => Assert.Contains("Accepted the transfer of Litware.", app.Find(".notice-bar").TextContent));
    }

    [TestMethod]
    public void Decline_and_cancel_act_on_pending_transfers_only()
    {
        Http.SignedInAs("Ada", Transfers(request => request.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.OK, IncomingPending with { Status = TenantTransferStatus.Declined })
            : null));
        var app = RenderApp("/tenant-transfers");

        app.WaitForElement($"#decline-{IncomingPending.Id}").Click();
        app.WaitForElement("fluent-dialog").QuerySelector("fluent-button[appearance=primary]")!.Click();
        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r =>
            r.PathAndQuery == $"/api/v1/tenants/{IncomingPending.TenantId}/transfers/{IncomingPending.Id}:decline")));

        app.WaitForElement($"#cancel-{OutgoingPending.Id}").Click();
        app.WaitForElement("fluent-dialog").QuerySelector("fluent-button[appearance=primary]")!.Click();
        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r =>
            r.PathAndQuery == $"/api/v1/tenants/{OutgoingPending.TenantId}/transfers/{OutgoingPending.Id}:cancel")));

        Assert.IsEmpty(app.FindAll($"#accept-{IncomingDeclined.Id}, #decline-{IncomingDeclined.Id}, #cancel-{OutgoingAccepted.Id}"));
        Assert.IsEmpty(app.FindAll($"#accept-{OutgoingPending.Id}, #cancel-{IncomingPending.Id}"));
    }

    [TestMethod]
    public void Propose_posts_the_chosen_tenant_and_new_parent()
    {
        Http.SignedInAs("Ada", Transfers(request => request.Method == HttpMethod.Post
            ? FakeHttp.Json(HttpStatusCode.Created, OutgoingPending)
            : null));
        var app = RenderApp("/tenant-transfers");

        app.WaitForElement("#propose-transfer").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        var option = app.WaitForElement($"#transfer-tenant fluent-option[value='{ClientId}']");
        app.Find("#transfer-tenant").TriggerEvent("ondropdownchange",
            new DropdownEventArgs { Id = "transfer-tenant", SelectedOptions = option.Id! });
        app.Find("#transfer-new-parent").Change(FabrikamId.ToString());
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Post)));
        var post = Http.Requests.Single(r => r.Method == HttpMethod.Post);
        Assert.AreEqual($"/api/v1/tenants/{ClientId}/transfers", post.PathAndQuery);
        Assert.AreEqual(FabrikamId, JsonSerializer.Deserialize<ProposeTenantTransferRequest>(post.Body!, Signal9Json.Options)!.ToParentTenantId);
        app.WaitForAssertion(() => Assert.Contains("Proposed transferring Contoso Client to Fabrikam.", app.Find(".notice-bar").TextContent));
    }

    [TestMethod]
    public void A_refused_acceptance_shows_why()
    {
        Http.SignedInAs("Ada", Transfers(request => request.Method == HttpMethod.Post
            ? FakeHttp.Problem(HttpStatusCode.Conflict, "Duplicate devices", "Delete or transfer one of each pair first.")
            : null));
        var app = RenderApp("/tenant-transfers");

        app.WaitForElement($"#accept-{IncomingPending.Id}").Click();
        app.WaitForElement("fluent-dialog").QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.Contains("Delete or transfer one of each pair first.", app.Find(".error-bar").TextContent));
    }

    private static TenantTransferDto Transfer(string tenantName, string toParentName, TenantTransferStatus status) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tenantName, Guid.NewGuid(), Guid.NewGuid(), toParentName, status, Now,
            status == TenantTransferStatus.Pending ? null : Now);
}
