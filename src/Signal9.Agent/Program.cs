using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Signal9.Agent.Services;
using Signal9.Shared.Configuration;
using System.Text.Json;

var builder = Host.CreateApplicationBuilder(args);

// Add configuration with .NET 9 enhancements
builder.Configuration
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>(optional: true);

// Add enhanced logging with .NET 9 structured logging
builder.Services.AddLogging(logging =>
{
    logging.ClearProviders();
    logging.AddConsole();
    
    // Add structured logging with .NET 9 improvements
    logging.AddSimpleConsole(options =>
    {
        options.IncludeScopes = true;
        options.SingleLine = true;
        options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ";
    });
});

// Add configuration options with validation
builder.Services.Configure<AgentConfiguration>(
    builder.Configuration.GetSection("AgentConfiguration"));

// Add services with .NET 9 performance improvements
builder.Services.AddSingleton<ITelemetryCollector, TelemetryCollector>();
builder.Services.AddSingleton<ISystemInfoProvider, SystemInfoProvider>();
builder.Services.AddHostedService<AgentService>();

// Add HttpClient with .NET 9 optimizations
builder.Services.AddHttpClient("Signal9Api", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Signal9Api:BaseUrl"] ?? "https://api.signal9.com");
    client.Timeout = TimeSpan.FromSeconds(30);
});

var host = builder.Build();

// Create logger for startup
var logger = host.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Signal9 Agent starting up with .NET 9 optimizations");

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
    await host.StopAsync();
}
