using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.SignalR;
using OpenIddict.Abstractions;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;
using OSI.Signal9.Server.Hosting;
using OSI.Signal9.Server.Identity;
using StackExchange.Redis;

namespace OSI.Signal9.API.Hubs;

/// <summary>How api hosts its hubs. The connection strings <c>signalr</c> and <c>redis</c> come from Aspire.</summary>
public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";

    /// <summary>The Azure SignalR Service connection string's name.</summary>
    public const string AzureSignalRConnection = "signalr";

    /// <summary>The Redis backplane's connection string's name; given only when self-hosted api scales out.</summary>
    public const string RedisConnection = "redis";

    /// <summary>Where the hubs run. The AppHost sets it in every mode; api doesn't start without it.</summary>
    [Required(ErrorMessage = HostingRequired)]
    public RealtimeHostingMode? Hosting { get; set; }

    internal const string HostingRequired =
        "Realtime:Hosting is required (SelfHosted or AzureSignalR): the AppHost sets it.";

    /// <summary>
    /// How long the token Azure SignalR issues a client for the service lasts. The client uses it only to open its
    /// connection; reconnecting negotiates at api again, with a current access token.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "00:10:00")]
    public TimeSpan ServiceTokenLifetime { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Prefixes the backplane's Redis channels, so deployments can share a Redis.</summary>
    [Required]
    public string BackplaneChannelPrefix { get; set; } = "signal9";
}

/// <summary>
/// Real-time hosting chosen by configuration (ADR-0009). On Azure both hubs run on Azure SignalR Service, which gets
/// only the claims the hubs read, in a short-lived token. Self-hosted, the hubs run in api; once api scales out they
/// share a Redis backplane, take WebSockets only, and answer negotiation statelessly, so a client's socket may land on
/// any replica without sticky sessions. A single replica needs no Redis.
/// </summary>
public static class RealtimeHosting
{
    /// <summary>The scopes a hub checks: the agent hub's, and any of the dashboard hub's.</summary>
    private static readonly string[] HubScopes = [Scopes.Agent, .. DashboardHub.AnyOfScopes];

    /// <summary>The claims the hubs read, and nothing else (<see cref="ServiceClaims"/>).</summary>
    private static readonly string[] HubClaims =
    [
        OpenIddictConstants.Claims.Subject,
        OpenIddictConstants.Claims.ClientId,
        OpenIddictConstants.Claims.Confirmation,
        OpenIddictConstants.Claims.Private.TokenId,
    ];

    public static WebApplicationBuilder AddSignal9Realtime(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<RealtimeOptions>().BindConfiguration(RealtimeOptions.SectionName)
            .ValidateDataAnnotations().ValidateOnStart();
        var options = builder.Configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>() ?? new();
        var redis = builder.Configuration.GetConnectionString(RealtimeOptions.RedisConnection);
        var signalR = builder.Services.AddSignalR()
            .AddJsonProtocol(json => Signal9Json.Configure(json.PayloadSerializerOptions));

        switch (options.Hosting ?? throw new InvalidOperationException(RealtimeOptions.HostingRequired))
        {
            case RealtimeHostingMode.AzureSignalR:
                var connectionString = builder.Configuration.GetConnectionString(RealtimeOptions.AzureSignalRConnection)
                    ?? throw new InvalidOperationException(
                        $"Realtime:Hosting is {RealtimeHostingMode.AzureSignalR}, but ConnectionStrings:{RealtimeOptions.AzureSignalRConnection} is missing.");
                if (redis is not null)
                    throw new InvalidOperationException("Azure SignalR Service scales out itself; the Redis backplane is for self-hosted real-time only.");
                signalR.AddAzureSignalR(service =>
                {
                    service.ConnectionString = connectionString;
                    service.AccessTokenLifetime = options.ServiceTokenLifetime;
                    service.ClaimsProvider = context => ServiceClaims(context.User);
                });
                break;
            case RealtimeHostingMode.SelfHosted when redis is not null:
                signalR.AddStackExchangeRedis(redis, backplane =>
                    backplane.Configuration.ChannelPrefix = RedisChannel.Literal(options.BackplaneChannelPrefix));
                break;
        }
        return builder;
    }

    /// <summary>
    /// The claims Azure SignalR Service passes on to the hubs, in the token it issues the client: who the caller is,
    /// which access token it connected with (re-checked while it stays connected), the hub scopes it holds, whether it
    /// has a certificate binding (agents) and whether it's a Superadministrator. Nothing else leaves api.
    /// </summary>
    public static IEnumerable<Claim> ServiceClaims(ClaimsPrincipal user)
    {
        foreach (var claim in user.Claims)
        {
            if (HubClaims.Contains(claim.Type)
                || (claim.Type == OpenIddictConstants.Claims.Private.Scope && HubScopes.Contains(claim.Value))
                || (claim.Type == OpenIddictConstants.Claims.Role && claim.Value == Superadministrator.Role))
                yield return new Claim(claim.Type, claim.Value, claim.ValueType);
        }
    }

    /// <summary>Whether api's hubs share a Redis backplane, because it runs more than one replica.</summary>
    public static bool IsScaledOut(IConfiguration configuration) =>
        configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>()?.Hosting is RealtimeHostingMode.SelfHosted
        && configuration.GetConnectionString(RealtimeOptions.RedisConnection) is not null;

    /// <summary>Maps both hubs. Each closes a connection when its access token expires (story 115).</summary>
    public static void MapSignal9Hubs(this WebApplication app)
    {
        var scaledOut = IsScaledOut(app.Configuration);
        void Configure(HttpConnectionDispatcherOptions options)
        {
            options.CloseOnAuthenticationExpiration = true;
            if (scaledOut)
                options.Transports = HttpTransportType.WebSockets;
        }

        app.MapHub<AgentHub>(HubRoutes.Agents, Configure).RequireRateLimiting(RateLimiting.Api);
        app.MapHub<DashboardHub>(HubRoutes.Dashboard, Configure).RequireRateLimiting(RateLimiting.Api);
    }

    /// <summary>
    /// After authorization, so a refused caller gets the hub's own 401 or 403. Self-hosted and scaled out, it answers
    /// negotiation itself, holding nothing (WebSockets, with a connection token no replica needs to know), and drops
    /// that token from the WebSocket request, so SignalR opens it as a connection that skipped negotiation. Clients
    /// therefore negotiate as usual, and their socket may land on any replica. On Azure SignalR, clients connect to
    /// the service, never to api's hubs directly.
    /// </summary>
    public static IApplicationBuilder UseRealtimeNegotiation(this WebApplication app)
    {
        var azure = app.Configuration.GetSection(RealtimeOptions.SectionName).Get<RealtimeOptions>()?.Hosting
            == RealtimeHostingMode.AzureSignalR;
        if (!azure && !IsScaledOut(app.Configuration))
            return app;

        return app.Use(async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            if (endpoint?.Metadata.GetMetadata<HubMetadata>() is null)
            {
                await next(context);
                return;
            }

            var negotiate = endpoint.Metadata.GetMetadata<NegotiateMetadata>() is not null;
            if (azure)
            {
                if (negotiate)
                    await next(context);
                else
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (negotiate)
            {
                await Results.Json(StatelessNegotiation(context.Request.Query["negotiateVersion"])).ExecuteAsync(context);
                return;
            }

            if (context.Request.Query.ContainsKey("id"))
            {
                var query = new QueryBuilder(context.Request.Query
                    .Where(q => q.Key != "id")
                    .SelectMany(q => q.Value.Select(v => KeyValuePair.Create(q.Key, v ?? ""))));
                context.Request.QueryString = query.ToQueryString();
            }
            await next(context);
        });
    }

    // The negotiate response of the SignalR transport protocol, offering WebSockets alone. Version 1 clients put the
    // connection token, and version 0 clients the connection ID, in the socket's "id", which is then dropped.
    private static Dictionary<string, object> StatelessNegotiation(string? requestedVersion)
    {
        var id = Guid.NewGuid().ToString("N");
        var response = new Dictionary<string, object>
        {
            ["connectionId"] = id,
            ["availableTransports"] = new[]
            {
                new Dictionary<string, object> { ["transport"] = "WebSockets", ["transferFormats"] = new[] { "Text", "Binary" } },
            },
        };
        if (int.TryParse(requestedVersion, out var version) && version >= 1)
        {
            response["negotiateVersion"] = 1;
            response["connectionToken"] = id;
        }
        return response;
    }
}
