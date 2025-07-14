using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Signal9.Shared.Data;
using Signal9.Shared.Interfaces;
using Signal9.Shared.Services;

namespace Signal9.Shared.Configuration;

/// <summary>
/// Service collection extensions for Signal9 RMM Platform
/// Minimal configuration focusing on core services only
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add Signal9 database services
    /// </summary>
    public static IServiceCollection AddSignal9Data(this IServiceCollection services, IConfiguration configuration)
    {
        // Add Entity Framework DbContext
        services.AddDbContext<Signal9DbContext>(options =>
        {
            var connectionString = configuration.GetConnectionString("DefaultConnection");
            options.UseSqlServer(connectionString);

            // Enable detailed errors in development
            if (configuration["Environment"] == "Development")
            {
                options.EnableSensitiveDataLogging();
            }
        });

        // Register core services
        services.AddSingleton<ITableStorageService, TableStorageService>();
        services.AddScoped<IAgentHubService, AgentHubService>();
        
        // TODO: Add back other services when interfaces are fixed
        // services.AddScoped<IRelationalDataService, RelationalDataService>();

        return services;
    }
}
