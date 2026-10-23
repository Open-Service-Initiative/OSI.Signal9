using Microsoft.EntityFrameworkCore;
using OSI.Signal9.API.Agents;
using OSI.Signal9.API.Data;
using OSI.Signal9.API.Hubs;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Migrations never run here; see README.md for how to apply them.
builder.Services.AddDbContext<Signal9DbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("signal9")));
builder.EnrichAzureNpgsqlDbContext<Signal9DbContext>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<AgentPresenceOptions>().BindConfiguration(AgentPresenceOptions.SectionName);
builder.Services.AddSingleton<AgentPresence>();

builder.Services.AddControllers()
    .AddJsonOptions(options => Signal9Json.Configure(options.JsonSerializerOptions));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSignalR()
    .AddJsonProtocol(options => Signal9Json.Configure(options.PayloadSerializerOptions));

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// TODO(auth): add Entra ID authentication for dashboard users and agent credentials, then require them.

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();

app.MapControllers();
app.MapHub<AgentHub>(HubRoutes.Agents);
app.MapHub<DashboardHub>(HubRoutes.Dashboard);
app.MapDefaultEndpoints();

app.Run();

public partial class Program;
