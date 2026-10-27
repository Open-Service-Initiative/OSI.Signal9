using Microsoft.EntityFrameworkCore;
using Npgsql;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.API.Infrastructure;

internal static class UniqueViolations
{
    /// <summary>Saves, or returns false when a unique index refused the change: the resource already exists.</summary>
    public static async Task<bool> TrySaveUniqueAsync(this Signal9DbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }
}
