using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Rate limiting (stories 128, 129): named, configurable policies under <c>RateLimiting:{name}</c>, with no limits
/// unless configured.
/// </summary>
[TestClass]
public sealed class RateLimitingTests
{
    public TestContext TestContext { get; set; } = null!;

    private CancellationToken Ct => TestContext.CancellationToken;

    private static Dictionary<string, string?> Limit(string policy, int permits) => new()
    {
        [$"RateLimiting:{policy}:PermitLimit"] = permits.ToString(System.Globalization.CultureInfo.InvariantCulture),
        // Long enough that no window ends during a test.
        [$"RateLimiting:{policy}:Window"] = "01:00:00",
    };

    private static HttpClient Host(Signal9ApiFactory factory, string host) =>
        host == "auth" ? factory.Auth.CreateClient() : factory.CreateAnonymousClient();

    private Task<HttpResponseMessage> RequestTokenAsync(HttpClient client, string clientId, string ip = "203.0.113.1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "connect/token")
        {
            Content = Signal9ApiFactory.ClientCredentials(clientId),
        };
        request.Headers.Add(Signal9ApiFactory.ClientIpHeader, ip);
        return client.SendAsync(request, Ct);
    }

    private Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string ip = "203.0.113.1")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(Signal9ApiFactory.ClientIpHeader, ip);
        return client.SendAsync(request, Ct);
    }

    [TestMethod]
    public async Task Nothing_is_limited_by_default()
    {
        using var factory = new Signal9ApiFactory();
        await factory.SeedClientAsync("busy-app");
        using var auth = factory.Auth.CreateClient();
        using var api = factory.CreateClient();

        for (var i = 0; i < 25; i++)
        {
            Assert.AreEqual(HttpStatusCode.OK, (await RequestTokenAsync(auth, "busy-app")).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await GetAsync(api, "api/v1/tenants")).StatusCode);
        }
    }

    [TestMethod]
    public async Task Enrollment_is_limited_per_ip()
    {
        using var factory = new Signal9ApiFactory(settings: Limit("Enrollment", 1));
        using var admin = factory.CreateClient();
        var tenant = await factory.CreateTenantAsync(admin, "Contoso");
        var first = await factory.IssueEnrollmentTokenAsync(tenant.Id);
        var second = await factory.IssueEnrollmentTokenAsync(tenant.Id);
        var site = IPAddress.Parse("203.0.113.1");

        var allowed = await factory.EnrollAsync(first.Secret!, TestCertificates.AgentCertificate(), from: site);
        var limited = await factory.EnrollAsync(second.Secret!, TestCertificates.AgentCertificate(), from: site);
        var otherIp = await factory.EnrollAsync(second.Secret!, TestCertificates.AgentCertificate(), from: IPAddress.Parse("198.51.100.7"));

        Assert.AreEqual(HttpStatusCode.Created, allowed.StatusCode);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, otherIp.StatusCode);
    }

    [TestMethod]
    public async Task Enrollment_is_limited_per_enrollment_token()
    {
        using var factory = new Signal9ApiFactory(settings: Limit("EnrollmentToken", 2));
        using var admin = factory.CreateClient();
        var tenant = await factory.CreateTenantAsync(admin, "Contoso");
        var busy = await factory.IssueEnrollmentTokenAsync(tenant.Id);
        var other = await factory.IssueEnrollmentTokenAsync(tenant.Id);

        Assert.AreEqual(HttpStatusCode.Created,
            (await factory.EnrollAsync(busy.Secret!, TestCertificates.AgentCertificate(), from: IPAddress.Parse("203.0.113.1"))).StatusCode);
        Assert.AreEqual(HttpStatusCode.Created,
            (await factory.EnrollAsync(busy.Secret!, TestCertificates.AgentCertificate(), from: IPAddress.Parse("203.0.113.2"))).StatusCode);
        var limited = await factory.EnrollAsync(busy.Secret!, TestCertificates.AgentCertificate(), from: IPAddress.Parse("203.0.113.3"));
        var otherToken = await factory.EnrollAsync(other.Secret!, TestCertificates.AgentCertificate(), from: IPAddress.Parse("203.0.113.3"));

        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.IsNotNull(limited.Headers.RetryAfter);
        Assert.AreEqual(HttpStatusCode.Created, otherToken.StatusCode);
        // A refused attempt isn't a use.
        using var tokens = factory.CreateClient();
        var used = await tokens.GetFromJsonAsync<OSI.Signal9.Contracts.Enrollment.EnrollmentTokenDto>(busy.Url.TrimStart('/'),
            OSI.Signal9.Contracts.Serialization.Signal9Json.Options, Ct);
        Assert.AreEqual(2, used!.UseCount);
    }

    [TestMethod]
    [DataRow("auth")]
    [DataRow("api")]
    public async Task Token_endpoint_limit_is_per_client_id_and_ip(string host)
    {
        using var factory = new Signal9ApiFactory(settings: Limit("Token", 2));
        await factory.SeedClientAsync("app-a");
        await factory.SeedClientAsync("app-b");
        using var client = Host(factory, host);

        Assert.AreEqual(HttpStatusCode.OK, (await RequestTokenAsync(client, "app-a")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await RequestTokenAsync(client, "app-a")).StatusCode);
        var limited = await RequestTokenAsync(client, "app-a");
        var otherClient = await RequestTokenAsync(client, "app-b");
        var otherIp = await RequestTokenAsync(client, "app-a", ip: "198.51.100.7");

        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.IsNotNull(limited.Headers.RetryAfter);
        Assert.AreEqual(HttpStatusCode.OK, otherClient.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, otherIp.StatusCode);
    }

    [TestMethod]
    public async Task Token_endpoint_limit_counts_failed_attempts_and_basic_authentication()
    {
        using var factory = new Signal9ApiFactory(settings: Limit("Token", 2));
        await factory.SeedClientAsync("app-a");
        using var auth = factory.Auth.CreateClient();

        // A wrong secret still counts, so the limit slows guessing.
        var wrongSecret = new HttpRequestMessage(HttpMethod.Post, "connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "app-a",
                ["client_secret"] = "wrong",
            }),
        };
        wrongSecret.Headers.Add(Signal9ApiFactory.ClientIpHeader, "203.0.113.1");
        Assert.AreNotEqual(HttpStatusCode.OK, (await auth.SendAsync(wrongSecret, Ct)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await RequestTokenAsync(auth, "app-a")).StatusCode);

        // client_secret_basic names the same client in the Authorization header (RFC 6749 §2.3.1).
        var basic = new HttpRequestMessage(HttpMethod.Post, "connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        basic.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"app-a:{Signal9ApiFactory.ClientSecret}")));
        basic.Headers.Add(Signal9ApiFactory.ClientIpHeader, "203.0.113.1");

        Assert.AreEqual(HttpStatusCode.TooManyRequests, (await auth.SendAsync(basic, Ct)).StatusCode);
    }

    [TestMethod]
    public async Task Authorization_endpoint_limit_is_per_ip()
    {
        using var factory = new Signal9ApiFactory(settings: Limit("Authorize", 2));
        await factory.SeedCodeClientAsync("code-app", "tenants.read");
        using var auth = factory.Auth.CreateClient();
        var authorize = new CodeFlow("code-app").AuthorizeRequest();

        Assert.AreEqual(HttpStatusCode.Redirect, (await GetAsync(auth, authorize)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Redirect, (await GetAsync(auth, authorize)).StatusCode);
        var limited = await GetAsync(auth, authorize);
        var otherIp = await GetAsync(auth, authorize, ip: "198.51.100.7");
        var otherEndpoint = await GetAsync(auth, ".well-known/openid-configuration");

        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.Redirect, otherIp.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, otherEndpoint.StatusCode);
    }

    [TestMethod]
    public async Task Api_limit_is_per_caller()
    {
        using var factory = new Signal9ApiFactory(settings: Limit("Api", 2));
        using var callerA = factory.CreateClient();
        await factory.SeedClientAsync("caller-b", "tenants.read");
        var tokenB = await factory.RequestTokenAsync("caller-b", "tenants.read");
        using var callerB = factory.CreateAnonymousClient();
        callerB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenB.Value);

        Assert.AreEqual(HttpStatusCode.OK, (await GetAsync(callerA, "api/v1/tenants")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await GetAsync(callerA, "api/v1/tenants", ip: "198.51.100.7")).StatusCode);
        var limited = await GetAsync(callerA, "api/v1/tenants", ip: "192.0.2.9");
        var otherCaller = await GetAsync(callerB, "api/v1/tenants");

        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual("application/problem+json", limited.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(HttpStatusCode.OK, otherCaller.StatusCode);
    }

    [TestMethod]
    [DataRow("auth", ".well-known/openid-configuration")]
    [DataRow("api", "api/v1/tenants")]
    public async Task Global_limit_covers_every_endpoint_per_ip(string host, string path)
    {
        using var factory = new Signal9ApiFactory(settings: Limit("Global", 2));
        using var client = host == "auth" ? factory.Auth.CreateClient() : factory.CreateClient();

        Assert.AreEqual(HttpStatusCode.OK, (await GetAsync(client, path)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await GetAsync(client, path)).StatusCode);
        var limited = await GetAsync(client, path);
        var otherIp = await GetAsync(client, path, ip: "198.51.100.7");

        Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, otherIp.StatusCode);
    }
}
