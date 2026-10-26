using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.IntegrationTests;

[TestClass]
public sealed class OpenApiTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new(migrated: false);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Single_resource_gets_document_the_etag_if_none_match_and_304()
    {
        var get = (await DocumentAsync()).Paths["/api/v1/tenants/{tenantId}"]!.Operations![HttpMethod.Get];

        Assert.IsTrue(HasHeaderParameter(get, "If-None-Match"));
        Assert.IsTrue(get.Responses!["200"].Headers!.ContainsKey("ETag"));
        Assert.IsTrue(get.Responses["304"].Headers!.ContainsKey("ETag"));
    }

    [TestMethod]
    public async Task Writes_document_if_match_the_412_and_the_new_etag()
    {
        var tenant = (await DocumentAsync()).Paths["/api/v1/tenants/{tenantId}"]!.Operations!;
        var create = (await DocumentAsync()).Paths["/api/v1/tenants"]!.Operations![HttpMethod.Post];

        foreach (var write in new[] { tenant[HttpMethod.Patch], tenant[HttpMethod.Delete], create })
        {
            Assert.IsTrue(HasHeaderParameter(write, "If-Match"), write.OperationId);
            Assert.IsTrue(write.Responses!.ContainsKey("412"), write.OperationId);
        }
        Assert.IsTrue(tenant[HttpMethod.Patch].Responses!["200"].Headers!.ContainsKey("ETag"));
        Assert.IsTrue(create.Responses!["201"].Headers!.ContainsKey("ETag"));
        Assert.IsFalse(tenant[HttpMethod.Delete].Responses!["204"].Headers?.ContainsKey("ETag") == true);
    }

    [TestMethod]
    public async Task Agent_operations_document_the_version_header_and_its_refusal()
    {
        var document = await DocumentAsync();
        var heartbeat = document.Paths["/api/v1/agents/{agentId}/heartbeat"]!.Operations![HttpMethod.Put];
        var enroll = document.Paths["/api/v1/agents"]!.Operations![HttpMethod.Post];
        var tenant = document.Paths["/api/v1/tenants/{tenantId}"]!.Operations![HttpMethod.Get];

        foreach (var operation in new[] { heartbeat, enroll })
        {
            Assert.IsTrue(operation.Parameters!.Any(p => p.In == ParameterLocation.Header
                && p.Name == OSI.Signal9.Contracts.Agents.AgentProtocol.VersionHeader && p.Required), operation.OperationId);
            StringAssert.Contains(operation.Responses!["400"].Description,
                OSI.Signal9.Contracts.Agents.AgentProtocol.UnsupportedVersion.ProblemType, operation.OperationId);
        }
        Assert.IsFalse(HasHeaderParameter(tenant, OSI.Signal9.Contracts.Agents.AgentProtocol.VersionHeader));
    }

    [TestMethod]
    public async Task Collections_do_not_document_etags()
    {
        var list = (await DocumentAsync()).Paths["/api/v1/tenants"]!.Operations![HttpMethod.Get];

        Assert.IsFalse(HasHeaderParameter(list, "If-None-Match"));
        Assert.IsFalse(list.Responses!.ContainsKey("304"));
    }

    [TestMethod]
    public async Task Documented_requirements_appear_on_their_operations()
    {
        using var migrated = new Signal9ApiFactory();
        using var api = migrated.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddControllers().AddApplicationPart(typeof(RequirementProbeController).Assembly)));
        using var client = api.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>("docs/openapi/v1.json", TestContext.CancellationToken);

        var probe = document.GetProperty("paths").GetProperty("/test/requirement-probe").GetProperty("get");
        var requirement = probe.GetProperty(DocumentedRequirements.Extension).EnumerateArray().Single();
        Assert.AreEqual("test-step-up", requirement.GetProperty("name").GetString());
        Assert.AreEqual(RequireTestStepUpAttribute.Text, requirement.GetProperty("description").GetString());
        // People read the description; tools read the extension.
        StringAssert.Contains(probe.GetProperty("description").GetString(), RequireTestStepUpAttribute.Text);
        var list = document.GetProperty("paths").GetProperty("/api/v1/tenants").GetProperty("get");
        Assert.IsFalse(list.TryGetProperty(DocumentedRequirements.Extension, out _));
    }

    private Task<OpenApiDocument> DocumentAsync() =>
        _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1").GetOpenApiDocumentAsync(TestContext.CancellationToken);

    private static bool HasHeaderParameter(OpenApiOperation operation, string name) =>
        operation.Parameters?.Any(p => p.In == ParameterLocation.Header && p.Name == name) == true;

    public void Dispose() => _factory.Dispose();
}

/// <summary>
/// Stands in for the requirements #120 and #122 add (<c>[RequireRecentMfa]</c>, <c>[RequireAncestorAdministrator]</c>):
/// any authorization requirement that documents itself.
/// </summary>
public sealed class RequireTestStepUpAttribute : Attribute, IAuthorizationRequirementData, IAuthorizationRequirement,
    IDocumentedRequirement
{
    public const string Text = "Needs a test step-up within the last 15 minutes.";

    public string Name => "test-step-up";

    public string Description => Text;

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return this;
    }
}

[ApiController]
[Route("test/requirement-probe")]
public sealed class RequirementProbeController : ControllerBase
{
    [RequireScope(Scopes.Tenants.Read)]
    [RequireTestStepUp]
    [HttpGet]
    public IActionResult Get() => Ok();
}
