using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Hosting;

namespace OSI.Signal9.API.Hubs;

/// <summary>How often api re-checks every open hub connection against its caller's live access.</summary>
public sealed class HubAccessOptions
{
    public const string SectionName = "Hubs";

    /// <summary>
    /// The longest a connection outlives a change made elsewhere: a session ended at another host (web's sign-out,
    /// auth's front-channel logout), or an agent's revocation, key rotation or tenant deletion on another api replica
    /// or on Azure SignalR Service. Changes made through this api replica apply at once.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:05:00")]
    public TimeSpan AccessCheckInterval { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>An open hub connection, and the groups its access put it in.</summary>
public sealed class OpenHubConnection(HubCallerContext context)
{
    public HubCallerContext Context { get; } = context;

    /// <summary>The token's subject: a person, an agent, or an API application acting as itself.</summary>
    public string? Subject { get; } = context.User?.GetClaim(OpenIddictConstants.Claims.Subject);

    internal HashSet<string> Groups { get; } = [];

    /// <summary>One check at a time, so the last to run applies what it read last.</summary>
    internal SemaphoreSlim Checking { get; } = new(1, 1);

    internal bool Closed { get; set; }
}

/// <summary>
/// The connections to <typeparamref name="THub"/> this api replica holds. SignalR can't close a connection from outside
/// its hub, or say which groups it's in, so the hub records each one here.
/// </summary>
public sealed class HubConnections<THub> where THub : Hub
{
    private readonly ConcurrentDictionary<string, OpenHubConnection> _open = new();

    internal OpenHubConnection Add(HubCallerContext context) => _open[context.ConnectionId] = new OpenHubConnection(context);

    internal void Remove(HubCallerContext context) => _open.TryRemove(context.ConnectionId, out _);

    internal IReadOnlyList<OpenHubConnection> Where(Func<OpenHubConnection, bool> match) => [.. _open.Values.Where(match)];

    /// <summary>Closes <paramref name="connection"/>, which nothing then re-checks.</summary>
    internal void Close(OpenHubConnection connection)
    {
        connection.Closed = true;
        Remove(connection.Context);
        connection.Context.Abort();
    }
}

/// <summary>Whether the access token a hub connection was opened with is still good.</summary>
internal static class HubTokens
{
    /// <summary>
    /// Not revoked, by itself or with its authorization (sign-out, front-channel logout, a revoked connection, role or
    /// agent), and not expired.
    /// </summary>
    public static Task<bool> IsLiveAsync(Signal9DbContext db, System.Security.Claims.ClaimsPrincipal user, TimeProvider time,
        CancellationToken cancellationToken)
    {
        if (user.GetTokenId() is not { } tokenId)
            return Task.FromResult(false);
        var now = time.GetUtcNow().UtcDateTime;
        return db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking()
            .AnyAsync(t => t.Id == tokenId && t.Status == OpenIddictConstants.Statuses.Valid && t.ExpirationDate > now
                && (t.Authorization == null || t.Authorization.Status == OpenIddictConstants.Statuses.Valid), cancellationToken);
    }
}

/// <summary>
/// Re-checks every open hub connection this api replica holds on an interval (<see cref="HubAccessOptions"/>), so a
/// change made elsewhere closes or narrows it here too.
/// </summary>
public sealed class HubAccessSweeper(
    IServiceScopeFactory scopes,
    IOptions<HubAccessOptions> options,
    TimeProvider time,
    ILogger<HubAccessSweeper> logger) : PeriodicJob(options.Value.AccessCheckInterval, scopes, time, logger)
{
    protected override async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await services.GetRequiredService<AgentHubAccess>().RecheckAllAsync(cancellationToken);
        await services.GetRequiredService<DashboardAccess>().RecheckAllAsync(cancellationToken);
    }
}
