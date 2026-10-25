using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.Server.Hosting;

/// <summary>
/// Rate limiting with ASP.NET Core's built-in limiter (stories 128, 129). Every policy is configured under
/// <c>RateLimiting:{name}</c> and applies no limit until its <see cref="RateLimitOptions.PermitLimit"/> is set.
/// <see cref="Global"/> covers every request on a host; named policies add their own limit on the endpoints that
/// require them. Later tickets register theirs with <see cref="AddRateLimitPolicy"/>.
/// </summary>
public static class RateLimiting
{
    public const string SectionName = "RateLimiting";

    /// <summary>Every request on the host, per client IP.</summary>
    public const string Global = "Global";

    /// <summary>The token endpoints, per <c>client_id</c> and client IP together.</summary>
    public const string Token = "Token";

    /// <summary>The authorization endpoint, per client IP.</summary>
    public const string Authorize = "Authorize";

    /// <summary>The api's resources, per caller: the access token's subject, or the client IP without one.</summary>
    public const string Api = "Api";

    /// <summary>Agent enrollment, per client IP.</summary>
    public const string Enrollment = "Enrollment";

    /// <summary>
    /// Agent enrollment, per enrollment token. The token is in the request body, which the middleware can't read, so
    /// the endpoint acquires the permit itself from <see cref="RequestRateLimits"/>.
    /// </summary>
    public const string EnrollmentToken = "EnrollmentToken";

    public static IServiceCollection AddSignal9RateLimiting(this IServiceCollection services)
    {
        AddLimitOptions(services, Global);
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // The status code pages write the ProblemDetails body.
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }
                return ValueTask.CompletedTask;
            };
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                Partition(context, Global, ClientIp(context)));
        });
        services.AddRateLimitPolicy(Token, context => $"{ClientId(context)} {ClientIp(context)}");
        services.AddRateLimitPolicy(Authorize, ClientIp);
        services.AddRateLimitPolicy(Api, Caller);
        services.AddRateLimitPolicy(Enrollment, ClientIp);
        AddLimitOptions(services, EnrollmentToken);
        services.AddSingleton<RequestRateLimits>();
        return services;
    }

    /// <summary>
    /// Requires <paramref name="policy"/> on every endpoint that doesn't name its own policy with
    /// <see cref="EnableRateLimitingAttribute"/>.
    /// </summary>
    public static TBuilder RequireRateLimitingUnlessNamed<TBuilder>(this TBuilder builder, string policy)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.Add(endpoint =>
        {
            if (!endpoint.Metadata.OfType<EnableRateLimitingAttribute>().Any())
                endpoint.Metadata.Add(new EnableRateLimitingAttribute(policy));
        });
        return builder;
    }

    /// <summary>Adds a named policy, configured under <c>RateLimiting:{name}</c>, with one limit per partition key.</summary>
    public static IServiceCollection AddRateLimitPolicy(this IServiceCollection services, string name,
        Func<HttpContext, string> partitionKey)
    {
        AddLimitOptions(services, name);
        services.Configure<RateLimiterOptions>(options =>
            options.AddPolicy(name, context => Partition(context, name, partitionKey(context))));
        return services;
    }

    /// <summary>
    /// Adds rate limiting and authentication, in the order each policy needs; hosts call it instead of
    /// <c>UseAuthentication</c>. Every request is limited before authentication, so OpenIddict's own endpoints are
    /// covered and failed client authentication at a token endpoint counts against its limit; except per-caller
    /// endpoints, which are limited after it, once the caller is known. Each request passes one limiter; both share
    /// the global limit. It goes after the error pages, so rejections are ProblemDetails.
    /// </summary>
    public static IApplicationBuilder UseRateLimitingAndAuthentication(this IApplicationBuilder app)
    {
        app.UseWhen(context => !IsPerCaller(context), beforeAuthentication =>
        {
            // A partition key can't be read asynchronously, so read the form for the token policy first.
            beforeAuthentication.Use(async (context, next) =>
            {
                if (PolicyName(context) == Token && context.Request.HasFormContentType)
                    await context.Request.ReadFormAsync(context.RequestAborted);
                await next(context);
            });
            beforeAuthentication.UseRateLimiter();
        });
        app.UseAuthentication();
        app.UseWhen(IsPerCaller, afterAuthentication => afterAuthentication.UseRateLimiter());
        return app;
    }

    private static string? PolicyName(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

    private static bool IsPerCaller(HttpContext context) => PolicyName(context) == Api;

    private static void AddLimitOptions(IServiceCollection services, string name) =>
        services.AddOptions<RateLimitOptions>(name).BindConfiguration($"{SectionName}:{name}")
            .ValidateDataAnnotations().ValidateOnStart();

    private static RateLimitPartition<string> Partition(HttpContext context, string policy, string key) =>
        Partition(context.RequestServices.GetRequiredService<IOptionsMonitor<RateLimitOptions>>().Get(policy), key);

    internal static RateLimitPartition<TKey> Partition<TKey>(RateLimitOptions limit, TKey key)
    {
        if (limit.PermitLimit is not { } permits)
            return RateLimitPartition.GetNoLimiter(key);

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = limit.Window,
            QueueLimit = 0,
        });
    }

    // Forwarded headers are resolved into the remote address only from trusted proxies (story 129).
    private static string ClientIp(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;

    private static string Caller(HttpContext context) =>
        context.User.FindFirst(Claims.Subject)?.Value is { } subject ? $"sub:{subject}" : $"ip:{ClientIp(context)}";

    /// <summary>
    /// The client the request names, authenticated or not: <c>client_id</c> in the form, or the user name of
    /// <c>client_secret_basic</c> (RFC 6749 §2.3.1).
    /// </summary>
    private static string ClientId(HttpContext context)
    {
        if (context.Request.HasFormContentType && context.Request.Form[Parameters.ClientId] is [{ } fromForm, ..])
            return fromForm;

        if (AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var header)
            && string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            && header.Parameter is { } parameter)
        {
            try
            {
                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
                var separator = credentials.IndexOf(':', StringComparison.Ordinal);
                if (separator > 0)
                    return Uri.UnescapeDataString(credentials[..separator].Replace('+', ' '));
            }
            catch (FormatException)
            {
                // Not Base64: OpenIddict rejects it; it's limited with every other unnamed client.
            }
        }

        return string.Empty;
    }
}

/// <summary>
/// Named policies whose partition key only the endpoint knows, such as a value in the request body: the endpoint
/// acquires a permit itself, on top of the policy the middleware applied. Configured like every other policy.
/// </summary>
public sealed class RequestRateLimits(IOptionsMonitor<RateLimitOptions> options) : IDisposable
{
    private readonly PartitionedRateLimiter<(string Policy, string Key)> _limiter =
        PartitionedRateLimiter.Create<(string Policy, string Key), (string Policy, string Key)>(
            partition => RateLimiting.Partition(options.Get(partition.Policy), partition));

    /// <summary>Takes a permit from <paramref name="policy"/>'s partition for <paramref name="key"/>, if one is left.</summary>
    public RateLimitLease Acquire(string policy, string key) => _limiter.AttemptAcquire((policy, key));

    public void Dispose() => _limiter.Dispose();
}

/// <summary>One rate limit policy's settings. Without a <see cref="PermitLimit"/>, nothing is limited.</summary>
public sealed class RateLimitOptions
{
    /// <summary>Requests allowed per partition in each window, or <see langword="null"/> for no limit.</summary>
    [Range(1, int.MaxValue)]
    public int? PermitLimit { get; set; }

    /// <summary>The fixed window the permits are counted in.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
