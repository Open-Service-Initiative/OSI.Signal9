using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Hubs;

namespace OSI.Signal9.Agent.Identity;

/// <summary>
/// How the agent reaches its deployment's api host: over TLS, presenting an enrolled instance's certificate in the
/// handshake. Tests replace it with an in-memory transport.
/// </summary>
public interface IAgentTransport
{
    /// <summary>
    /// A client for api that presents <paramref name="identity"/>'s certificate (none when enrolling), sending each
    /// request through <paramref name="handlers"/> first.
    /// </summary>
    HttpClient CreateClient(AgentIdentity? identity, params DelegatingHandler[] handlers);

    /// <summary>
    /// A connection to the agent hub, with automatic reconnects, presenting the certificate, a current token and
    /// <paramref name="headers"/>.
    /// </summary>
    HubConnection ConnectToHub(AgentIdentity identity, Func<Task<string?>> accessToken, IReadOnlyDictionary<string, string> headers);
}

/// <summary>The real network: mutual TLS to <see cref="AgentOptions.ApiBaseUrl"/>.</summary>
public sealed class MutualTlsTransport(IOptions<AgentOptions> options) : IAgentTransport
{
    private Uri ApiBaseUrl => options.Value.ApiBaseUrl;

    public HttpClient CreateClient(AgentIdentity? identity, params DelegatingHandler[] handlers)
    {
        var tls = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (identity is not null)
            tls.SslOptions.ClientCertificates = new X509CertificateCollection { identity.Certificate };
        return new HttpClient(Chain(handlers, tls)) { BaseAddress = ApiBaseUrl };
    }

    public HubConnection ConnectToHub(AgentIdentity identity, Func<Task<string?>> accessToken, IReadOnlyDictionary<string, string> headers) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(ApiBaseUrl, HubRoutes.Agents.TrimStart('/')), hub =>
            {
                hub.ClientCertificates = new X509CertificateCollection { identity.Certificate };
                hub.AccessTokenProvider = accessToken;
                foreach (var (name, value) in headers)
                    hub.Headers[name] = value;
            })
            .AddJsonProtocol(json => AgentJson.Configure(json.PayloadSerializerOptions))
            .WithAutomaticReconnect()
            .Build();

    /// <summary><paramref name="handlers"/> in order, in front of <paramref name="inner"/>.</summary>
    public static HttpMessageHandler Chain(IEnumerable<DelegatingHandler> handlers, HttpMessageHandler inner)
    {
        foreach (var handler in handlers.Reverse())
        {
            handler.InnerHandler = inner;
            inner = handler;
        }
        return inner;
    }
}

/// <summary>
/// An enrolled agent instance's authenticated link to its deployment: REST calls and the hub, each presenting its
/// certificate and a certificate-bound access token.
/// </summary>
public sealed class AgentConnection : IDisposable
{
    private readonly HttpClient _tokenHttp;
    private readonly HttpClient _apiHttp;
    private readonly AgentIdentity _identity;
    private readonly IAgentTransport _transport;

    public AgentConnection(AgentIdentity identity, IAgentTransport transport, TimeProvider time)
    {
        _identity = identity;
        _transport = transport;
        _tokenHttp = transport.CreateClient(identity, new AgentVersionHandler());
        Tokens = new AgentTokens(identity.AgentId, _tokenHttp, time);
        _apiHttp = transport.CreateClient(identity, new AgentVersionHandler(), new AgentTokenHandler(Tokens));
        Api = new AgentApiClient(_apiHttp);
    }

    public Guid AgentId => _identity.AgentId;

    public AgentTokens Tokens { get; }

    public AgentApiClient Api { get; }

    /// <summary>A new connection to the agent hub; each (re)connect asks for a current token.</summary>
    public HubConnection ConnectToHub() =>
        _transport.ConnectToHub(_identity, async () => await Tokens.GetAsync(), AgentVersion.Headers);

    public void Dispose()
    {
        _apiHttp.Dispose();
        Tokens.Dispose();
        _tokenHttp.Dispose();
    }
}
