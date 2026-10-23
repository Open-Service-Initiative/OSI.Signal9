using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace OSI.Signal9.IntegrationTests;

/// <summary>A person signing in at <see cref="FakeEntra"/>, with the claims Entra would put in their ID token.</summary>
/// <param name="Amr">Authentication methods (<c>amr</c>); Entra writes <c>mfa</c> when MFA was performed.</param>
/// <param name="SessionId">The Entra session (<c>sid</c>), or <see langword="null"/> for a sign-in without one.</param>
/// <param name="Roles">App roles assigned on the operator's app registration.</param>
/// <param name="Email">The UPN, email and preferred username, which must never identify the person.</param>
public sealed record EntraUser(Guid TenantId, Guid ObjectId, string[] Amr, string? SessionId = "entra-session",
    string[]? Roles = null, string Email = "alex@contoso.example")
{
    /// <summary>A person from a new directory who signed in with a password and MFA.</summary>
    public static EntraUser WithMfa() => new(Guid.NewGuid(), Guid.NewGuid(), ["pwd", "mfa"]);
}

/// <summary>
/// A stand-in for Microsoft Entra ID inside the fixture: it answers on <c>login.microsoftonline.com</c> as Entra's v2
/// endpoints do (instance discovery, OpenID Connect metadata, keys, token), and <see cref="SignIn"/> plays the person's
/// browser at the authorize endpoint. Codes are redeemed only with PKCE and a client assertion signed by
/// <see cref="CredentialCertificate"/>, the app registration's certificate credential.
/// </summary>
public sealed class FakeEntra
{
    public const string Instance = "https://login.microsoftonline.com/";

    /// <summary>The operator's multi-tenant app registration.</summary>
    public Guid ClientId { get; } = Guid.NewGuid();

    /// <summary>The directory the app registration belongs to: the operator's own, which deployed Signal9.</summary>
    public Guid DeployingTenantId { get; } = Guid.NewGuid();

    /// <summary>The certificate registered as the app registration's credential (story 125).</summary>
    public X509Certificate2 CredentialCertificate { get; }

    /// <summary>Where the credential certificate is, as a PKCS#12 file for the auth host to load.</summary>
    public string CredentialPath { get; }

    /// <summary>Requests the fake doesn't serve; a test fails on any.</summary>
    public ConcurrentQueue<string> Unhandled { get; } = new();

    private readonly RsaSecurityKey _signingKey = new(RSA.Create(2048)) { KeyId = "fake-entra-key" };
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new();
    private int _signIns;

    /// <summary>How many times people signed in here.</summary>
    public int SignIns => _signIns;

    private sealed record PendingCode(EntraUser User, string RedirectUri, string Nonce, string? Challenge);

    public FakeEntra()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Signal9 test Entra credential", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pfx = certificate.Export(X509ContentType.Pfx);
        CredentialPath = Path.Combine(Path.GetTempPath(), $"signal9-test-entra-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(CredentialPath, pfx);
        CredentialCertificate = X509CertificateLoader.LoadPkcs12(pfx, password: null);
    }

    /// <summary>The handler every outbound HTTP call of the auth host goes through.</summary>
    public HttpMessageHandler CreateHandler() => new Handler(this);

    /// <summary>
    /// Signs <paramref name="user"/> in at the authorize request auth redirected the browser to, and returns what Entra
    /// would post back (<c>response_mode=form_post</c>): the redirect URI and its form.
    /// </summary>
    public (Uri RedirectUri, FormUrlEncodedContent Form) SignIn(Uri authorizeRequest, EntraUser user)
    {
        Assert.AreEqual(new Uri(Instance).Host, authorizeRequest.Host, "auth didn't redirect to Entra.");
        var query = QueryHelpers.ParseQuery(authorizeRequest.Query);
        Assert.AreEqual(ClientId.ToString(), query["client_id"].ToString());
        Assert.AreEqual("code", query["response_type"].ToString());
        Assert.AreEqual("S256", query["code_challenge_method"].ToString(), "auth must use PKCE upstream.");

        Interlocked.Increment(ref _signIns);
        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        _codes[code] = new PendingCode(user, query["redirect_uri"]!, query["nonce"]!, query["code_challenge"]);
        var form = new Dictionary<string, string> { ["code"] = code, ["state"] = query["state"]!, ["session_state"] = Guid.NewGuid().ToString() };
        return (new Uri(query["redirect_uri"]!), new FormUrlEncodedContent(form));
    }

    private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath.Trim('/').Split('/');
        if (request.RequestUri.Host != new Uri(Instance).Host)
            return Unknown(request);

        return path switch
        {
            ["common", "discovery", "instance"] => Json(InstanceDiscovery()),
            [var tenant, "v2.0", ".well-known", "openid-configuration"] => Json(Metadata(tenant)),
            [_, "discovery", "v2.0", "keys"] => Json(new { keys = new[] { PublicJwk() } }),
            [var tenant, "oauth2", "v2.0", "token"] when request.Method == HttpMethod.Post =>
                await RedeemAsync(request, tenant),
            _ => Unknown(request),
        };
    }

    private HttpResponseMessage Unknown(HttpRequestMessage request)
    {
        Unhandled.Enqueue($"{request.Method} {request.RequestUri}");
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static object InstanceDiscovery() => new Dictionary<string, object>
    {
        ["tenant_discovery_endpoint"] = $"{Instance}organizations/v2.0/.well-known/openid-configuration",
        ["api-version"] = "1.1",
        ["metadata"] = new[]
        {
            new Dictionary<string, object>
            {
                ["preferred_network"] = "login.microsoftonline.com",
                ["preferred_cache"] = "login.windows.net",
                ["aliases"] = new[] { "login.microsoftonline.com", "login.windows.net", "login.microsoft.com", "sts.windows.net" },
            },
        },
    };

    private static object Metadata(string tenant) => new Dictionary<string, object>
    {
        ["issuer"] = $"{Instance}{{tenantid}}/v2.0",
        ["authorization_endpoint"] = $"{Instance}{tenant}/oauth2/v2.0/authorize",
        ["token_endpoint"] = $"{Instance}{tenant}/oauth2/v2.0/token",
        ["end_session_endpoint"] = $"{Instance}{tenant}/oauth2/v2.0/logout",
        ["jwks_uri"] = $"{Instance}{tenant}/discovery/v2.0/keys",
        ["response_types_supported"] = new[] { "code", "id_token", "code id_token", "id_token token" },
        ["response_modes_supported"] = new[] { "query", "fragment", "form_post" },
        ["subject_types_supported"] = new[] { "pairwise" },
        ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
        ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_post", "private_key_jwt", "client_secret_basic" },
        ["scopes_supported"] = new[] { "openid", "profile", "email", "offline_access" },
        ["frontchannel_logout_supported"] = true,
        ["http_logout_supported"] = true,
        ["cloud_instance_name"] = "microsoftonline.com",
    };

    private JsonWebKey PublicJwk()
    {
        var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_signingKey.Rsa.ExportParameters(false)));
        jwk.KeyId = _signingKey.KeyId;
        jwk.Use = "sig";
        return jwk;
    }

    private async Task<HttpResponseMessage> RedeemAsync(HttpRequestMessage request, string tenant)
    {
        var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync());
        if (form["grant_type"] != "authorization_code" || !_codes.TryRemove(form["code"].ToString(), out var pending))
            return Error("invalid_grant", "The code is unknown or was already redeemed.");
        if (form["redirect_uri"] != pending.RedirectUri)
            return Error("invalid_grant", "The redirect URI doesn't match.");
        if (pending.Challenge is not null && Challenge(form["code_verifier"].ToString()) != pending.Challenge)
            return Error("invalid_grant", "The code verifier doesn't match.");
        if (!await IsCertificateAssertionAsync(form, request.RequestUri!))
            return Error("invalid_client", "The client must authenticate with its certificate credential.");

        var user = pending.User;
        var now = DateTime.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["tid"] = user.TenantId.ToString(),
            ["oid"] = user.ObjectId.ToString(),
            ["sub"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes($"{user.TenantId}{user.ObjectId}"))),
            ["amr"] = user.Amr,
            ["nonce"] = pending.Nonce,
            ["ver"] = "2.0",
            ["name"] = "Alex Example",
            ["preferred_username"] = user.Email,
            ["email"] = user.Email,
            ["upn"] = user.Email,
        };
        if (user.SessionId is not null)
            claims["sid"] = user.SessionId;
        if (user.Roles is { Length: > 0 })
            claims["roles"] = user.Roles;

        // Entra's clock is the real one: the ID token is checked against it, not the fixture's FakeTimeProvider.
        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = $"{Instance}{user.TenantId}/v2.0",
            Audience = ClientId.ToString(),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddHours(1),
            Claims = claims,
            SigningCredentials = new SigningCredentials(_signingKey, SecurityAlgorithms.RsaSha256),
        });
        var clientInfo = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new { uid = user.ObjectId, utid = user.TenantId }));
        return Json(new Dictionary<string, object>
        {
            ["token_type"] = "Bearer",
            ["scope"] = "openid profile offline_access",
            ["expires_in"] = 3600,
            ["ext_expires_in"] = 3600,
            ["access_token"] = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)),
            ["refresh_token"] = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)),
            ["id_token"] = idToken,
            ["client_info"] = clientInfo,
        });
    }

    private async Task<bool> IsCertificateAssertionAsync(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> form,
        Uri tokenEndpoint)
    {
        if (form.ContainsKey("client_secret")
            || form["client_assertion_type"] != "urn:ietf:params:oauth:client-assertion-type:jwt-bearer")
            return false;

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(form["client_assertion"].ToString(),
            new TokenValidationParameters
            {
                ValidIssuer = ClientId.ToString(),
                ValidAudience = tokenEndpoint.GetLeftPart(UriPartial.Path),
                IssuerSigningKey = new X509SecurityKey(CredentialCertificate),
            });
        return result.IsValid && result.Claims["sub"] as string == ClientId.ToString();
    }

    private static string Challenge(string verifier) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private static HttpResponseMessage Error(string error, string description) =>
        new(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { error, error_description = description }),
        };

    private sealed class Handler(FakeEntra entra) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            entra.HandleAsync(request);
    }
}
