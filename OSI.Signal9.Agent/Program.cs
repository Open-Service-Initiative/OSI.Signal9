using OSI.Signal9.Agent;
using OSI.Signal9.Agent.Identity;
using OSI.Signal9.Agent.Platforms;
using OSI.Signal9.Agent.Services;

var builder = Host.CreateApplicationBuilder(args);

// Everything the agent reads from its OS, and where it keeps state there, comes from the running OS's platform.
IPlatform platform = OperatingSystem.IsWindows() ? new WindowsPlatform()
    : OperatingSystem.IsLinux() ? new LinuxPlatform()
    : OperatingSystem.IsMacOS() ? new MacOSPlatform()
    : throw new PlatformNotSupportedException("The Signal9 agent runs on Windows, Linux and macOS.");

// A Windows service, a systemd service (Type=notify, logging to the journal), or a launchd daemon: launchd needs no
// integration, since the console lifetime stops on its SIGTERM. Each is a no-op when the agent isn't run that way.
builder.Services.AddWindowsService(options => options.ServiceName = "Signal9 Agent");
builder.Services.AddSystemd();

builder.Services.AddOptions<AgentOptions>()
    .BindConfiguration(AgentOptions.SectionName)
    .PostConfigure(options => options.StateDirectory ??= platform.StateDirectory)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(platform);
builder.Services.AddSingleton<IAgentKeyStore>(sp => OperatingSystem.IsWindows()
    ? new CngAgentKeyStore(sp.GetRequiredService<ILogger<CngAgentKeyStore>>())
    : new UnsupportedAgentKeyStore());
builder.Services.AddSingleton<AgentIdentityStore>();
builder.Services.AddSingleton<IAgentTransport, MutualTlsTransport>();
builder.Services.AddSingleton<AgentEnrollment>();
builder.Services.AddSingleton<AgentCertificateRotation>();
builder.Services.AddSingleton<IMachineFactsSource, SystemInfoProvider>();
builder.Services.AddSingleton<ITelemetrySource, TelemetryCollector>();
builder.Services.AddSingleton<ICommandHandler, CollectSystemInfoHandler>();
builder.Services.AddSingleton<CommandExecutor>();
builder.Services.AddSingleton<AgentSession>();
builder.Services.AddHostedService<AgentWorker>();

await builder.Build().RunAsync();
