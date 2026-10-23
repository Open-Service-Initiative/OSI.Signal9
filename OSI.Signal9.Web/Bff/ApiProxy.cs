using System.Net.Http.Headers;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.Server.Identity;
using Yarp.ReverseProxy.Forwarder;

namespace OSI.Signal9.Web.Bff;

/// <summary>
/// web forwards the dashboard's calls to <c>api</c> with YARP (ADR-0003). The destinations are a fixed allow-list on
/// api's configured address (RFC 10017 §6.1): the API and the dashboard hub, with the person's access token attached
/// here, and the public OpenAPI document, without one. Nothing else reaches api, and the destination never comes from the
/// request.
/// </summary>
public static class ApiProxy
{
    private static readonly string[] WithToken = ["/api/{**path}", "/hubs/dashboard", "/hubs/dashboard/{**path}"];
    // api's OpenAPI document; web serves the reference page itself (ApiReference).
    private static readonly string[] Public = [ApiDocs.Path + "/openapi/{**path}"];

    public static WebApplication MapApiProxy(this WebApplication app)
    {
        var apiUri = app.Configuration.GetIdentityOptions().ApiUri;
        // YARP appends the request path, so the prefix has no trailing slash.
        var destination = apiUri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        var forwarder = app.Services.GetRequiredService<IHttpForwarder>();
        var channel = app.Services.GetRequiredService<ApiChannel>();
        var config = new ForwarderRequestConfig { ActivityTimeout = TimeSpan.FromSeconds(100) };

        foreach (var pattern in WithToken)
        {
            app.Map(pattern, async context =>
            {
                var sessions = context.RequestServices.GetRequiredService<DashboardSessions>();
                if (await sessions.GetAccessTokenAsync(context.User, context.RequestAborted) is not { } token)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await forwarder.SendAsync(context, destination, channel.Invoker, config, new Transformer(token));
            }).RequireAuthorization();
        }

        foreach (var pattern in Public)
        {
            app.Map(pattern, async context =>
                await forwarder.SendAsync(context, destination, channel.Invoker, config, new Transformer(token: null)))
                .AllowAnonymous();
        }

        return app;
    }

    /// <summary>
    /// Copies the request, minus what's web's alone: the browser's cookies, any credentials it sent and the CSRF
    /// header, and any forwarding headers a client made up. Then it attaches the person's access token, if any.
    /// </summary>
    private sealed class Transformer(string? token) : HttpTransformer
    {
        public override async ValueTask TransformRequestAsync(HttpContext httpContext, HttpRequestMessage proxyRequest,
            string destinationPrefix, CancellationToken cancellationToken)
        {
            await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);
            proxyRequest.Headers.Remove("Cookie");
            proxyRequest.Headers.Authorization = null;
            proxyRequest.Headers.Remove(BffRoutes.CsrfHeader);
            proxyRequest.Headers.Remove("Forwarded");
            foreach (var name in proxyRequest.Headers.Select(h => h.Key)
                         .Where(name => name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)).ToList())
                proxyRequest.Headers.Remove(name);
            if (token is not null)
                proxyRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        public override async ValueTask<bool> TransformResponseAsync(HttpContext httpContext,
            HttpResponseMessage? proxyResponse, CancellationToken cancellationToken)
        {
            var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            // Only web sets cookies on its own origin.
            httpContext.Response.Headers.Remove("Set-Cookie");
            return result;
        }
    }
}
