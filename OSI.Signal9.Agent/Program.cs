using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Options;
using OSI.Signal9.Agent;
using OSI.Signal9.Agent.Services;
using OSI.Signal9.Contracts.Hubs;
using OSI.Signal9.Contracts.Serialization;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "Signal9 Agent");

builder.Services.AddOptions<AgentOptions>()
    .BindConfiguration(AgentOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddHttpClient<AgentApiClient>((sp, http) =>
        http.BaseAddress = sp.GetRequiredService<IOptions<AgentOptions>>().Value.ApiBaseUrl)
    .AddStandardResilienceHandler();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SystemInfoProvider>();
builder.Services.AddSingleton<TelemetryCollector>();
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddSingleton<AgentHubConnectionFactory>(sp =>
{
    var apiBaseUrl = sp.GetRequiredService<IOptions<AgentOptions>>().Value.ApiBaseUrl;
    return agentId => new HubConnectionBuilder()
        .WithUrl(new Uri(apiBaseUrl, $"{HubRoutes.Agents.TrimStart('/')}?agentId={agentId}"))
        .AddJsonProtocol(o => Signal9Json.Configure(o.PayloadSerializerOptions))
        .WithAutomaticReconnect()
        .Build();
});
builder.Services.AddSingleton<AgentSession>();
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
