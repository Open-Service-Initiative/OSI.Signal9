using Microsoft.Extensions.Configuration;

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

// People sign in at auth through the operator's own multi-tenant Entra app registration (ADR-0002). Its credential is a
// certificate locally and a federated identity credential backed by auth's managed identity when published
// (stories 124, 125); never a secret.
var entraClientId = builder.AddParameter("entra-client-id");
// The operator's own directory: only its people can be Superadministrators (ADR-0006).
var entraDeployingTenantId = builder.AddParameter("entra-deploying-tenant-id");
auth.WithEnvironment("Entra__ClientId", entraClientId)
    .WithEnvironment("Entra__DeployingTenantId", entraDeployingTenantId);
if (builder.ExecutionContext.IsRunMode)
{
    auth.WithEnvironment("Entra__ClientCredentials__0__SourceType", "Path")
        .WithEnvironment("Entra__ClientCredentials__0__CertificateDiskPath", LocalEntraCredential.EnsureCertificate());
}
else
{
    auth.WithEnvironment("Entra__ClientCredentials__0__SourceType", "SignedAssertionFromManagedIdentity");
}

var api = builder.AddProject<Projects.OSI_Signal9_API>("api")
    .WithReference(database)
    .WaitFor(database)
    .WithExternalHttpEndpoints();

// The dashboard's backend-for-frontend (ADR-0003): browsers only ever talk to web and auth.
var web = builder.AddProject<Projects.OSI_Signal9_Web>("web")
    .WithReference(database)
    .WaitFor(database)
    .WaitFor(auth)
    .WaitFor(api)
    .WithExternalHttpEndpoints();

// Real-time (ADR-0009, stories 116-117), chosen by configuration: Realtime:Hosting is AzureSignalR (the default when
// published) or SelfHosted (the default locally, where Azure SignalR has no emulator for hub servers). On Azure SignalR
// Service both hubs run there, and the dashboard reaches it through web. Self-hosted, the hubs run in api, which needs
// Redis only once Realtime:ApiReplicas is more than one.
var realtime = builder.Configuration["Realtime:Hosting"] ?? (builder.ExecutionContext.IsRunMode ? "SelfHosted" : "AzureSignalR");
if (string.Equals(realtime, "AzureSignalR", StringComparison.OrdinalIgnoreCase))
{
    var signalr = builder.AddAzureSignalR("signalr");
    api.WithReference(signalr)
        .WithEnvironment("Realtime__Hosting", "AzureSignalR");
    web.WithEnvironment("Realtime__Hosting", "AzureSignalR")
        .WithEnvironment("Realtime__ServiceEndpoint", ReferenceExpression.Create($"https://{signalr.Resource.HostName}/"));
}
else
{
    // api and web refuse to start without Realtime:Hosting, so it's set in every mode.
    api.WithEnvironment("Realtime__Hosting", "SelfHosted");
    web.WithEnvironment("Realtime__Hosting", "SelfHosted");
    if (builder.Configuration.GetValue("Realtime:ApiReplicas", 1) is var replicas and > 1)
    {
        var redis = builder.AddRedis("redis");
        api.WithReference(redis)
            .WaitFor(redis)
            .WithReplicas(replicas);
    }
}

var issuer = ReferenceExpression.Create($"{auth.GetEndpoint("https")}/");
var apiUri = ReferenceExpression.Create($"{api.GetEndpoint("https")}/");
var webUri = ReferenceExpression.Create($"{web.GetEndpoint("https")}/");
web.WithEnvironment("Identity__WebUri", webUri);
// An accepted invitation on auth links to the dashboard (#121).
auth.WithEnvironment("Identity__WebUri", webUri);
foreach (var host in new IResourceBuilder<ProjectResource>[] { auth, api, web })
{
    host.WithEnvironment("Identity__Issuer", issuer)
        .WithEnvironment("Identity__ApiUri", apiUri);

    // Locally a certificate is the key-encryption key that wraps the stored keys and web's client certificate
    // (ADR-0008); when published it's a Key Vault key reached by managed identity, which #77 provisions.
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

builder.Build().Run();
