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

    // Locally a certificate is the key-encryption key that wraps the stored keys (ADR-0008); when published it's a
    // Key Vault key reached by managed identity, which #77 provisions.
    if (builder.ExecutionContext.IsRunMode)
    {
        host.WithEnvironment("KeyEncryption__Provider", "Certificate")
            .WithEnvironment("KeyEncryption__Certificate__Path", LocalKeyEncryptionKey.EnsureCertificate());
    }

    // Health probes on their own internal port (story 134), added after WithExternalHttpEndpoints so it stays internal.
    host.WithHttpEndpoint(name: "probes");
    var probes = host.GetEndpoint("probes");
    host.WithEnvironment("HealthProbes__Port", probes.Property(EndpointProperty.TargetPort))
        .WithHttpHealthCheck("/health", endpointName: "probes");
}

// The Blazor WebAssembly app reads the API address from wwwroot/appsettings.*.json, so this only starts it.
builder.AddProject<Projects.OSI_Signal9_Web>("web")
    .WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
