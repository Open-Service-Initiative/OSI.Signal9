using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Signal9.Agent.Services;
using Signal9.Shared.Configuration;

var builder = Host.CreateApplicationBuilder(args);

// Add configuration
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// Add logging
builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
    if (OperatingSystem.IsWindows())
    {
        logging.AddEventLog();
    }
});

// Add configuration options
builder.Services.Configure<AgentConfiguration>(
    builder.Configuration.GetSection("AgentConfiguration"));

// Add services
builder.Services.AddSingleton<ITelemetryCollector, TelemetryCollector>();
builder.Services.AddHostedService<AgentService>();

var host = builder.Build();

// Create logger for startup
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Signal9 Agent starting up");

try
{
    await host.RunAsync();
}
catch (Exception ex)
{
    logger.LogCritical(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    logger.LogInformation("Signal9 Agent shutting down");
}