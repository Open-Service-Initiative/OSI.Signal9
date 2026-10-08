var builder = DistributedApplication.CreateBuilder(args);

// Local development topology. The Agent is not orchestrated here: it runs as a Windows Service on managed machines.

// Azure SQL when published; a SQL Server container locally.
var database = builder.AddAzureSqlServer("sql")
    .RunAsContainer(container => container.WithDataVolume())
    .AddDatabase("signal9");

var api = builder.AddProject<Projects.OSI_Signal9_API>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithExternalHttpEndpoints();

// The Blazor WebAssembly app reads the API address from wwwroot/appsettings.*.json, so this only starts it.
builder.AddProject<Projects.OSI_Signal9_Web>("web")
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
