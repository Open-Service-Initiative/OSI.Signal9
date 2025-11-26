var builder = DistributedApplication.CreateBuilder(args);

// Signal9 RMM Platform Orchestration
// Note: Signal9.Agent is NOT included - it runs on managed customer machines

var signalR = builder.AddAzureSignalR("signalr-signal9")
    .RunAsEmulator()
    .WithExternalHttpEndpoints();

var cosmos = builder.AddAzureCosmosDB("cosmos-signal9")
    .RunAsEmulator()
    .WithExternalHttpEndpoints();

var keyVault = builder.AddAzureKeyVault("kv-signal9");

var agentFunctions = builder.AddAzureFunctionsProject<Projects.Signal9_Agent_Functions>("func-signal9-agent")
    .WithReference(signalR)
    .WithReference(cosmos)
    .WaitFor(signalR)
    .WaitFor(cosmos)
    .WithReference(keyVault);

var webFunctions = builder.AddAzureFunctionsProject<Projects.Signal9_Web_Functions>("func-signal9-web")
    .WithReference(cosmos)
    .WithExternalHttpEndpoints()
    .WithHttpEndpoint(port: 7072, targetPort: 7071, name: "webfunc-http")
    .WaitFor(cosmos)
    .WithReference(keyVault);

var webProjectHostPath = Path.Combine("..", "Signal9.Web");
var webProjectContainerPath = "/workspace/Signal9.Web";
var webRunCommand = $"dotnet watch --project {webProjectContainerPath}/Signal9.Web.csproj";

var swaDevServer = builder.AddContainer("swa-signal9-dev", "swacli/static-web-apps-cli", "latest")
    .WithBindMount(webProjectHostPath, webProjectContainerPath)
    .WithEnvironment("HOME", "/tmp/swa")
    .WithEnvironment("DOTNET_CLI_HOME", "/tmp/dotnet")
    .WithEnvironment("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1")
    .WithHttpEndpoint(port: 4280, targetPort: 4280, name: "swa-ui")
    .WithHttpEndpoint(port: 4281, targetPort: 4281, name: "swa-auth")
    .WithArgs(
        "swa", "start", $"{webProjectContainerPath}/wwwroot",
        "--host", "0.0.0.0",
        "--port", "4280",
        "--api-port", "4281",
        "--api-location", "http://host.docker.internal:7071",
        "--run", webRunCommand
    )
    .WithReference(webFunctions)
    .WaitFor(webFunctions);

// var web = builder.AddProject<Projects.Signal9_Web>("swa-signal9")
//     .WithReference(webFunctions)
//     .WithReference(agentFunctions)
//     .WithExternalHttpEndpoints()
//     .WaitFor(webFunctions)
//     .WaitFor(agentFunctions);

builder.Build().Run();
