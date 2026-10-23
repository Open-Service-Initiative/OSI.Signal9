using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.Server.Identity;
using Yarp.ReverseProxy.Forwarder;

namespace OSI.Signal9.Web.Bff;

/// <summary>
/// web forwards the dashboard's calls to <c>api</c> with YARP (ADR-0003). The destinations are a fixed allow-list on
/// api's configured address (RFC 10017 §6.1): the API and the dashboard hub, with the person's access token attached
/// here, and the public OpenAPI document, without one. An access token about to expire is refreshed first, and a 401
/// from api is retried once after a refresh (#118). Nothing else reaches api, and the destination never comes from the
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
                if (await TokenAsync(context, sessions, rejected: null) is not { } token)
                    return;

                // A 401 from api gets one refresh and one retry, so the request body must be readable twice.
                var upgrade = context.Features.Get<IHttpUpgradeFeature>()?.IsUpgradableRequest == true;
                if (!upgrade)
                    context.Request.EnableBuffering();
                var first = new Transformer(token, holdUnauthorized: !upgrade);
                await forwarder.SendAsync(context, destination, channel.Invoker, config, first);
                if (!first.HeldUnauthorized)
                    return;

                if (await TokenAsync(context, sessions, rejected: token) is not { } refreshed)
                    return;
                context.Request.Body.Position = 0;
                context.Response.StatusCode = StatusCodes.Status200OK;
                await forwarder.SendAsync(context, destination, channel.Invoker, config,
                    new Transformer(refreshed, holdUnauthorized: false));
            }).RequireAuthorization();
        }

        foreach (var pattern in Public)
        {
            app.Map(pattern, async context =>
                await forwarder.SendAsync(context, destination, channel.Invoker, config, new Transformer(token: null, holdUnauthorized: false)))
                .AllowAnonymous();
        }

        return app;
    }

    /// <summary>
    /// The person's access token, or null after answering for web: 401 when their session has ended, signing them out
    /// (only <c>invalid_grant</c> ends it), and 503 when it couldn't be refreshed just now (story 50).
    /// </summary>
    private static async Task<string?> TokenAsync(HttpContext context, DashboardSessions sessions, string? rejected)
    {
        var session = await sessions.GetAccessTokenAsync(context.User, rejected, context.RequestAborted);
        switch (session.Status)
        {
            case SessionTokenStatus.Current:
                return session.AccessToken;
            case SessionTokenStatus.Ended:
                await context.SignOutAsync(BffAuthentication.CookieScheme);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return null;
            default:
                context.Response.Headers.RetryAfter = "5";
                await Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Signal9 can't renew your session right now. You're still signed in; try again shortly.")
                    .ExecuteAsync(context);
                return null;
        }
    }

    /// <summary>
    /// Copies the request, minus what's web's alone: the browser's cookies, any credentials it sent and the CSRF
    /// header, and any forwarding headers a client made up. Then it attaches the person's access token, if any.
    /// </summary>
    private sealed class Transformer(string? token, bool holdUnauthorized) : HttpTransformer
    {
        /// <summary>Whether api answered 401 and the response was held back for a retry.</summary>
        public bool HeldUnauthorized { get; private set; }

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
            if (holdUnauthorized && proxyResponse?.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                // Nothing of it reaches the browser; web refreshes and retries.
                HeldUnauthorized = true;
                return false;
            }

            var result = await base.TransformResponseAsync(httpContext, proxyResponse, cancellationToken);
            // Only web sets cookies on its own origin.
            httpContext.Response.Headers.Remove("Set-Cookie");
            return result;
        }
    }
}
