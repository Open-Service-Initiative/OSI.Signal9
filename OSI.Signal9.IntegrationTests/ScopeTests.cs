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
        using var api = await _factory.CreatePersonClientAsync(_factory.Superadministrator(), "devices.read");

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
    [DataRow("PATCH", "api/v1/tenants/" + AnyId, "tenants.write")]
    [DataRow("POST", "api/v1/tenants/" + AnyId + ":suspend", "tenants.write")]
    [DataRow("POST", "api/v1/tenants/" + AnyId + ":activate", "tenants.write")]
    [DataRow("POST", "api/v1/tenants/" + AnyId + ":cancel", "tenants.write")]
    [DataRow("DELETE", "api/v1/tenants/" + AnyId, "tenants.delete")]
    [DataRow("GET", "api/v1/devices", "devices.read")]
    [DataRow("GET", "api/v1/devices/" + AnyId, "devices.read")]
    [DataRow("PATCH", "api/v1/devices/" + AnyId, "devices.write")]
    [DataRow("DELETE", "api/v1/devices/" + AnyId, "devices.delete")]
    [DataRow("GET", "api/v1/agents/" + AnyId, "devices.read")]
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
    [DataRow("GET", "api/v1/users", "users.read")]
    [DataRow("GET", "api/v1/users/" + AnyId, "users.read")]
    [DataRow("POST", "api/v1/users", "users.write")]
    [DataRow("GET", "api/v1/roles", "roles.read")]
    [DataRow("GET", "api/v1/roles/" + AnyId, "roles.read")]
    [DataRow("PATCH", "api/v1/roles/" + AnyId, "roles.write")]
    [DataRow("DELETE", "api/v1/roles/" + AnyId, "roles.write")]
    [DataRow("GET", "api/v1/tenants/" + AnyId + "/role-assignments", "roleassignments.read")]
    [DataRow("GET", "api/v1/tenants/" + AnyId + "/role-assignments/" + AnyId, "roleassignments.read")]
    [DataRow("POST", "api/v1/tenants/" + AnyId + "/role-assignments", "roleassignments.write")]
    [DataRow("GET", "api/v1/tenants/" + AnyId + "/enrollment-tokens", "enrollment.read")]
    [DataRow("GET", "api/v1/tenants/" + AnyId + "/enrollment-tokens/" + AnyId, "enrollment.read")]
    [DataRow("POST", "api/v1/tenants/" + AnyId + "/enrollment-tokens", "enrollment.write")]
    [DataRow("POST", "api/v1/tenants/" + AnyId + "/enrollment-tokens/" + AnyId + ":rotate", "enrollment.write")]
    [DataRow("POST", "api/v1/tenants/" + AnyId + "/enrollment-tokens/" + AnyId + ":revoke", "enrollment.write")]
    [DataRow("POST", "hubs/agents/negotiate?negotiateVersion=1", "agent")]
    [DataRow("POST", "hubs/dashboard/negotiate?negotiateVersion=1", "devices.read")]
    [DataRow("POST", "hubs/dashboard/negotiate?negotiateVersion=1", "telemetry.read")]
    public async Task Each_endpoint_requires_its_scope(string method, string path, string scope)
    {
        // The dashboard hub accepts either of its two scopes, so neither may be in the token that lacks the scope.
        string[] alternatives = path.StartsWith("hubs/dashboard", StringComparison.Ordinal) ? ["devices.read", "telemetry.read"] : [scope];
        // A Superadministrator who signed in with only that scope, or without it, reaches anything a role could grant.
        // The agent scope works only for an enrolled agent, acting as itself.
        using var superadministrator = _factory.CreateSuperadministratorClient();
        using var agent = scope == "agent"
            ? await _factory.EnrollAgentAsync((await _factory.CreateTenantAsync(superadministrator, $"T{Guid.NewGuid():N}")).Id)
            : null;
        path = agent is null ? path : path.Replace(AnyId, agent.Id.ToString(), StringComparison.Ordinal);
        using var withScope = agent is not null ? Signal9ApiFactory.AsAgent(_factory.Server, agent)
            : await _factory.CreatePersonClientAsync(_factory.Superadministrator(), scope);
        using var withEverythingElse = await _factory.CreatePersonClientAsync(_factory.Superadministrator(),
            Signal9ApiFactory.PersonScopes.Except(alternatives).ToArray());

        var allowed = await withScope.SendAsync(Request(method, path), Ct);
        var refused = await withEverythingElse.SendAsync(Request(method, path), Ct);

        Assert.IsFalse(allowed.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.UnsupportedMediaType,
            $"{method} {path} refused a token with {scope}: {(int)allowed.StatusCode}");
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, $"{method} {path} without {scope}");
    }

    [TestMethod]
    public async Task Running_a_script_needs_terminal_execute_as_well_as_commands_execute()
    {
        using var admin = _factory.CreateSuperadministratorClient();
        var tenant = await _factory.CreateTenantAsync(admin, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id, "PC01");
        var commands = $"api/v1/devices/{agent.DeviceId}/commands";
        using var commandsOnly = await _factory.CreateApplicationClientAsync(tenant.Id, "commands.execute");
        using var terminalOnly = await _factory.CreateApplicationClientAsync(tenant.Id, "terminal.execute");
        // Running code also needs a person's recent MFA (RecentMfaTests).
        using var both = await _factory.CreatePersonClientAsync(_factory.Superadministrator(), "commands.execute", "terminal.execute");

        var predefined = await commandsOnly.PostAsJsonAsync(commands, new CreateCommandRequest { Type = CommandType.CollectSystemInfo }, Signal9Json.Options, Ct);
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

    // Enums are names on the wire (AGENTS.md): a number, or no type at all, isn't a command type the api can authorize.
    [TestMethod]
    [DataRow("""{"type":2}""", "RunScript's number")]
    [DataRow("""{"type":7}""", "a number no type has")]
    [DataRow("""{"type":"2"}""", "a number in a string")]
    [DataRow("""{}""", "no type")]
    public async Task A_command_type_must_be_given_by_name(string body, string what)
    {
        using var admin = _factory.CreateSuperadministratorClient();
        var tenant = await _factory.CreateTenantAsync(admin, "Contoso");
        using var agent = await _factory.EnrollAgentAsync(tenant.Id, "PC01");
        var commands = $"api/v1/devices/{agent.DeviceId}/commands";
        using var commandsOnly = await _factory.CreateApplicationClientAsync(tenant.Id, "commands.execute", "commands.read");

        var response = await commandsOnly.PostAsync(commands, new StringContent(body, Encoding.UTF8, "application/json"), Ct);
        var queued = await commandsOnly.GetFromJsonAsync<JsonElement>(commands, Signal9Json.Options, Ct);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, what);
        Assert.AreEqual(0, queued.GetProperty("items").GetArrayLength(), what);
    }

    [TestMethod]
    public async Task OpenApi_document_shows_each_endpoints_required_scopes()
    {
        using var api = _factory.CreateAnonymousClient();

        var response = await api.GetAsync("docs/openapi/v1.json", Ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

        var scheme = document.GetProperty("components").GetProperty("securitySchemes").GetProperty("oauth2");
        Assert.AreEqual("oauth2", scheme.GetProperty("type").GetString());
        var flow = scheme.GetProperty("flows").GetProperty("clientCredentials");
        Assert.AreEqual(Signal9AuthFactory.Issuer + "connect/token", flow.GetProperty("tokenUrl").GetString());
        var documented = flow.GetProperty("scopes").EnumerateObject().Select(p => p.Name).ToList();
        CollectionAssert.IsSubsetOf(new[] { "tenants.read", "devices.write", "terminal.execute" }, documented);

        CollectionAssert.AreEqual(new[] { "tenants.read" }, ScopesOf(document, "/api/v1/tenants", "get"));
        CollectionAssert.AreEqual(new[] { "tenants.delete" }, ScopesOf(document, "/api/v1/tenants/{tenantId}", "delete"));
        CollectionAssert.AreEqual(new[] { "devices.write" }, ScopesOf(document, "/api/v1/devices/{deviceId}", "patch"));
        CollectionAssert.AreEqual(new[] { "agent" }, ScopesOf(document, "/api/v1/agents/{agentId}/telemetry", "post"));
        // Enrollment is the one anonymous operation: an enrollment token in the body is its authority.
        Assert.IsFalse(document.GetProperty("paths").GetProperty("/api/v1/agents").GetProperty("post").TryGetProperty("security", out _));
        // Every other operation in the document names its scopes and its 401 and 403 responses.
        foreach (var path in document.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
            {
                if ((path.Name, operation.Name) == ("/api/v1/agents", "post"))
                    continue;
                Assert.IsTrue(operation.Value.TryGetProperty("security", out var security), $"{operation.Name} {path.Name}");
                Assert.IsGreaterThan(0, security.GetArrayLength(), $"{operation.Name} {path.Name}");
                var responses = operation.Value.GetProperty("responses");
                Assert.IsTrue(responses.TryGetProperty("401", out _), $"{operation.Name} {path.Name} 401");
                Assert.IsTrue(responses.TryGetProperty("403", out _), $"{operation.Name} {path.Name} 403");
            }
    }

    [TestMethod]
    public async Task The_security_schemes_describe_each_way_to_get_a_token_with_the_scopes_it_can_grant()
    {
        using var api = _factory.CreateAnonymousClient();
        var document = await api.GetFromJsonAsync<JsonElement>("docs/openapi/v1.json", Ct);
        var schemes = document.GetProperty("components").GetProperty("securitySchemes");

        // A person, through the dashboard or a delegated API application: authorization code, refreshable.
        var flows = schemes.GetProperty("oauth2").GetProperty("flows");
        var code = flows.GetProperty("authorizationCode");
        Assert.AreEqual("https://auth.signal9.test/connect/authorize", code.GetProperty("authorizationUrl").GetString());
        Assert.AreEqual("https://auth.signal9.test/connect/token", code.GetProperty("tokenUrl").GetString());
        Assert.AreEqual("https://auth.signal9.test/connect/token", code.GetProperty("refreshUrl").GetString());
        var codeScopes = ScopeNames(code);
        CollectionAssert.IsSubsetOf(new[] { "openid", "profile", "offline_access", "devices.read", "terminal.execute" }, codeScopes);
        CollectionAssert.DoesNotContain(codeScopes, "agent");

        // An API application acting as itself: client credentials, without the scopes that need a person or an agent.
        var credentials = ScopeNames(flows.GetProperty("clientCredentials"));
        Assert.AreEqual("https://auth.signal9.test/connect/token", flows.GetProperty("clientCredentials").GetProperty("tokenUrl").GetString());
        CollectionAssert.IsSubsetOf(new[] { "devices.read", "terminal.execute" }, credentials);
        foreach (var personOrAgent in new[] { "openid", "profile", "offline_access", "agent" })
            CollectionAssert.DoesNotContain(credentials, personOrAgent);

        // An enrolled agent: client credentials at api's mTLS token endpoint, for the agent scope alone.
        var agent = schemes.GetProperty("agent").GetProperty("flows").GetProperty("clientCredentials");
        Assert.AreEqual("https://api.signal9.test/connect/token", agent.GetProperty("tokenUrl").GetString());
        CollectionAssert.AreEqual(new[] { "agent" }, ScopeNames(agent));

        // Each operation names the scheme that can grant its scope.
        var paths = document.GetProperty("paths");
        Assert.AreEqual("""[{"oauth2":["tenants.read"]}]""",
            paths.GetProperty("/api/v1/tenants").GetProperty("get").GetProperty("security").GetRawText().Replace(" ", "").Replace("\n", ""));
        Assert.AreEqual("""[{"agent":["agent"]}]""",
            paths.GetProperty("/api/v1/agents/{agentId}/heartbeat").GetProperty("put").GetProperty("security").GetRawText().Replace(" ", "").Replace("\n", ""));
    }

    private static List<string> ScopeNames(JsonElement flow) =>
        flow.GetProperty("scopes").EnumerateObject().Select(scope => scope.Name).ToList();

    [TestMethod]
    public async Task OpenApi_document_names_terminal_execute_for_running_scripts()
    {
        using var api = _factory.CreateAnonymousClient();

        var document = await api.GetFromJsonAsync<JsonElement>("docs/openapi/v1.json", Ct);

        var create = document.GetProperty("paths").GetProperty("/api/v1/devices/{deviceId}/commands").GetProperty("post");
        CollectionAssert.AreEqual(new[] { "commands.execute" }, ScopesOf(document, "/api/v1/devices/{deviceId}/commands", "post"));
        StringAssert.Contains(create.GetProperty("description").GetString(), "terminal.execute");
    }

    /// <summary>Every catalogue scope a client can request with client credentials.</summary>
    private static readonly string[] GrantableScopes = Signal9ApiFactory.GrantableScopes;

    private static List<string> ScopesOf(JsonElement document, string path, string method)
    {
        var security = document.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("security");
        return security.EnumerateArray().SelectMany(requirement => requirement.EnumerateObject())
            .SelectMany(scheme => scheme.Value.EnumerateArray()).Select(s => s.GetString()!).ToList();
    }

    private static HttpRequestMessage Request(string method, string path) =>
        new(new HttpMethod(method), path)
        {
            // [Consumes] takes part in routing: the wrong media type is a 415 before authorization runs.
            Content = new StringContent("{}", Encoding.UTF8, method == "PATCH" ? "application/merge-patch+json" : "application/json"),
        };

    public void Dispose() => _factory.Dispose();
}
