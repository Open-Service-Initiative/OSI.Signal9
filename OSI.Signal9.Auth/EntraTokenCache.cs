using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web.TokenCacheProviders;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.Auth;

/// <summary>
/// auth's MSAL token cache for Entra, in the database (spec #63: "a distributed, encrypted token cache"): each
/// account's cache is encrypted with Data Protection, whose keys are in the database wrapped by the key-encryption key,
/// so every auth replica shares it and the hourly Entra check finds the person's refresh token after a restart.
/// </summary>
public sealed class EntraTokenCache(IDataProtectionProvider dataProtection, IServiceScopeFactory scopes, TimeProvider time)
    : MsalAbstractTokenCacheProvider(dataProtection.CreateProtector("OSI.Signal9.Auth.EntraTokenCache"))
{
    protected override async Task WriteCacheBytesAsync(string cacheKey, byte[] bytes)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();
        var now = time.GetUtcNow().UtcDateTime;
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "EntraTokenCache" ("Key", "Value", "UpdatedAt") VALUES ({cacheKey}, {bytes}, {now})
            ON CONFLICT ("Key") DO UPDATE SET "Value" = excluded."Value", "UpdatedAt" = excluded."UpdatedAt"
            """);
    }

    protected override async Task<byte[]?> ReadCacheBytesAsync(string cacheKey)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();
        // No entry is an empty cache.
        return await db.EntraTokenCache.AsNoTracking().Where(e => e.Key == cacheKey).Select(e => e.Value)
            .SingleOrDefaultAsync();
    }

    protected override async Task RemoveKeyAsync(string cacheKey)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();
        await db.EntraTokenCache.Where(e => e.Key == cacheKey).ExecuteDeleteAsync();
    }
}
