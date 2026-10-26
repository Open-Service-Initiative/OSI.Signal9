using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using OSI.Signal9.Auth;
using OSI.Signal9.Contracts;
using OSI.Signal9.Contracts.Invitations;
using OSI.Signal9.Contracts.Roles;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// What request telemetry exports: never a secret carried in a URL path, such as an invitation's reference, which
/// redeems the invitation for whoever holds it (#121).
/// </summary>
[TestClass]
public sealed class RequestTelemetryTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public RequestTelemetryTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    [TestMethod]
    public async Task Opening_and_redeeming_an_invitation_exports_no_trace_of_its_reference()
    {
        var exported = new List<Activity>();
        using var auth = _factory.Auth.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.ConfigureOpenTelemetryTracerProvider(tracing =>
                tracing.AddProcessor(new SimpleActivityExportProcessor(new CapturingExporter(exported))))));
        using var client = _factory.CreateClient();
        var tenant = await _factory.CreateTenantAsync(client, "Contoso");
        var roles = await client.GetFromJsonAsync<PagedResult<RoleDto>>("api/v1/roles", Signal9Json.Options, Ct);
        var created = await client.PostAsJsonAsync($"api/v1/tenants/{tenant.Id}/invitations",
            new CreateInvitationRequest { RoleId = roles!.Items.Single(r => r.Name == "Viewer").Id }, Signal9Json.Options, Ct);
        var link = new Uri((await created.Content.ReadFromJsonAsync<InvitationDto>(Signal9Json.Options, Ct))!.Link!);
        var reference = link.AbsolutePath.Split('/').Last();

        // Opening the link signs the person in at Entra first; then they see the offer and accept it.
        using var browser = new Browser(auth.Server);
        var page = await browser.SendAuthorizeAsync(link.AbsolutePath.TrimStart('/'), _factory.Entra, EntraUser.WithMfa());
        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode, await page.Content.ReadAsStringAsync(Ct));
        var accepted = await browser.PostAsync(new Uri(link.AbsolutePath, UriKind.Relative), new FormUrlEncodedContent([]));
        Assert.AreEqual(HttpStatusCode.BadRequest, accepted.StatusCode, "Without the page's antiforgery field.");

        lock (exported)
        {
            // Opening, the return from Entra and the post are still traced, with the path redacted.
            var invitationRequests = exported.Where(a => a.GetTagItem("http.route") is string route
                && route.Contains(OSI.Signal9.Server.Identity.InvitationReference.Path + "/", StringComparison.Ordinal)).ToList();
            Assert.IsGreaterThanOrEqualTo(2, invitationRequests.Count, string.Join(", ", exported.Select(a => a.DisplayName)));
            Assert.IsTrue(invitationRequests.All(a => (string?)a.GetTagItem("url.path") == "/invitations/{redacted}"));
            foreach (var activity in exported)
            {
                Assert.DoesNotContain(reference, activity.DisplayName);
                foreach (var (key, value) in activity.TagObjects)
                    Assert.DoesNotContain(reference, value?.ToString() ?? "", $"Tag {key} of {activity.DisplayName}");
            }
        }
    }

    private sealed class CapturingExporter(List<Activity> exported) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            lock (exported)
            {
                foreach (var activity in batch)
                    exported.Add(activity);
            }
            return ExportResult.Success;
        }
    }

    public void Dispose() => _factory.Dispose();
}
