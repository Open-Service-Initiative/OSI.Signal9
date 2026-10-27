using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.Features;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.Web.Bff;

/// <summary>
/// What web asks of browsers (story 130, story 47): a strict Content Security Policy, a custom header on every
/// state-changing request, and web's own <c>Origin</c> on the dashboard hub's handshake. There's no CORS anywhere, so a cross-site page can neither send that header nor read a
/// response (RFC 10017 §6.1).
/// </summary>
public static class BrowserHardening
{
    /// <summary>
    /// Scripts only from web itself, plus WebAssembly compilation and the import map, by hash. Styles only from web,
    /// except inline style attributes, which Fluent UI and Blazor set at run time; that's the one relaxation. No other
    /// origin, no plugins, no framing. A page with inline script or style of its own (the API reference) gets a nonce for
    /// it, fresh on every response.
    /// </summary>
    public static string ContentSecurityPolicy(string? importMap, string? nonce = null)
    {
        var scripts = "'self' 'wasm-unsafe-eval'";
        var styles = "'self'";
        if (importMap is not null)
            scripts += $" 'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(importMap)))}'";
        if (nonce is not null)
        {
            scripts += $" 'nonce-{nonce}'";
            styles += $" 'nonce-{nonce}'";
        }
        return string.Join("; ",
            "default-src 'self'",
            $"script-src {scripts}",
            $"style-src {styles}",
            $"style-src-elem {styles}",
            "style-src-attr 'unsafe-inline'",
            "img-src 'self' data:",
            "font-src 'self'",
            "connect-src 'self'",
            "object-src 'none'",
            "base-uri 'self'",
            "form-action 'self'",
            "frame-ancestors 'none'");
    }

    /// <summary>The import map a page renders: the one its endpoint describes (<see cref="ImportMapDefinition"/>).</summary>
    public static string? ImportMap(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<ImportMapDefinition>()?.ToString()
        ?? (context.GetEndpoint()?.Metadata.GetMetadata<ResourceAssetCollection>() is { } assets
            ? ImportMapDefinition.FromResourceCollection(assets).ToString()
            : null);

    /// <summary>Sends the Content Security Policy on every response.</summary>
    public static IApplicationBuilder UseContentSecurityPolicy(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy(ImportMap(context),
                    context.Items[ApiReference.NonceItem] as string);
                return Task.CompletedTask;
            });
            return next(context);
        });

    /// <summary>
    /// Refuses any state-changing request without <see cref="BffRoutes.CsrfHeader"/>: 403, since the request is
    /// understood but not one web will act on (RFC 9110 §15.5.4).
    /// </summary>
    public static IApplicationBuilder UseCsrfHeader(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var method = context.Request.Method;
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)
                || HttpMethods.IsTrace(method)
                || context.Request.Headers[BffRoutes.CsrfHeader] == BffRoutes.CsrfHeaderValue)
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Missing CSRF header",
                    Detail = $"State-changing requests must send the {BffRoutes.CsrfHeader} header.",
                },
            });
        });

    /// <summary>
    /// Refuses a dashboard hub request from any origin but web's own (story 47; RFC 6455 §10.2), with 403 like a
    /// missing CSRF header. A WebSocket handshake can't carry the CSRF header, so it must carry web's
    /// <c>Origin</c>, which browsers always send on one (RFC 6455 §4.1). Any other hub request that names an origin
    /// must name web's. A same-origin GET (long polling, server-sent events) names none, and only continues a
    /// connection begun with a negotiate, which needs the CSRF header.
    /// </summary>
    public static IApplicationBuilder UseHubOrigin(this IApplicationBuilder app)
    {
        var webUri = app.ApplicationServices.GetRequiredService<IConfiguration>().GetIdentityOptions().WebUri
            ?? throw new InvalidOperationException("Identity:WebUri must give web's public address.");
        // The origin's ASCII serialization (RFC 6454 §6.2): scheme, host and any non-default port.
        var origin = webUri.GetLeftPart(UriPartial.Authority);
        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(HubRoutes.Dashboard))
            {
                await next(context);
                return;
            }

            var sent = context.Request.Headers.Origin;
            if (sent.Count == 1 ? string.Equals(sent[0], origin, StringComparison.OrdinalIgnoreCase)
                : sent.Count == 0 && !IsWebSocketHandshake(context))
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
            {
                HttpContext = context,
                ProblemDetails =
                {
                    Status = StatusCodes.Status403Forbidden,
                    Title = "Cross-origin hub request",
                    Detail = "The dashboard hub accepts connections from the dashboard's own origin only.",
                },
            });
        });
    }

    // An HTTP/1.1 upgrade (RFC 6455) or an HTTP/2 extended CONNECT (RFC 8441) to a WebSocket.
    private static bool IsWebSocketHandshake(HttpContext context) =>
        context.Request.Headers.Upgrade.Any(u => string.Equals(u, "websocket", StringComparison.OrdinalIgnoreCase))
        || context.Features.Get<IHttpExtendedConnectFeature>()?.IsExtendedConnect == true;
}
