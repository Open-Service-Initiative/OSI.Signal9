using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// OpenIddict's authorization manager, where revoking an authorization also revokes every token issued under it (#63).
/// OpenIddict 7.x only marks the authorization itself as revoked, so its tokens would otherwise keep their status.
/// Every way of revoking goes through here, OpenIddict's own included.
/// </summary>
public sealed class Signal9AuthorizationManager<TAuthorization>(
    IOpenIddictAuthorizationCache<TAuthorization> cache,
    ILogger<OpenIddictAuthorizationManager<TAuthorization>> logger,
    IOptionsMonitor<OpenIddictCoreOptions> options,
    IOpenIddictAuthorizationStore<TAuthorization> store,
    IOpenIddictTokenManager tokens)
    : OpenIddictAuthorizationManager<TAuthorization>(cache, logger, options, store)
    where TAuthorization : class
{
    public override async ValueTask<bool> TryRevokeAsync(TAuthorization authorization, CancellationToken cancellationToken = default)
    {
        var revoked = await base.TryRevokeAsync(authorization, cancellationToken);
        // Also when another request revoked it first: its tokens are revoked either way.
        if (revoked || await HasStatusAsync(authorization, OpenIddictConstants.Statuses.Revoked, cancellationToken))
            await tokens.RevokeByAuthorizationIdAsync((await GetIdAsync(authorization, cancellationToken))!, cancellationToken);
        return revoked;
    }

    public override async ValueTask<long> RevokeAsync(string? subject, string? client, string? status, string? type,
        CancellationToken cancellationToken = default)
    {
        var ids = new List<string>();
        await foreach (var authorization in FindAsync(subject, client, status, type, scopes: null, cancellationToken))
            ids.Add((await GetIdAsync(authorization, cancellationToken))!);
        var count = await base.RevokeAsync(subject, client, status, type, cancellationToken);
        foreach (var id in ids)
            await tokens.RevokeByAuthorizationIdAsync(id, cancellationToken);
        return count;
    }

    public override async ValueTask<long> RevokeByApplicationIdAsync(string identifier, CancellationToken cancellationToken = default)
    {
        var count = await base.RevokeByApplicationIdAsync(identifier, cancellationToken);
        await tokens.RevokeByApplicationIdAsync(identifier, cancellationToken);
        return count;
    }

    public override async ValueTask<long> RevokeBySubjectAsync(string subject, CancellationToken cancellationToken = default)
    {
        var count = await base.RevokeBySubjectAsync(subject, cancellationToken);
        await tokens.RevokeBySubjectAsync(subject, cancellationToken);
        return count;
    }
}
