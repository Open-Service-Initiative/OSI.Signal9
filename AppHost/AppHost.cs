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
    .WaitFor(cosmos)
    .WithReference(keyVault);

var web = builder.AddProject<Projects.Signal9_Web>("swa-signal9")
    .WithReference(webFunctions)
    .WithReference(agentFunctions)
    .WithExternalHttpEndpoints()
    .WaitFor(webFunctions)
    .WaitFor(agentFunctions);

builder.Build().Run();
