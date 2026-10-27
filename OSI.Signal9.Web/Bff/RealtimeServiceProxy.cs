using System.Net;
using System.Text.Json.Nodes;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.Server.Identity;
using Yarp.ReverseProxy.Forwarder;

namespace OSI.Signal9.Web.Bff;

/// <summary>How the deployment hosts real-time, as web needs to know it.</summary>
public sealed class RealtimeServiceOptions
{
    public const string SectionName = "Realtime";

    /// <summary>Where the hubs run, as api has it. The AppHost sets it in every mode; web doesn't start without it.</summary>
    public RealtimeHostingMode? Hosting { get; set; }

    /// <summary>Azure SignalR Service's endpoint, such as <c>https://signal9.service.signalr.net/</c>, when on Azure.</summary>
    public Uri? ServiceEndpoint { get; set; }
}

/// <summary>Creates the handler web reaches Azure SignalR Service with.</summary>
public interface IRealtimeServiceHandlerFactory
{
    HttpMessageHandler Create();
}

/// <summary>A direct TLS connection to the service, set up the way YARP recommends for forwarding.</summary>
internal sealed class SocketsRealtimeServiceHandlerFactory : IRealtimeServiceHandlerFactory
{
    public HttpMessageHandler Create() => new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        EnableMultipleHttp2Connections = true,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };
}

/// <summary>
/// On Azure SignalR Service (ADR-0009), the dashboard reaches the service through web, so the browser stays on web's
/// origin and its Content Security Policy needs no other (#63). api's negotiation redirects the client to the service;
/// web rewrites that redirect to <see cref="ServicePath"/> on its own origin and forwards that path, for the dashboard
/// hub's client endpoint only, to the configured service. The browser holds the service's short-lived token, which
/// carries only what the hub needs, never an API token. Self-hosted, none of this is mapped: the hub is in api, behind
/// the API proxy.
/// </summary>
public static class RealtimeServiceProxy
{
    /// <summary>Where web serves the service's client endpoint; under the hub's path, so its origin check applies.</summary>
    public const string ServicePath = HubRoutes.Dashboard + "/service";

    // Azure SignalR names a hub by its class, lowercased.
    private const string DashboardHubName = "dashboardhub";

    public static IServiceCollection AddRealtimeServiceProxy(this IServiceCollection services, IConfiguration configuration)
    {
        var options = Options(configuration);
        if (options.Hosting is null)
            throw new InvalidOperationException("Realtime:Hosting is required (SelfHosted or AzureSignalR): the AppHost sets it.");
        if (options.Hosting == RealtimeHostingMode.AzureSignalR && options.ServiceEndpoint is not { IsAbsoluteUri: true })
            throw new InvalidOperationException("Realtime:ServiceEndpoint must give Azure SignalR Service's endpoint.");
        services.AddSingleton<IRealtimeServiceHandlerFactory, SocketsRealtimeServiceHandlerFactory>();
        services.AddSingleton(sp => new RealtimeServiceChannel(sp.GetRequiredService<IRealtimeServiceHandlerFactory>()));
        return services;
    }

    /// <summary>The rewrite of api's negotiate response for the dashboard, when on Azure SignalR Service.</summary>
    internal static NegotiationRewrite? Rewrite(IConfiguration configuration) =>
        Options(configuration) is { Hosting: RealtimeHostingMode.AzureSignalR, ServiceEndpoint: { } service }
            ? new NegotiationRewrite(service, configuration.GetIdentityOptions().WebUri
                ?? throw new InvalidOperationException("Identity:WebUri must give web's public address."))
            : null;

    public static WebApplication MapRealtimeServiceProxy(this WebApplication app)
    {
        if (Options(app.Configuration) is not { Hosting: RealtimeHostingMode.AzureSignalR, ServiceEndpoint: { } service })
            return app;

        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        var channel = app.Services.GetRequiredService<RealtimeServiceChannel>();
        var config = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(100) };
        var serviceRoot = service.GetLeftPart(UriPartial.Authority) + "/";

        app.Map(ServicePath + "/{**path}", async context =>
        {
            var path = context.Request.Path.Value![ServicePath.Length..];
            var hubs = context.Request.Query["hub"];
            if (!(path == "/client" || path.StartsWith("/client/", StringComparison.Ordinal))
                || hubs.Count != 1 || hubs[0] != DashboardHubName)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var destination = new Uri(serviceRoot + path.TrimStart('/') + context.Request.QueryString.Value);
            await forwarder.SendAsync(context, serviceRoot, channel.Invoker, config, new ToService(destination));
        }).RequireAuthorization();
        return app;
    }

    private static RealtimeServiceOptions Options(IConfiguration configuration) =>
        configuration.GetSection(RealtimeServiceOptions.SectionName).Get<RealtimeServiceOptions>() ?? new();

    /// <summary>
    /// Copies the request to the service with only what the service needs: the client's service token goes; the
    /// browser's cookies, the CSRF header and any forwarding headers a client made up don't.
    /// </summary>
    private sealed class ToService(Uri destination) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest,
            string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            proxyRequest.RequestUri = destination;
            // Authorization stays: it's the service token the service issued the client, not a Signal9 credential.
            ProxyHeaders.RemoveFromRequest(proxyRequest);
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext,
            HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
        {
            var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            ProxyHeaders.RemoveFromResponse(httpContext);
            return result;
        }
    }
}

/// <summary>The one connection web keeps to Azure SignalR Service.</summary>
public sealed class RealtimeServiceChannel(IRealtimeServiceHandlerFactory handlers)
{
    private readonly Lazy<HttpMessageInvoker> _invoker = new(() => new HttpMessageInvoker(handlers.Create()));

    public HttpMessageInvoker Invoker => _invoker.Value;
}

/// <summary>
/// Points a negotiate redirect to Azure SignalR Service's client endpoint at web's own path for it, keeping the rest
/// of the URL (the hub and the service's own parameters) as it is.
/// </summary>
internal sealed class NegotiationRewrite(Uri serviceEndpoint, Uri webUri)
{
    private readonly string _serviceClient = serviceEndpoint.GetLeftPart(UriPartial.Authority) + "/client/";
    private readonly string _webClient = new Uri(webUri, RealtimeServiceProxy.ServicePath.TrimStart('/') + "/client/").AbsoluteUri;

    /// <summary>
    /// The response with its redirect rewritten; unchanged when it isn't a redirect; null when it redirects anywhere
    /// but the configured service, which web never passes on.
    /// </summary>
    public byte[]? Apply(byte[] body)
    {
        if (JsonNode.Parse(body) is not JsonObject response || response["url"] is not { } url)
            return body;
        var target = url.GetValue<string>();
        if (!target.StartsWith(_serviceClient, StringComparison.OrdinalIgnoreCase))
            return null;
        response["url"] = _webClient + target[_serviceClient.Length..];
        return System.Text.Encoding.UTF8.GetBytes(response.ToJsonString());
    }
}
