using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OSI.Signal9.API.Authorization;
using OSI.Signal9.Contracts.Commands;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.IntegrationTests;

/// <summary>The scope catalogue and the scope every endpoint requires (#63, ADR-0010).</summary>
[TestClass]
public sealed class ScopeTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public ScopeTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    private const string AnyId = "0193a6c4-0000-7000-8000-000000000001";

    /// <summary>#63's scope table, with <c>users.transfer</c> (#93) and <c>deployment.*</c> as #76 names it.</summary>
    private static readonly string[] SpecScopes =
    [
        "tenants.read", "tenants.write", "tenants.delete", "tenants.transfer",
        "devices.read", "devices.write", "devices.delete", "devices.transfer",
        "commands.read", "commands.execute",
        "scripts.read", "scripts.write", "scripts.delete", "scripts.execute",
        "terminal.execute",
        "telemetry.read",
        "enrollment.read", "enrollment.write",
        "users.read", "users.write", "users.transfer",
        "roleassignments.read", "roleassignments.write",
        "roles.read", "roles.write",
        "audit.read",
        "apiapplications.read", "apiapplications.write",
        "integrations.read", "integrations.write",
        "deployment.read", "deployment.write",
        "openid", "profile", "offline_access",
        "agent",
    ];

    [TestMethod]
    public async Task Discovery_offers_exactly_the_scope_catalogue()
    {
        using var auth = _factory.Auth.CreateClient();

        var discovery = await auth.GetFromJsonAsync<JsonElement>(".well-known/openid-configuration", Ct);

        var offered = discovery.GetProperty("scopes_supported").EnumerateArray().Select(s => s.GetString()!).ToList();
        CollectionAssert.AreEquivalent(SpecScopes, offered);
        CollectionAssert.AreEquivalent(SpecScopes, Scopes.All.Keys.ToList());
    }

    [TestMethod]
    public void Every_scope_uses_one_of_the_five_verbs_or_is_special_purpose()
    {
        string[] verbs = ["read", "write", "delete", "transfer", "execute"];
        string[] special = ["openid", "profile", "offline_access", "agent"];

        foreach (var (scope, description) in Scopes.All)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(description), scope);
            if (special.Contains(scope))
                continue;
            var parts = scope.Split('.');
            Assert.HasCount(2, parts, scope);
            CollectionAssert.Contains(verbs, parts[1], scope);
        }
    }

    [TestMethod]
    public async Task A_scope_outside_the_catalogue_is_refused()
    {
        await _factory.SeedClientAsync("greedy-app", "tenants.admin");
        using var auth = _factory.Auth.CreateClient();

        var response = await auth.PostAsync("connect/token", Signal9ApiFactory.ClientCredentials("greedy-app", "tenants.admin"), Ct);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("invalid_scope", error.GetProperty("error").GetString());
    }

    /// <summary>
    /// Architecture test (story 62): an endpoint that neither requires a catalogue scope nor is explicitly anonymous
    /// fails the build.
    /// </summary>
    [TestMethod]
    public void Every_endpoint_requires_a_scope_or_is_explicitly_anonymous()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

        Assert.IsNotEmpty(endpoints);
        var unprotected = new List<string>();
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                continue;
            var required = endpoint.Metadata.GetOrderedMetadata<RequireScopeAttribute>();
            if (required.Count == 0 || required.Any(r => r.Scopes.Count == 0 || r.Scopes.Any(s => !Scopes.All.ContainsKey(s))))
                unprotected.Add(endpoint.DisplayName ?? endpoint.RoutePattern.RawText!);
        }

        Assert.IsEmpty(unprotected, "Endpoints with neither a catalogue scope nor [AllowAnonymous]: " + string.Join(", ", unprotected));
    }

    [TestMethod]
    public void The_architecture_test_covers_both_hubs()
    {
        var patterns = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/')).ToList();

        CollectionAssert.Contains(patterns, "/hubs/agents");
        CollectionAssert.Contains(patterns, "/hubs/dashboard");
    }

    [TestMethod]
    public async Task A_call_without_a_token_gets_401_problem_details()
    {
        using var api = _factory.CreateAnonymousClient();

        var response = await api.GetAsync("api/v1/tenants", Ct);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.AreEqual(401, problem!.Status);
    }

    [TestMethod]
    public async Task A_token_missing_the_scope_gets_403_problem_details_naming_the_scope()
    {
        using var api = await _factory.CreateClientAsync("devices.read");

        var response = await api.GetAsync("api/v1/tenants", Ct);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        // RFC 6750 §3.1: insufficient_scope, with the scope the resource needs.
        var challenge = response.Headers.WwwAuthenticate.Single();
        Assert.AreEqual("Bearer", challenge.Scheme);
        StringAssert.Contains(challenge.Parameter, "error=\"insufficient_scope\"");
        StringAssert.Contains(challenge.Parameter, "scope=\"tenants.read\"");
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(Ct);
        Assert.AreEqual(403, problem!.Status);
    }

    [TestMethod]
    [DataRow("GET", "api/v1/tenants", "tenants.read")]
    [DataRow("GET", "api/v1/tenants/" + AnyId, "tenants.read")]
    [DataRow("POST", "api/v1/tenants", "tenants.write")]
    [DataRow("PUT", "api/v1/tenants/" + AnyId, "tenants.write")]
    [DataRow("DELETE", "api/v1/tenants/" + AnyId, "tenants.delete")]
    [DataRow("GET", "api/v1/devices", "devices.read")]
    [DataRow("GET", "api/v1/devices/" + AnyId, "devices.read")]
    [DataRow("PATCH", "api/v1/devices/" + AnyId, "devices.write")]
    [DataRow("DELETE", "api/v1/devices/" + AnyId, "devices.delete")]
    [DataRow("GET", "api/v1/agents/" + AnyId, "devices.read")]
    [DataRow("POST", "api/v1/agents", "agent")]
    [DataRow("PUT", "api/v1/agents/" + AnyId + "/heartbeat", "agent")]
    [DataRow("GET", "api/v1/agents/" + AnyId + "/pending-commands", "agent")]
    [DataRow("GET", "api/v1/devices/" + AnyId + "/commands", "commands.read")]
    [DataRow("GET", "api/v1/devices/" + AnyId + "/commands/" + AnyId, "commands.read")]
    [DataRow("POST", "api/v1/devices/" + AnyId + "/commands", "commands.execute")]
    [DataRow("POST", "api/v1/devices/" + AnyId + "/commands/" + AnyId + ":cancel", "commands.execute")]
    [DataRow("PUT", "api/v1/devices/" + AnyId + "/commands/" + AnyId + "/result", "agent")]
    [DataRow("GET", "api/v1/devices/" + AnyId + "/telemetry", "telemetry.read")]
    [DataRow("GET", "api/v1/devices/" + AnyId + "/telemetry/" + AnyId, "telemetry.read")]
    [DataRow("POST", "api/v1/agents/" + AnyId + "/telemetry", "agent")]
    [DataRow("POST", "hubs/agents/negotiate?negotiateVersion=1", "agent")]
    [DataRow("POST", "hubs/dashboard/negotiate?negotiateVersion=1", "devices.read")]
    [DataRow("POST", "hubs/dashboard/negotiate?negotiateVersion=1", "telemetry.read")]
    public async Task Each_endpoint_requires_its_scope(string method, string path, string scope)
    {
        // The dashboard hub accepts either of its two scopes, so neither may be in the token that lacks the scope.
        string[] alternatives = path.StartsWith("hubs/dashboard", StringComparison.Ordinal) ? ["devices.read", "telemetry.read"] : [scope];
        using var withScope = await _factory.CreateClientAsync(scope);
        using var withEverythingElse = await _factory.CreateClientAsync(GrantableScopes.Except(alternatives).ToArray());

        var allowed = await withScope.SendAsync(Request(method, path), Ct);
        var refused = await withEverythingElse.SendAsync(Request(method, path), Ct);

        Assert.IsFalse(allowed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnsupportedMediaType,
            $"{method} {path} refused a token with {scope}: {(int)allowed.StatusCode}");
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, $"{method} {path} without {scope}");
    }

    [TestMethod]
    public async Task Running_a_script_needs_terminal_execute_as_well_as_commands_execute()
    {
        using var admin = _factory.CreateClient();
        var tenant = await _factory.CreateTenantAsync(admin, "Contoso");
        var agent = await (await Signal9ApiFactory.RegisterAsync(admin, tenant.Id, "PC01"))
            .Content.ReadFromJsonAsync<JsonElement>(Signal9Json.Options, Ct);
        var commands = $"api/v1/devices/{agent.GetProperty("deviceId").GetGuid()}/commands";
        using var commandsOnly = await _factory.CreateClientAsync("commands.execute");
        using var terminalOnly = await _factory.CreateClientAsync("terminal.execute");
        using var both = await _factory.CreateClientAsync("commands.execute", "terminal.execute");

        var predefined = await commandsOnly.PostAsJsonAsync(commands, new CreateCommandRequest { Type = CommandType.RestartService }, Signal9Json.Options, Ct);
        var scriptWithoutTerminal = await commandsOnly.PostAsJsonAsync(commands, new CreateCommandRequest { Type = CommandType.RunScript }, Signal9Json.Options, Ct);
        var scriptWithoutCommands = await terminalOnly.PostAsJsonAsync(commands, new CreateCommandRequest { Type = CommandType.RunScript }, Signal9Json.Options, Ct);
        var script = await both.PostAsJsonAsync(commands, new CreateCommandRequest { Type = CommandType.RunScript }, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.Created, predefined.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, scriptWithoutTerminal.StatusCode);
        StringAssert.Contains(scriptWithoutTerminal.Headers.WwwAuthenticate.Single().Parameter, "scope=\"terminal.execute\"");
        Assert.AreEqual("application/problem+json", scriptWithoutTerminal.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Forbidden, scriptWithoutCommands.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, script.StatusCode);
    }

    [TestMethod]
    public async Task OpenApi_document_shows_each_endpoints_required_scopes()
    {
        using var api = _factory.CreateAnonymousClient();

        var response = await api.GetAsync("openapi/v1.json", Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("oauth2");
        Assert.AreEqual("oauth2", scheme.GetProperty("type").GetString());
        var flow = scheme.GetProperty("flows").GetProperty("clientCredentials");
        Assert.AreEqual(Signal9AuthFactory.Issuer + "connect/token", flow.GetProperty("tokenUrl").GetString());
        var documented = flow.GetProperty("scopes").EnumerateObject().Select(p => p.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "tenants.read", "devices.write", "terminal.execute", "agent" }, documented);

        CollectionAssert.AreEqual(new[] { "tenants.read" }, ScopesOf(document, "/api/v1/tenants", "get"));
        CollectionAssert.AreEqual(new[] { "tenants.delete" }, ScopesOf(document, "/api/v1/tenants/{tenantId}", "delete"));
        CollectionAssert.AreEqual(new[] { "devices.write" }, ScopesOf(document, "/api/v1/devices/{deviceId}", "patch"));
        CollectionAssert.AreEqual(new[] { "agent" }, ScopesOf(document, "/api/v1/agents/{agentId}/telemetry", "post"));
        // Every operation in the document names its scopes and its 401 and 403 responses.
        foreach (var path in document.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
            {
                Assert.IsTrue(operation.Value.TryGetProperty("security", out var security), $"{operation.Name} {path.Name}");
                Assert.IsGreaterThan(0, security.GetArrayLength(), $"{operation.Name} {path.Name}");
                var responses = operation.Value.GetProperty("responses");
                Assert.IsTrue(responses.TryGetProperty("401", out _), $"{operation.Name} {path.Name} 401");
                Assert.IsTrue(responses.TryGetProperty("403", out _), $"{operation.Name} {path.Name} 403");
            }
    }

    [TestMethod]
    public async Task OpenApi_document_names_terminal_execute_for_running_scripts()
    {
        using var api = _factory.CreateAnonymousClient();

        var document = await api.GetFromJsonAsync<JsonElement>("openapi/v1.json", Ct);

        var create = document.GetProperty("paths").GetProperty("/api/v1/devices/{deviceId}/commands").GetProperty("post");
        CollectionAssert.AreEqual(new[] { "commands.execute" }, ScopesOf(document, "/api/v1/devices/{deviceId}/commands", "post"));
        StringAssert.Contains(create.GetProperty("description").GetString(), "terminal.execute");
    }

    /// <summary>Every catalogue scope a client can request with client credentials.</summary>
    private static readonly string[] GrantableScopes = Signal9ApiFactory.GrantableScopes;

    private static List<string> ScopesOf(JsonElement document, string path, string method)
    {
        var security = document.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("security");
        return security.EnumerateArray().SelectMany(requirement => requirement.GetProperty("oauth2").EnumerateArray())
            .Select(s => s.GetString()!).ToList();
    }

    private static HttpRequestMessage Request(string method, string path) =>
        new(new HttpMethod(method), path)
        {
            // [Consumes] takes part in routing: the wrong media type is a 415 before authorization runs.
            Content = new StringContent("{}", Encoding.UTF8, method == "PATCH" ? "application/merge-patch+json" : "application/json"),
        };

    public void Dispose() => _factory.Dispose();
}
