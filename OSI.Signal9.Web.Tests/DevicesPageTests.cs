using System.Net;
using System.Text.Json;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Serialization;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.Contracts.Devices;
using OSI.Signal9.Web.Client.Services;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class DevicesPageTests : DashboardTestContext
{
    [TestMethod]
    public void Lists_the_first_page_of_devices_from_the_API()
    {
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath == "/api/v1/devices"
            ? FakeHttp.Json(HttpStatusCode.OK, Page(Device("WS-001"), Device("SRV-DB", DeviceStatus.Offline)))
            : null);

        var app = RenderApp("/devices");

        app.WaitForAssertion(() => Assert.Contains("SRV-DB", app.Markup));
        Assert.Contains("WS-001", app.Markup);
        Assert.IsTrue(Http.Requests.Any(r => r.PathAndQuery == "/api/v1/devices?page=1&pageSize=25"));
    }

    [TestMethod]
    public void Shows_each_devices_agent_status_apart_from_its_own_status()
    {
        var id = Guid.NewGuid();
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath == "/api/v1/devices"
            ? FakeHttp.Json(HttpStatusCode.OK, Page(Device("WS-001", id: id, agentStatus: Contracts.Agents.AgentStatus.AwaitingApproval)))
            : null);

        var app = RenderApp("/devices");

        app.WaitForAssertion(() => Assert.Contains("Awaiting approval", app.Find($"#agent-status-{id}").TextContent));
        Assert.Contains("Online", app.Find($"#status-{id}").TextContent);
    }

    [TestMethod]
    public void A_problem_response_is_shown_in_an_error_message_bar()
    {
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath == "/api/v1/devices"
            ? FakeHttp.Problem(HttpStatusCode.Forbidden, "Forbidden", "The token lacks devices.read.")
            : null);

        var app = RenderApp("/devices");

        var bar = app.WaitForElement(".error-bar");
        Assert.Contains("Forbidden", bar.TextContent);
        Assert.Contains("The token lacks devices.read.", bar.TextContent);
    }

    [TestMethod]
    public void A_401_from_the_API_means_the_session_ended_so_the_sign_in_prompt_replaces_the_page()
    {
        var signedIn = true;
        Http.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/bff/user" when signedIn => FakeHttp.Json(HttpStatusCode.OK, new Contracts.Bff.BffUser("s", "Ada")),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
        };
        var app = RenderApp("/");
        signedIn = false;

        Navigation.NavigateTo("/devices");

        app.WaitForElement("#sign-in-prompt");
        Assert.IsEmpty(app.FindAll(".error-bar"));
    }

    [TestMethod]
    public void A_401_while_web_still_holds_the_session_is_shown_as_an_error()
    {
        Http.SignedInAs("Ada", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var app = RenderApp("/devices");

        app.WaitForElement(".error-bar");
        Assert.IsEmpty(app.FindAll("#sign-in-prompt"));
    }

    [TestMethod]
    public void A_device_changed_on_the_hub_updates_its_row()
    {
        var id = Guid.NewGuid();
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath == "/api/v1/devices"
            ? FakeHttp.Json(HttpStatusCode.OK, Page(Device("WS-001", DeviceStatus.Online, id)))
            : null);
        var app = RenderApp("/devices");
        app.WaitForAssertion(() => Assert.Contains("WS-001", app.Markup));

        var hub = Services.GetRequiredService<DashboardHubConnection>();
        app.InvokeAsync(() => hub.DeviceChanged(Device("WS-001-RENAMED", DeviceStatus.Offline, id)));

        app.WaitForAssertion(() => Assert.Contains("WS-001-RENAMED", app.Markup));
    }

    [TestMethod]
    public void The_hub_being_unavailable_shows_a_status_instead_of_crashing()
    {
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath == "/api/v1/devices"
            ? FakeHttp.Json(HttpStatusCode.OK, Page(Device("WS-001")))
            : null);

        var app = RenderApp("/devices");

        app.WaitForAssertion(() => Assert.Contains("Live updates unavailable", app.Find("#live-status").TextContent));
        var negotiate = Http.Requests.First(r => r.Uri.AbsolutePath == "/hubs/dashboard/negotiate");
        Assert.AreEqual("1", negotiate.Header("X-CSRF"));
    }

    [TestMethod]
    public void Delete_asks_first_then_deletes_the_device()
    {
        var id = Guid.NewGuid();
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/devices") => FakeHttp.Json(HttpStatusCode.OK, Page(Device("WS-001", DeviceStatus.Online, id))),
            ("DELETE", _) => new HttpResponseMessage(HttpStatusCode.NoContent),
            _ => null,
        });
        var app = RenderApp("/devices");

        app.WaitForElement($"#delete-{id}").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        Assert.Contains("WS-001", dialog.TextContent);
        Assert.IsFalse(Http.Requests.Any(r => r.Method == HttpMethod.Delete), "Nothing is deleted before the person confirms.");

        dialog.QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Delete && r.PathAndQuery == $"/api/v1/devices/{id}")));
        app.WaitForAssertion(() => Assert.Contains("Deleted WS-001.", app.Find(".notice-bar").TextContent));
    }

    [TestMethod]
    public void Edit_sends_group_tags_and_maintenance_as_a_merge_patch()
    {
        var id = Guid.NewGuid();
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/devices") => FakeHttp.Json(HttpStatusCode.OK, Page(Device("WS-001", DeviceStatus.Online, id))),
            ("PATCH", _) => FakeHttp.Json(HttpStatusCode.OK, Device("WS-001", DeviceStatus.Online, id)),
            _ => null,
        });
        var app = RenderApp("/devices");

        app.WaitForElement($"#edit-{id}").Click();
        app.WaitForElement("#device-group").Change("Kiosks");
        app.Find("#device-tags").Change("lobby, , kiosk");
        app.WaitForElement("#device-maintenance").Change(true);
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Patch)));
        var patch = Http.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.AreEqual($"/api/v1/devices/{id}", patch.PathAndQuery);
        Assert.AreEqual("application/merge-patch+json", patch.ContentType);
        var sent = JsonSerializer.Deserialize<UpdateDeviceRequest>(patch.Body!, Signal9Json.Options)!;
        Assert.AreEqual("Kiosks", sent.GroupName);
        CollectionAssert.AreEqual(new[] { "lobby", "kiosk" }, sent.Tags.ToArray());
        Assert.IsTrue(sent.MaintenanceMode);
    }

    [TestMethod]
    public void Transfer_posts_the_chosen_tenant_to_the_transfer_action()
    {
        var id = Guid.NewGuid();
        var contoso = Tenant("Contoso");
        var fabrikam = Tenant("Fabrikam");
        var device = Device("WS-001", DeviceStatus.Online, id) with { TenantId = contoso.Id };
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/devices") => FakeHttp.Json(HttpStatusCode.OK, Page(device)),
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(contoso, fabrikam)),
            ("POST", var p) when p.EndsWith(":transfer") => FakeHttp.Json(HttpStatusCode.OK, device with { TenantId = fabrikam.Id }),
            _ => null,
        });
        var app = RenderApp("/devices");

        app.WaitForElement($"#transfer-{id}").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        Assert.Contains("WS-001", dialog.TextContent);
        // The device's own tenant isn't a destination.
        Assert.IsEmpty(app.FindAll($"#transfer-tenant fluent-option[value='{contoso.Id}']"));
        Choose(app, "transfer-tenant", fabrikam.Id);
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        var path = $"/api/v1/devices/{id}:transfer";
        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r => r.Method == HttpMethod.Post && r.PathAndQuery == path)));
        var post = Http.Requests.Single(r => r.Method == HttpMethod.Post && r.PathAndQuery == path);
        Assert.AreEqual(fabrikam.Id, JsonSerializer.Deserialize<TransferDeviceRequest>(post.Body!, Signal9Json.Options)!.TenantId);
        app.WaitForAssertion(() => Assert.Contains("Transferred WS-001 to Fabrikam.", app.Find(".notice-bar").TextContent));
    }

    [TestMethod]
    public void A_refused_transfer_shows_the_problem()
    {
        var id = Guid.NewGuid();
        var fabrikam = Tenant("Fabrikam");
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/devices") => FakeHttp.Json(HttpStatusCode.OK, Page(Device("WS-001", DeviceStatus.Online, id))),
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(fabrikam)),
            ("POST", var p) when p.EndsWith(":transfer") => FakeHttp.Problem(HttpStatusCode.Conflict, "Machine already in that tenant tree",
                "The destination's root tenant tree already holds a device for the same hardware."),
            _ => null,
        });
        var app = RenderApp("/devices");

        app.WaitForElement($"#transfer-{id}").Click();
        app.WaitForElement("fluent-dialog");
        Choose(app, "transfer-tenant", fabrikam.Id);
        app.Find("fluent-dialog fluent-button[appearance=primary]").Click();

        app.WaitForAssertion(() => Assert.Contains("already holds a device for the same hardware", app.Find(".error-bar").TextContent));
    }

    [TestMethod]
    public void Collect_system_info_queues_the_command()
    {
        var id = Guid.NewGuid();
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/devices") => FakeHttp.Json(HttpStatusCode.OK, Page(Device("WS-001", DeviceStatus.Online, id))),
            ("POST", _) => FakeHttp.Json(HttpStatusCode.Created, new { }),
            _ => null,
        });
        var app = RenderApp("/devices");

        app.WaitForElement($"#collect-{id}").Click();

        app.WaitForAssertion(() => Assert.Contains("Queued", app.Find(".notice-bar").TextContent));
        var post = Http.Requests.Single(r => r.Method == HttpMethod.Post && r.PathAndQuery == $"/api/v1/devices/{id}/commands");
        Assert.AreEqual(CommandType.CollectSystemInfo,
            JsonSerializer.Deserialize<CreateCommandRequest>(post.Body!, Signal9Json.Options)!.Type);
    }

    [TestMethod]
    public void Filters_ask_the_API_for_matching_devices_from_the_first_page()
    {
        Http.SignedInAs("Ada", request => request.RequestUri!.AbsolutePath == "/api/v1/devices"
            ? FakeHttp.Json(HttpStatusCode.OK, Page(Device("WS-001")))
            : null);
        var app = RenderApp("/devices");
        app.WaitForAssertion(() => Assert.Contains("WS-001", app.Markup));

        app.Find("#device-search").Change("10.0 0");
        var offline = app.Find("#device-status fluent-option[value=Offline]");
        app.Find("#device-status").TriggerEvent("ondropdownchange",
            new Microsoft.FluentUI.AspNetCore.Components.DropdownEventArgs { Id = "device-status", SelectedOptions = offline.Id! });
        app.Find("form.filters").Submit();

        app.WaitForAssertion(() => Assert.IsTrue(Http.Requests.Any(r =>
            r.PathAndQuery == "/api/v1/devices?page=1&pageSize=25&status=Offline&search=10.0%200")));
    }

    private static void Choose(Bunit.IRenderedComponent<OSI.Signal9.Web.Client.Routes> app, string selectId, Guid value)
    {
        var option = app.WaitForElement($"#{selectId} fluent-option[value='{value}']");
        app.Find($"#{selectId}").TriggerEvent("ondropdownchange",
            new Microsoft.FluentUI.AspNetCore.Components.DropdownEventArgs { Id = selectId, SelectedOptions = option.Id! });
    }
}
