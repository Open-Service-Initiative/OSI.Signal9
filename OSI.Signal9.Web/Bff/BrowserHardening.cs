using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components;
using OSI.Signal9.Contracts.Bff;

namespace OSI.Signal9.Web.Bff;

/// <summary>
/// What web asks of browsers (story 130, story 47): a strict Content Security Policy, and a custom header on every
/// state-changing request. There's no CORS anywhere, so a cross-site page can neither send that header nor read a
/// response (RFC 10017 §6.1).
/// </summary>
public static class BrowserHardening
{
    /// <summary>
    /// Scripts only from web itself, plus WebAssembly compilation and the import map, by hash. Styles only from web,
    /// except inline style attributes, which Fluent UI and Blazor set at run time; that's the one relaxation. No other
    /// origin, no plugins, no framing.
    /// </summary>
    public static string ContentSecurityPolicy(string? importMap)
    {
        var scripts = "'self' 'wasm-unsafe-eval'";
        if (importMap is not null)
            scripts += $" 'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(importMap)))}'";
        return string.Join("; ",
            "default-src 'self'",
            $"script-src {scripts}",
            "style-src 'self'",
            "style-src-elem 'self'",
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
                context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy(ImportMap(context));
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
}
