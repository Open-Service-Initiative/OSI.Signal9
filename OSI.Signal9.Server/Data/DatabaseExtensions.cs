using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace OSI.Signal9.Server.Data;

public static class DatabaseExtensions
{
    /// <summary>
    /// Registers <see cref="Signal9DbContext"/> on the shared PostgreSQL database (ADR-0011). Migrations never run here;
    /// see README.md for how to apply them.
    /// </summary>
    public static IHostApplicationBuilder AddSignal9Database(this IHostApplicationBuilder builder)
    {
        builder.Services.AddDbContext<Signal9DbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString("signal9")));
        builder.EnrichAzureNpgsqlDbContext<Signal9DbContext>();
        // Fails closed: a host that doesn't register its callers' role assignments (api does) sees no tenant-owned rows.
        builder.Services.TryAddSingleton<IAssignedTenants>(NoAssignedTenants.Instance);
        return builder;
    }
}
