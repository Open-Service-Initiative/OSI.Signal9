using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OSI.Signal9.Web;
using OSI.Signal9.Web.Bff;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Hosts a <c>web</c> replica in memory, sharing the database, clock and key-encryption key of the
/// <see cref="Signal9ApiFactory"/> that owns it. Its calls to <c>auth</c> and <c>api</c> reach their in-memory hosts:
/// to <c>api</c> through Container Apps' ingress, which forwards web's client certificate.
/// </summary>
public sealed class Signal9WebFactory : WebApplicationFactory<WebHost>
{
    private readonly Signal9ApiFactory _owner;

    public Signal9WebFactory(Signal9ApiFactory owner)
    {
        _owner = owner;
        ClientOptions.BaseAddress = BaseUri;
        ClientOptions.AllowAutoRedirect = false;
    }

    /// <summary>web's public origin, where people's browsers reach it.</summary>
    public const string Origin = "https://web.signal9.test";

    public static readonly Uri BaseUri = new(Origin + "/");

    /// <summary>Every request web sent to <c>api</c>, as api's ingress received it.</summary>
    public ObservedQueue<ApiHop> ApiRequests { get; } = new();

    /// <summary>Every request web sent to <c>api</c> that has had its answer, by URI, as they complete.</summary>
    public ObservedQueue<Uri> AnsweredApiRequests { get; } = new();

    /// <summary>Every warning or worse this host logged, formatted.</summary>
    public ObservedQueue<string> Logs { get; } = new();

    /// <summary>The refresh tokens api's token endpoint issued to web, in order.</summary>
    public ConcurrentQueue<string> IssuedRefreshTokens { get; } = new();

    /// <summary>
    /// Stands in for api or the network failing: when it returns a response (or throws), web gets that instead of
    /// api's answer.
    /// </summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Fault { get; set; }

    /// <summary>Answers for Azure SignalR Service, when web forwards the dashboard's service path to it.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? RealtimeService { get; set; }

    /// <summary>Every request web forwarded to Azure SignalR Service, as sent.</summary>
    public ConcurrentQueue<(Uri Uri, IReadOnlyDictionary<string, string> Headers)> ServiceRequests { get; } = new();

    /// <summary>How many times web refreshed tokens at api.</summary>
    public int Refreshes => ApiRequests.Count(r => r.Uri.AbsolutePath == "/connect/token" && r.GrantType == "refresh_token");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Signal9ApiFactory.ConfigureSharedHost(builder, _owner.ConnectionString, _owner.Time, _owner.Settings);
        builder.UseSetting("Identity:WebUri", BaseUri.ToString());
        builder.ConfigureTestServices(services =>
        {
            // Discovery and keys at auth. Microsoft.IdentityModel caches them on the real clock, while the fixture's clock
            // can jump past a key rotation in an instant, so this web fetches them each time.
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                options.BackchannelHttpHandler = _owner.Auth.Server.CreateHandler();
                options.ConfigurationManager = new UncachedConfiguration(
                    new Uri(new Uri(Signal9AuthFactory.Issuer), ".well-known/openid-configuration").ToString(),
                    new HttpClient(_owner.Auth.Server.CreateHandler()));
            });
            // Code redemption and proxied calls at api, presenting web's certificate.
            services.RemoveAll<IApiHandlerFactory>();
            services.AddSingleton<IApiHandlerFactory>(new IngressHandlerFactory(this));
            services.RemoveAll<IRealtimeServiceHandlerFactory>();
            services.AddSingleton<IRealtimeServiceHandlerFactory>(new FakeRealtimeService(this));
            services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(Logs.Enqueue));
        });
    }

    /// <summary>
    /// api behind Container Apps' ingress: the request arrives from the trusted proxy with the client certificate web
    /// presented in <c>X-Forwarded-Client-Cert</c>, as in production.
    /// </summary>
    private sealed class IngressHandlerFactory(Signal9WebFactory web) : IApiHandlerFactory
    {
        public HttpMessageHandler Create(X509Certificate2 clientCertificate) =>
            new Ingress(web, clientCertificate)
            {
                InnerHandler = web._owner.Server.CreateHandler(context =>
                    context.Connection.RemoteIpAddress = Signal9ApiFactory.TrustedProxy),
            };
    }

    /// <summary>Stands in for Azure SignalR Service, recording what web sends it.</summary>
    private sealed class FakeRealtimeService(Signal9WebFactory web) : HttpMessageHandler, IRealtimeServiceHandlerFactory
    {
        public HttpMessageHandler Create() => this;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            web.ServiceRequests.Enqueue((request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(web.RealtimeService?.Invoke(request)
                ?? throw new InvalidOperationException("The test set no answer for Azure SignalR Service."));
        }

        protected override void Dispose(bool disposing)
        {
            // Shared by every invoker web makes; the factory outlives them.
        }
    }

    private sealed class Ingress(Signal9WebFactory web, X509Certificate2 certificate) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // The ingress replaces any client certificate header and connects from its own address.
            request.Headers.Remove(Signal9ApiFactory.ClientCertificateHeader);
            request.Headers.Remove(Signal9ApiFactory.ClientIpHeader);
            request.Headers.Add(Signal9ApiFactory.ClientCertificateHeader, TestCertificates.ContainerAppsHeader(certificate));
            string? grantType = null;
            if (request.Content is FormUrlEncodedContent form)
            {
                await form.LoadIntoBufferAsync(cancellationToken);
                grantType = QueryHelpers.ParseQuery(await form.ReadAsStringAsync(cancellationToken))
                    .GetValueOrDefault("grant_type").ToString();
            }
            web.ApiRequests.Enqueue(new ApiHop(request.Method, request.RequestUri!,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase),
                certificate.Thumbprint, grantType));
            if (web.Fault?.Invoke(request) is { } fault)
                return fault;

            var response = await base.SendAsync(request, cancellationToken);
            web.AnsweredApiRequests.Enqueue(request.RequestUri!);
            if (request.RequestUri!.AbsolutePath == "/connect/token" && response.IsSuccessStatusCode)
            {
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                using var tokens = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                if (tokens.RootElement.TryGetProperty("refresh_token", out var refreshToken))
                    web.IssuedRefreshTokens.Enqueue(refreshToken.GetString()!);
            }
            return response;
        }
    }
}

/// <summary>auth's discovery document and keys, fetched afresh on every use.</summary>
internal sealed class UncachedConfiguration(string address, HttpClient client)
    : IConfigurationManager<OpenIdConnectConfiguration>
{
    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
        OpenIdConnectConfigurationRetriever.GetAsync(address, new HttpDocumentRetriever(client), cancel);

    public void RequestRefresh()
    {
    }
}

/// <summary>A request from web to api, with the thumbprint of the client certificate it presented.</summary>
public sealed record ApiHop(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers,
    string CertificateThumbprint, string? GrantType = null)
{
    public string? Authorization => Headers.GetValueOrDefault("Authorization");
}
