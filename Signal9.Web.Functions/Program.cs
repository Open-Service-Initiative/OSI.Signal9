using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Signal9.Shared.Data;
using System.Text.Json;

var builder = FunctionsApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Configure JSON serialization for the Functions Worker
builder.Services.Configure<JsonSerializerOptions>(options =>
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.WriteIndented = true;
    options.PropertyNameCaseInsensitive = true;
});

// Configure Entity Framework
var connectionString = Environment.GetEnvironmentVariable("DefaultConnection");
if (string.IsNullOrEmpty(connectionString))
{
    // Development fallback - use in-memory database
    builder.Services.AddDbContext<Signal9DbContext>(options =>
        options.UseInMemoryDatabase("Signal9Dev"));
}
else
{
    // Production - use SQL Server
    builder.Services.AddDbContext<Signal9DbContext>(options =>
        options.UseSqlServer(connectionString, sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 3,
                maxRetryDelay: TimeSpan.FromSeconds(5),
                errorNumbersToAdd: null);
        }));
}

builder.Build().Run();