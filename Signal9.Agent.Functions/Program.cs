using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Signal9.Shared.Data;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        // Add Application Insights
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        
        // Add HTTP client
        services.AddHttpClient();

        // Configure Entity Framework for agent communication
        var connectionString = Environment.GetEnvironmentVariable("DefaultConnection");
        if (string.IsNullOrEmpty(connectionString))
        {
            services.AddDbContext<Signal9DbContext>(options =>
                options.UseInMemoryDatabase("Signal9Dev"));
        }
        else
        {
            services.AddDbContext<Signal9DbContext>(options =>
                options.UseSqlServer(connectionString, sqlOptions =>
                {
                    sqlOptions.EnableRetryOnFailure(
                        maxRetryCount: 3,
                        maxRetryDelay: TimeSpan.FromSeconds(5),
                        errorNumbersToAdd: null);
                }));
        }

        // Add health checks
        services.AddHealthChecks();
    })
    .Build();

// Ensure database is created in development
if (Environment.GetEnvironmentVariable("AZURE_FUNCTIONS_ENVIRONMENT") == "Development")
{
    using var scope = host.Services.CreateScope();
    var context = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();
    await context.Database.EnsureCreatedAsync();
}

host.Run();
