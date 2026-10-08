using Microsoft.Extensions.Options;
using OSI.Signal9.Agent;
using OSI.Signal9.Agent.Services;

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
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
