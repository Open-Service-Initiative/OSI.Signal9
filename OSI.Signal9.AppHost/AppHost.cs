var builder = DistributedApplication.CreateBuilder(args);

// Local development topology. The Agent is not orchestrated here: it runs as a Windows Service on managed machines.

// Azure Database for PostgreSQL Flexible Server when published; a postgres:18 container locally (ADR-0011).
var database = builder.AddAzurePostgresFlexibleServer("postgres")
    .RunAsContainer(container => container.WithImageTag("18").WithDataVolume())
    .AddDatabase("signal9");

// The OAuth authorization server, split across auth and api (ADR-0003): both share the database, keys and issuer.
var auth = builder.AddProject<Projects.OSI_Signal9_Auth>("auth")
    .WithReference(database)
    .WaitFor(database)
    .WithExternalHttpEndpoints();

var api = builder.AddProject<Projects.OSI_Signal9_API>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithExternalHttpEndpoints();

var issuer = ReferenceExpression.Create($"{auth.GetEndpoint("https")}/");
var apiUri = ReferenceExpression.Create($"{api.GetEndpoint("https")}/");
foreach (var host in new IResourceBuilder<ProjectResource>[] { auth, api })
{
    host.WithEnvironment("Identity__Issuer", issuer)
        .WithEnvironment("Identity__ApiUri", apiUri);
}

// The Blazor WebAssembly app reads the API address from wwwroot/appsettings.*.json, so this only starts it.
builder.AddProject<Projects.OSI_Signal9_Web>("web")
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
