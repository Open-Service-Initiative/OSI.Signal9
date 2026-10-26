using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OSI.Signal9.Server.Hosting;

/// <summary>
/// The operational hardening every host shares (#105): ProblemDetails errors, HSTS, response security headers and
/// rate limiting. Each host calls
/// <see cref="AddHostHardening"/> while building, then <see cref="UseHostHardening"/> right after its health probes and
/// <see cref="RateLimiting.UseRateLimitingAndAuthentication"/> in place of <c>UseAuthentication</c>.
/// </summary>
public static class HostHardening
{
    public static readonly TimeSpan HstsMaxAge = TimeSpan.FromDays(730);

    public static IHostApplicationBuilder AddHostHardening(this IHostApplicationBuilder builder)
    {
        // Errors are ProblemDetails. Outside Development the exception handler writes only the status and a generic
        // title, never the exception (story 131).
        builder.Services.AddProblemDetails();

        // Preload-ready HSTS (story 130), per hstspreload.org: at least a year (two, as recommended), every
        // subdomain, and the preload directive. The middleware never sends it for localhost or plain HTTP.
        builder.Services.AddHsts(options =>
        {
            options.MaxAge = HstsMaxAge;
            options.IncludeSubDomains = true;
            options.Preload = true;
        });

        builder.Services.AddSignal9RateLimiting();
        return builder;
    }

    /// <summary>
    /// Paths whose responses carry a caller's data or credentials, never stored by a browser or shared cache (RFC 9111
    /// §5.2.2.5): api's resources, as api serves them and as web proxies them, and web's session endpoints.
    /// </summary>
    private static readonly PathString[] PrivatePaths = ["/api", "/bff"];

    private const string ReferrerPolicy = "Referrer-Policy";

    /// <summary>
    /// Marks a response that carries a secret, such as a client secret or an enrollment token shown once, as never to be
    /// stored (RFC 9111 §5.2.2.5, as RFC 6749 §5.1 does for tokens), whatever path serves it.
    /// </summary>
    public static void NoStore(this HttpResponse response) => response.Headers.CacheControl = "no-store";

    public static WebApplication UseHostHardening(this WebApplication app)
    {
        // On every response, errors included (OWASP HTTP Headers Cheat Sheet, ASVS V3.4): no MIME sniffing, no Referer
        // to other sites (a page may say otherwise), and no caching of private data unless an endpoint says otherwise.
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                if (!headers.ContainsKey(ReferrerPolicy))
                    headers[ReferrerPolicy] = "no-referrer";
                if (PrivatePaths.Any(path => context.Request.Path.StartsWithSegments(path)) && !headers.ContainsKey(HeaderNames.CacheControl))
                    headers.CacheControl = "no-store";
                return Task.CompletedTask;
            });
            return next(context);
        });
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        // Not in Development, where browsers would cache it for every app on the developer's hostnames.
        if (!app.Environment.IsDevelopment())
            app.UseHsts();
        return app;
    }
}
