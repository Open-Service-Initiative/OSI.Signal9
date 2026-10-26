using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.IntegrationTests;

[TestClass]
public sealed class OpenApiTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Single_resource_gets_document_the_etag_if_none_match_and_304()
    {
        var document = await DocumentAsync();
        var get = Operation(document, "/api/v1/tenants/{tenantId}", "get");

        Assert.IsTrue(HasHeaderParameter(document, get, "If-None-Match"));
        Assert.IsTrue(HasResponseHeader(get, "200", "ETag"));
        Assert.IsTrue(HasResponseHeader(get, "304", "ETag"));
    }

    [TestMethod]
    public async Task Writes_document_if_match_the_412_and_the_new_etag()
    {
        var document = await DocumentAsync();
        var patch = Operation(document, "/api/v1/tenants/{tenantId}", "patch");
        var delete = Operation(document, "/api/v1/tenants/{tenantId}", "delete");
        var create = Operation(document, "/api/v1/tenants", "post");

        foreach (var (name, write) in new[] { ("PATCH", patch), ("DELETE", delete), ("POST", create) })
        {
            Assert.IsTrue(HasHeaderParameter(document, write, "If-Match"), name);
            Assert.IsTrue(write.GetProperty("responses").TryGetProperty("412", out _), name);
        }
        Assert.IsTrue(HasResponseHeader(patch, "200", "ETag"));
        Assert.IsTrue(HasResponseHeader(create, "201", "ETag"));
        Assert.IsFalse(HasResponseHeader(delete, "204", "ETag"));
    }

    [TestMethod]
    public async Task Agent_operations_document_the_version_header_and_its_refusal()
    {
        var document = await DocumentAsync();
        var heartbeat = Operation(document, "/api/v1/agents/{agentId}/heartbeat", "put");
        var enroll = Operation(document, "/api/v1/agents", "post");
        var tenant = Operation(document, "/api/v1/tenants/{tenantId}", "get");

        foreach (var (name, operation) in new[] { ("heartbeat", heartbeat), ("enroll", enroll) })
        {
            Assert.IsTrue(operation.GetProperty("parameters").EnumerateArray().Select(p => Resolve(document, p))
                .Any(p => p.GetProperty("in").GetString() == "header"
                    && p.GetProperty("name").GetString() == OSI.Signal9.Contracts.Agents.AgentProtocol.VersionHeader
                    && p.TryGetProperty("required", out var required) && required.GetBoolean()), name);
            StringAssert.Contains(operation.GetProperty("responses").GetProperty("400").GetProperty("description").GetString(),
                OSI.Signal9.Contracts.Agents.AgentProtocol.UnsupportedVersion.ProblemType, name);
        }
        Assert.IsFalse(HasHeaderParameter(document, tenant, OSI.Signal9.Contracts.Agents.AgentProtocol.VersionHeader));
    }

    [TestMethod]
    public async Task Collections_do_not_document_etags()
    {
        var document = await DocumentAsync();
        var list = Operation(document, "/api/v1/tenants", "get");

        Assert.IsFalse(HasHeaderParameter(document, list, "If-None-Match"));
        Assert.IsFalse(list.GetProperty("responses").TryGetProperty("304", out _));
    }

    [TestMethod]
    public async Task Documented_requirements_appear_on_their_operations()
    {
        using var api = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
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

    [TestMethod]
    public async Task The_contract_offers_no_scaffold_fields_that_nothing_reads()
    {
        var schemas = (await DocumentAsync()).GetProperty("components").GetProperty("schemas");

        Assert.IsFalse(schemas.TryGetProperty("CommandPriority", out _));
        Assert.IsFalse(schemas.TryGetProperty("SubscriptionPlan", out _));
        foreach (var (schema, property) in new[]
        {
            ("CommandDto", "priority"), ("CreateCommandRequest", "priority"), ("TenantDto", "plan"),
            ("CreateTenantRequest", "plan"), ("DeviceDto", "maintenanceMode"),
        })
            Assert.IsFalse(schemas.GetProperty(schema).GetProperty("properties").TryGetProperty(property, out _), $"{schema}.{property}");
    }

    [TestMethod]
    public async Task Enums_are_documented_as_the_strings_the_api_sends()
    {
        var document = await DocumentAsync();

        var commandType = document.GetProperty("components").GetProperty("schemas").GetProperty("CommandType");
        Assert.IsTrue(commandType.TryGetProperty("enum", out var values), commandType.ToString());
        Assert.IsTrue(values.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String), commandType.ToString());
        CollectionAssert.Contains(values.EnumerateArray().Select(v => v.GetString()).ToList(), "CollectSystemInfo");
        if (commandType.TryGetProperty("type", out var type))
            Assert.AreEqual("string", type.GetString(), commandType.ToString());
    }

    /// <summary>The OpenAPI document api publishes, as anyone fetches it.</summary>
    private async Task<JsonElement> DocumentAsync()
    {
        using var client = _factory.CreateAnonymousClient();
        return await client.GetFromJsonAsync<JsonElement>("docs/openapi/v1.json", TestContext.CancellationToken);
    }

    private static JsonElement Operation(JsonElement document, string path, string method) =>
        document.GetProperty("paths").GetProperty(path).GetProperty(method);

    private static bool HasHeaderParameter(JsonElement document, JsonElement operation, string name) =>
        operation.TryGetProperty("parameters", out var parameters)
        && parameters.EnumerateArray().Select(p => Resolve(document, p))
            .Any(p => p.GetProperty("in").GetString() == "header" && p.GetProperty("name").GetString() == name);

    private static bool HasResponseHeader(JsonElement operation, string status, string header) =>
        operation.GetProperty("responses").GetProperty(status).TryGetProperty("headers", out var headers)
        && headers.TryGetProperty(header, out _);

    /// <summary>A component the document refers to (<c>$ref</c>), or the element itself.</summary>
    private static JsonElement Resolve(JsonElement document, JsonElement element)
    {
        if (!element.TryGetProperty("$ref", out var reference))
            return element;
        var resolved = document;
        foreach (var segment in reference.GetString()!.TrimStart('#', '/').Split('/'))
            resolved = resolved.GetProperty(segment);
        return resolved;
    }

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
