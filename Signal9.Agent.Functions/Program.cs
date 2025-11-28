using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Signal9.Shared.Data;

var builder = FunctionsApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.ConfigureFunctionsWebApplication();

var connectionString = Environment.GetEnvironmentVariable("DefaultConnection");
if (string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddDbContext<Signal9DbContext>(options =>
        options.UseInMemoryDatabase("Signal9Dev"));
}
else
{
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