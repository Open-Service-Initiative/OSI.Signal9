using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.Tokens;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// A person's browser at <c>auth</c>: it keeps cookies and doesn't follow redirects, so tests walk each hop of a
/// sign-in. Cookie expiry is ignored, because the hosts set it from the fixture's clock rather than the real one.
/// </summary>
public sealed class Browser : IDisposable
{
    private readonly HttpClient _client;

    public Browser(TestServer auth, string clientIp = "203.0.113.1")
    {
        _client = new HttpClient(new CookieJar { InnerHandler = auth.CreateHandler() })
        {
            BaseAddress = Signal9ApiFactory.HttpsLocalhost,
        };
        _client.DefaultRequestHeaders.Add(Signal9ApiFactory.ClientIpHeader, clientIp);
    }

    public Task<HttpResponseMessage> GetAsync(string uri) => _client.GetAsync(uri);

    public Task<HttpResponseMessage> PostAsync(Uri uri, HttpContent content) => _client.PostAsync(uri, content);

    /// <summary>
    /// Sends an authorization request to <c>auth</c> and, if auth sends the browser to Entra, signs
    /// <paramref name="user"/> in there and follows the redirects back. Returns where auth finally sends the browser:
    /// the client's redirect URI, with a code or an error.
    /// </summary>
    public async Task<Uri> AuthorizeAsync(string authorizeRequest, FakeEntra entra, EntraUser user)
    {
        var response = await SendAuthorizeAsync(authorizeRequest, entra, user);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, await response.Content.ReadAsStringAsync());
        return response.Headers.Location!;
    }

    /// <summary>
    /// Like <see cref="AuthorizeAsync"/>, but answers auth's consent screen if it shows one (story 87), and says whether
    /// it did.
    /// </summary>
    public async Task<(Uri Redirect, bool AskedForConsent)> AuthorizeWithConsentAsync(string authorizeRequest, FakeEntra entra,
        EntraUser user, bool accept = true)
    {
        var response = await SendAuthorizeAsync(authorizeRequest, entra, user);
        var asked = response.StatusCode == HttpStatusCode.OK;
        if (asked)
            response = await AnswerConsentAsync(response, accept);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (response.Headers.Location!, asked);
    }

    /// <summary>
    /// Sends an authorization request and, if auth sends the browser to Entra, signs <paramref name="user"/> in there and
    /// follows the redirects back. Returns auth's last response: a redirect to the client, or a consent screen.
    /// </summary>
    public async Task<HttpResponseMessage> SendAuthorizeAsync(string authorizeRequest, FakeEntra entra, EntraUser user)
    {
        var response = await GetAsync(authorizeRequest);
        if (response.StatusCode == HttpStatusCode.Redirect && response.Headers.Location!.Host == new Uri(FakeEntra.Instance).Host)
        {
            var (callback, form) = entra.SignIn(response.Headers.Location, user);
            response = await PostAsync(callback, form);
            while (response.StatusCode == HttpStatusCode.Redirect && !response.Headers.Location!.IsAbsoluteUri)
                response = await GetAsync(response.Headers.Location.OriginalString);
        }
        Assert.IsEmpty(entra.Unhandled, string.Join(", ", entra.Unhandled));
        return response;
    }

    /// <summary>Submits auth's consent screen as the person would, accepting or declining, and returns auth's answer.</summary>
    public async Task<HttpResponseMessage> AnswerConsentAsync(HttpResponseMessage page, bool accept, bool omitAntiforgery = false)
    {
        var html = await page.Content.ReadAsStringAsync();
        var form = Regex.Match(html, "<form[^>]*action=\"([^\"]*)\"");
        Assert.IsTrue(form.Success, html);
        var fields = Regex.Matches(html, "<input type=\"hidden\" name=\"([^\"]*)\" value=\"([^\"]*)\"")
            .Select(m => new KeyValuePair<string, string>(WebUtility.HtmlDecode(m.Groups[1].Value), WebUtility.HtmlDecode(m.Groups[2].Value)))
            .Where(field => !omitAntiforgery || field.Key != "__RequestVerificationToken")
            .Append(new("consent", accept ? "accept" : "deny"));
        return await PostAsync(new Uri(WebUtility.HtmlDecode(form.Groups[1].Value), UriKind.Relative), new FormUrlEncodedContent(fields));
    }

    public void Dispose() => _client.Dispose();

    private sealed class CookieJar : DelegatingHandler
    {
        private readonly Dictionary<string, string> _cookies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_cookies.Count > 0)
                request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));

            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                foreach (var cookie in cookies)
                {
                    var pair = cookie.Split(';')[0];
                    var separator = pair.IndexOf('=', StringComparison.Ordinal);
                    var (name, value) = (pair[..separator], pair[(separator + 1)..]);
                    // Deleting a cookie sets it empty and expired in 1970.
                    if (value.Length == 0 || cookie.Contains("1970", StringComparison.Ordinal))
                        _cookies.Remove(name);
                    else
                        _cookies[name] = value;
                }
            }
            return response;
        }
    }
}

/// <summary>An OAuth client's side of authorization code with PKCE (RFC 7636).</summary>
public sealed class CodeFlow
{
    public const string RedirectUri = "https://client.example/callback";

    public string ClientId { get; }
    public string Verifier { get; } = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    public string State { get; } = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16));
    public string Challenge => Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier)));

    public CodeFlow(string clientId) => ClientId = clientId;

    /// <summary>The authorization request, with an S256 challenge unless another method (or none) is given.</summary>
    public string AuthorizeRequest(string scope = "openid tenants.read", string? challengeMethod = "S256", string? nonce = "client-nonce",
        int? maxAge = null)
    {
        var query = Parameters(scope, challengeMethod, nonce);
        if (maxAge is not null)
            query["max_age"] = maxAge.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return QueryHelpers.AddQueryString("connect/authorize", query);
    }

    /// <summary>The authorization request's parameters, as sent to the authorization or PAR endpoint.</summary>
    public Dictionary<string, string?> Parameters(string scope = "openid tenants.read", string? challengeMethod = "S256",
        string? nonce = "client-nonce")
    {
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = RedirectUri,
            ["scope"] = scope,
            ["state"] = State,
        };
        if (nonce is not null)
            query["nonce"] = nonce;
        if (challengeMethod is not null)
        {
            query["code_challenge"] = challengeMethod == "plain" ? Verifier : Challenge;
            query["code_challenge_method"] = challengeMethod;
        }
        return query;
    }

    /// <summary>The code in a redirect to the client, after checking it carries this flow's state.</summary>
    public string CodeFrom(Uri redirect)
    {
        var query = QueryHelpers.ParseQuery(redirect.Query);
        Assert.IsFalse(query.ContainsKey("error"), $"{query["error"]}: {query["error_description"]}");
        Assert.AreEqual(State, query["state"].ToString());
        return query["code"].ToString();
    }

    /// <summary>The error in a redirect to the client.</summary>
    public static string ErrorFrom(Uri redirect)
    {
        Assert.StartsWith(RedirectUri, redirect.ToString());
        return QueryHelpers.ParseQuery(redirect.Query)["error"].ToString();
    }

    /// <summary>The token request that redeems <paramref name="code"/>, authenticated with the client's secret.</summary>
    public FormUrlEncodedContent Redemption(string code) => Redemption(code, Signal9ApiFactory.ClientSecret);

    /// <summary>The token request that redeems <paramref name="code"/> with <paramref name="secret"/>, and the verifier if sent.</summary>
    public FormUrlEncodedContent Redemption(string code, string secret, bool withVerifier = true)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["client_secret"] = secret,
        };
        if (withVerifier)
            form["code_verifier"] = Verifier;
        return new FormUrlEncodedContent(form);
    }
}
