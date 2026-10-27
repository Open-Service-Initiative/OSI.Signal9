using System.Net;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Contracts.Tenants;
using static OSI.Signal9.Web.Tests.Samples;

namespace OSI.Signal9.Web.Tests;

[TestClass]
public sealed class AgentApprovalsPageTests : DashboardTestContext
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _agentId = Guid.NewGuid();

    private string Collection => $"/api/v1/tenants/{_tenantId}/agents";

    /// <summary>Signed in, with one tenant holding one agent awaiting approval; <paramref name="change"/> answers the rest.</summary>
    private void Serve(Func<HttpRequestMessage, HttpResponseMessage?>? change = null) =>
        Http.SignedInAs("Ada", request => (request.Method.Method, request.RequestUri!.AbsolutePath) switch
        {
            ("GET", "/api/v1/tenants") => FakeHttp.Json(HttpStatusCode.OK, Page(Tenant("Contoso", TenantStatus.Active, _tenantId))),
            ("GET", var path) when path == Collection => FakeHttp.Json(HttpStatusCode.OK, Page(Agent("PC02", _tenantId, id: _agentId))),
            _ => change?.Invoke(request),
        });

    [TestMethod]
    public void Lists_the_agents_awaiting_approval_in_the_first_tenant_and_its_descendants()
    {
        Serve();

        var app = RenderApp("/agent-approvals");

        app.WaitForAssertion(() => Assert.Contains("PC02", app.Markup));
        Assert.IsTrue(Http.Requests.Any(r => r.PathAndQuery == $"{Collection}?status=AwaitingApproval&page=1&pageSize=25"));
        Assert.Contains("Contoso", app.Find($"#tenant-{_agentId}").TextContent);
    }

    [TestMethod]
    public void Approving_asks_first_then_removes_the_agent_from_the_list()
    {
        Serve(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == $"/api/v1/agents/{_agentId}:approve"
            ? FakeHttp.Json(HttpStatusCode.OK, Agent("PC02", _tenantId, AgentStatus.Active, _agentId))
            : null);
        var app = RenderApp("/agent-approvals");

        app.WaitForElement($"#approve-{_agentId}").Click();
        var dialog = app.WaitForElement("fluent-dialog");
        Assert.IsFalse(Http.Requests.Any(r => r.Method == HttpMethod.Post), "Nothing changes before the person confirms.");
        dialog.QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.IsEmpty(app.FindAll($"#approve-{_agentId}")));
        Assert.Contains("Approved PC02.", app.Find(".notice-bar").TextContent);
    }

    [TestMethod]
    public void A_refused_approval_shows_the_problem()
    {
        Serve(request => request.Method == HttpMethod.Post
            ? FakeHttp.Problem(HttpStatusCode.Conflict, "Transition not allowed", "The agent is Revoked, not awaiting approval.")
            : null);
        var app = RenderApp("/agent-approvals");

        app.WaitForElement($"#approve-{_agentId}").Click();
        app.WaitForElement("fluent-dialog").QuerySelector("fluent-button[appearance=primary]")!.Click();

        app.WaitForAssertion(() => Assert.Contains("not awaiting approval", app.Markup));
        Assert.IsNotEmpty(app.FindAll($"#approve-{_agentId}"));
    }
}
