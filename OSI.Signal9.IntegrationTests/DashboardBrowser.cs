using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.TestHost;
using OSI.Signal9.Contracts.Bff;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// A person's browser using the dashboard: it reaches <c>web</c> at <see cref="Signal9WebFactory.Origin"/> and
/// <c>auth</c> at its issuer, keeps each host's cookies apart, and signs in at <see cref="FakeEntra"/> when sent there.
/// It records every response it receives, so tests can check what the browser ever held.
/// </summary>
public sealed class DashboardBrowser : IDisposable
{
    private static readonly string WebHost = Signal9WebFactory.BaseUri.Host;
    private static readonly string AuthHost = new Uri(Signal9AuthFactory.Issuer).Host;

    private readonly Signal9ApiFactory _factory;
    private readonly Dictionary<string, Dictionary<string, string>> _cookies = [];
    private HttpMessageInvoker _web;
    private readonly HttpMessageInvoker _auth;

    public DashboardBrowser(Signal9ApiFactory factory, Signal9WebFactory? web = null)
    {
        _factory = factory;
        _web = new HttpMessageInvoker((web ?? factory.Web).Server.CreateHandler());
        _auth = new HttpMessageInvoker(factory.Auth.Server.CreateHandler());
    }

    /// <summary>Every response the browser received, in order: its URI, its headers and its body.</summary>
    public List<(Uri Uri, string Headers, string Body)> Received { get; } = [];

    /// <summary>Every <c>Set-Cookie</c> header web sent, as sent.</summary>
    public List<string> WebSetCookies { get; } = [];

    /// <summary>From now on, reaches web at another replica, keeping its cookies (as a load balancer would).</summary>
    public void UseWebReplica(Signal9WebFactory replica)
    {
        _web.Dispose();
        _web = new HttpMessageInvoker(replica.Server.CreateHandler());
    }

    /// <summary>
    /// Opens web's sign-in, follows every redirect through auth and Entra, signing <paramref name="user"/> in there, and
    /// returns the last response: the page web returns the person to.
    /// </summary>
    /// <param name="maxAge">The dashboard's <c>maxAge</c> after a step-up challenge (#120), if any.</param>
    public async Task<HttpResponseMessage> SignInAsync(EntraUser user, string returnUrl = "/", int? maxAge = null)
    {
        var login = $"{BffRoutes.Login}?returnUrl={Uri.EscapeDataString(returnUrl)}";
        if (maxAge is not null)
            login += $"&{BffRoutes.MaxAgeParameter}={maxAge}";
        var response = await SendAsync(HttpMethod.Get, new Uri(Signal9WebFactory.BaseUri, login));
        return await FollowAsync(response, user);
    }

    /// <summary>A same-origin request from the dashboard to web, as the app's HttpClient sends it.</summary>
    public Task<HttpResponseMessage> FetchAsync(HttpMethod method, string path, HttpContent? content = null,
        bool csrfHeader = true, Action<HttpRequestHeaders>? headers = null) =>
        SendAsync(method, new Uri(Signal9WebFactory.BaseUri, path), content, request =>
        {
            if (csrfHeader)
                request.Headers.Add(BffRoutes.CsrfHeader, BffRoutes.CsrfHeaderValue);
            headers?.Invoke(request.Headers);
        });

    public Task<HttpResponseMessage> GetAsync(string path) => FetchAsync(HttpMethod.Get, path, csrfHeader: false);

    /// <summary>web's cookies in this browser, by name.</summary>
    public IReadOnlyDictionary<string, string> WebCookies => Jar(WebHost);

    /// <summary>Replaces web's cookies, as an attacker replaying a captured cookie would.</summary>
    public void SetWebCookies(IReadOnlyDictionary<string, string> cookies)
    {
        var jar = Jar(WebHost);
        jar.Clear();
        foreach (var (name, value) in cookies)
            jar[name] = value;
    }

    private async Task<HttpResponseMessage> FollowAsync(HttpResponseMessage response, EntraUser user)
    {
        for (var hops = 0; hops < 20 && IsRedirect(response.StatusCode); hops++)
        {
            var location = response.Headers.Location!;
            if (!location.IsAbsoluteUri)
                location = new Uri(response.RequestMessage!.RequestUri!, location);

            if (location.Host == new Uri(FakeEntra.Instance).Host)
            {
                var (callback, form) = _factory.Entra.SignIn(location, user);
                response = await SendAsync(HttpMethod.Post, callback, form);
            }
            else
            {
                response = await SendAsync(HttpMethod.Get, location);
            }
        }

        Assert.IsEmpty(_factory.Entra.Unhandled, string.Join(", ", _factory.Entra.Unhandled));
        return response;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.Found or HttpStatusCode.MovedPermanently;

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri uri, HttpContent? content = null,
        Action<HttpRequestMessage>? configure = null)
    {
        var invoker = uri.Host == WebHost ? _web
            : uri.Host == AuthHost ? _auth
            : throw new InvalidOperationException($"The browser can't reach {uri}.");

        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Add(Signal9ApiFactory.ClientIpHeader, "203.0.113.1");
        var jar = Jar(uri.Host);
        if (jar.Count > 0)
            request.Headers.Add("Cookie", string.Join("; ", jar.Select(c => $"{c.Key}={c.Value}")));
        configure?.Invoke(request);

        var response = await invoker.SendAsync(request, CancellationToken.None);
        // Keep the request for resolving relative redirects; the original is disposed with this scope.
        response.RequestMessage = new HttpRequestMessage(method, uri);

        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var setCookie in setCookies)
            {
                if (uri.Host == WebHost)
                    WebSetCookies.Add(setCookie);
                var pair = setCookie.Split(';')[0];
                var separator = pair.IndexOf('=', StringComparison.Ordinal);
                var (name, value) = (pair[..separator], pair[(separator + 1)..]);
                // Deleting a cookie sets it empty and expired in 1970.
                if (value.Length == 0 || setCookie.Contains("1970", StringComparison.Ordinal))
                    jar.Remove(name);
                else
                    jar[name] = value;
            }
        }

        var body = await response.Content.ReadAsStringAsync();
        Received.Add((uri, response.Headers.ToString() + response.Content.Headers, body));
        // The body was read; give callers a fresh copy of it.
        var copy = new StringContent(body);
        copy.Headers.Clear();
        foreach (var header in response.Content.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = copy;
        return response;
    }

    private Dictionary<string, string> Jar(string host)
    {
        if (!_cookies.TryGetValue(host, out var jar))
            _cookies[host] = jar = [];
        return jar;
    }

    public void Dispose()
    {
        _web.Dispose();
        _auth.Dispose();
    }
}
