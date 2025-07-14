var builder = DistributedApplication.CreateBuilder(args);

// Signal9 RMM Platform Orchestration
// Note: Signal9.Agent is NOT included - it runs on managed customer machines

var webFunctions = builder.AddProject<Projects.Signal9_Web_Functions>("web-functions");

var agentFunctions = builder.AddProject<Projects.Signal9_Agent_Functions>("agent-functions");

var web = builder.AddProject<Projects.Signal9_Web>("web")
    .WithReference(webFunctions)
    .WithReference(agentFunctions)
    .WithExternalHttpEndpoints()
    .WaitFor(webFunctions)
    .WaitFor(agentFunctions);

builder.Build().Run();
