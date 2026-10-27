using System.Net;
using System.Text.Json;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Tenants;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

/// <summary>The Needs-decision queue (#131, story 105): ambiguous enrollments a person resolves as Reinstalled or Keep both.</summary>
[TestClass]
public sealed class NeedsDecisionPageTests : DashboardTestContext
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();
    private readonly Guid _deviceId = Guid.NewGuid();

    private string Collection => $"/api/v1/tenants/{_tenantId}/agents";

    private string AgentUrl => $"/api/v1/agents/{_agentId}";

    private AgentDto Pending => new(_agentId, AgentUrl, null, _tenantId, "PC01-NEW", Guid.NewGuid(), "1.0.0", false,
        AgentStatus.NeedsDecision, Now, Now, null);

    /// <summary>Signed in, with one tenant and one agent needing a decision; <paramref name="change"/> answers the rest.</summary>
    private void Serve(Func<HttpRequestMessage, HttpResponseMessage?>? change = null) =>
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", TenantStatus.Active, _tenantId))),
            ("GET", var path) when path == Collection => FakeHttp.Json(HttpStatusCode.OK, Page(Pending)),
            ("GET", var path) when path == $"{AgentUrl}/candidate-devices" =>
                FakeHttp.Json(HttpStatusCode.OK, Page(Device("PC01", id: _deviceId))),
            _ => change?.Invoke(request),
        });

    [TestMethod]
    public void Lists_the_agents_needing_a_decision_in_the_first_tenant_and_its_descendants()
    {
        Serve();

        var app = RenderApp("/needs-decision");

        app.WaitForAssertion(() => Assert.Contains("PC01-NEW", app.Markup));
        Assert.IsTrue(Http.Requests.Any(r => r.PathAndQuery == $"{Collection}?status=NeedsDecision&page=1&pageSize=25"));
        Assert.Contains("Contoso", app.Find($"#tenant-{_agentId}").TextContent);
    }

    [TestMethod]
    public void Reviewing_an_agent_shows_the_devices_it_might_be_a_reinstall_of()
    {
        Serve();
        var app = RenderApp("/needs-decision");

        app.WaitForElement($"#review-{_agentId}").Click();

        app.WaitForElement($"#reinstalled-{_deviceId}");
        Assert.Contains("PC01", app.Find("#candidates").TextContent);
    }

    [TestMethod]
    public void Reinstalled_asks_first_then_rebinds_the_agent_to_the_device()
    {
        Serve(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == $"{AgentUrl}:identify"
            ? FakeHttp.Json(HttpStatusCode.OK, Pending with { DeviceId = _deviceId, Status = AgentStatus.Active, IsActive = true })
            : null);
        var app = RenderApp("/needs-decision");
        app.WaitForElement($"#review-{_agentId}").Click();

        app.WaitForElement($"#reinstalled-{_deviceId}").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        Assert.IsFalse(Http.Requests.Any(r => r.Method == HttpMethod.Post), "Nothing changes before the person confirms.");
        dialog.QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.IsEmpty(app.FindAll($"#review-{_agentId}")));
        var body = JsonDocument.Parse(Http.Requests.Single(r => r.Method == HttpMethod.Post).Body!).RootElement;
        Assert.AreEqual("Reinstalled", body.GetProperty("decision").GetString());
        Assert.AreEqual(_deviceId, body.GetProperty("deviceId").GetGuid());
        Assert.Contains("PC01-NEW", app.Find(".notice-bar").TextContent);
    }

    [TestMethod]
    public void Keep_both_asks_first_then_gives_the_agent_a_new_device()
    {
        Serve(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == $"{AgentUrl}:identify"
            ? FakeHttp.Json(HttpStatusCode.OK, Pending with { DeviceId = Guid.NewGuid(), Status = AgentStatus.Active, IsActive = true })
            : null);
        var app = RenderApp("/needs-decision");
        app.WaitForElement($"#review-{_agentId}").Click();

        app.WaitForElement($"#keep-both-{_agentId}").Click();
        app.WaitForElement("fluent-dialog").QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.IsEmpty(app.FindAll($"#review-{_agentId}")));
        var body = JsonDocument.Parse(Http.Requests.Single(r => r.Method == HttpMethod.Post).Body!).RootElement;
        Assert.AreEqual("KeepBoth", body.GetProperty("decision").GetString());
        Assert.IsFalse(body.TryGetProperty("deviceId", out var device) && device.ValueKind != JsonValueKind.Null);
    }

    [TestMethod]
    public void A_refused_decision_shows_the_problem_and_keeps_the_agent_queued()
    {
        Serve(request => request.Method == HttpMethod.Post
            ? FakeHttp.Problem(HttpStatusCode.Conflict, "Device limit reached", "Tenant 'Contoso' allows at most 1 devices.")
            : null);
        var app = RenderApp("/needs-decision");
        app.WaitForElement($"#review-{_agentId}").Click();

        app.WaitForElement($"#keep-both-{_agentId}").Click();
        app.WaitForElement("fluent-dialog").QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.Contains("allows at most 1 devices", app.Markup));
        Assert.IsNotEmpty(app.FindAll($"#review-{_agentId}"));
    }
}
