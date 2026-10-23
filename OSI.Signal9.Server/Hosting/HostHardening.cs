using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OSI.Signal9.Server.Hosting;

/// <summary>
/// The operational hardening every host shares (#105): ProblemDetails errors, HSTS and
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

    public static WebApplication UseHostHardening(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        // Not in Development, where browsers would cache it for every app on the developer's hostnames.
        if (!app.Environment.IsDevelopment())
            app.UseHsts();
        return app;
    }
}
