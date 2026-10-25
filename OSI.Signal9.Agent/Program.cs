using OSI.Signal9.Agent;
using OSI.Signal9.Agent.Identity;
using OSI.Signal9.Agent.Services;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options => options.ServiceName = "Signal9 Agent");

builder.Services.AddOptions<AgentOptions>()
    .BindConfiguration(AgentOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAgentKeyStore>(sp => OperatingSystem.IsWindows()
    ? new CngAgentKeyStore(sp.GetRequiredService<ILogger<CngAgentKeyStore>>())
    : new UnsupportedAgentKeyStore());
builder.Services.AddSingleton<AgentIdentityStore>();
builder.Services.AddSingleton<IAgentTransport, MutualTlsTransport>();
builder.Services.AddSingleton<AgentEnrollment>();
builder.Services.AddSingleton<SystemInfoProvider>();
builder.Services.AddSingleton<TelemetryCollector>();
builder.Services.AddSingleton<ICommandHandler, CollectSystemInfoHandler>();
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddSingleton<AgentSession>();
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
