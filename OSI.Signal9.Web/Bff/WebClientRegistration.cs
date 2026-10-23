using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace OSI.Signal9.Web.Bff;

/// <summary>
/// web's registration at the authorization server: a confidential client using authorization code with PKCE
/// (ADR-0003), that authenticates with <c>self_signed_tls_client_auth</c> (RFC 8705 §2.2) using the certificates in
/// its key set. web keeps it current itself, since it holds the certificates and knows its own address.
/// </summary>
public sealed class WebClientRegistration(IServiceScopeFactory scopes, IConfiguration configuration)
{
    public const string ClientId = AuthorizationServer.DashboardClientId;

    /// <summary>Key of the transaction-level advisory lock held while web's registration changes.</summary>
    public const long LockKey = 0x5367_3957_6562_436C; // "Sg9WebCl"

    /// <summary>
    /// The scopes the dashboard asks for: every catalogue scope a person can hold. Tokens carry identity only;
    /// what the person may actually do is checked live at api (ADR-0007). <c>offline_access</c> brings a refresh token.
    /// </summary>
    public static readonly string[] Scopes =
    [
        .. OSI.Signal9.Server.Identity.Scopes.All.Keys.Except(
            [OSI.Signal9.Server.Identity.Scopes.Agent, OpenIddictConstants.Scopes.Profile]),
    ];

    /// <summary>The redirect URI web's sign-in returns to.</summary>
    public const string CallbackPath = "/signin-oidc";

    /// <summary>Registers web, or brings its registration up to date, with exactly these certificates.</summary>
    public void Register(IReadOnlyList<X509Certificate2> certificates)
    {
        var webUri = configuration.GetIdentityOptions().WebUri
            ?? throw new InvalidOperationException("Identity:WebUri must give web's public address.");

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = ClientId,
            ClientType = ClientTypes.Confidential,
            ApplicationType = ApplicationTypes.Web,
            DisplayName = "Signal9 dashboard",
            RedirectUris = { new Uri(webUri, CallbackPath.TrimStart('/')) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
            JsonWebKeySet = new JsonWebKeySet(),
        };
        foreach (var name in Scopes.Where(s => s != OpenIddictConstants.Scopes.OpenId))
            descriptor.Permissions.Add(Permissions.Prefixes.Scope + name);
        foreach (var certificate in certificates)
            // The public certificate only.
            descriptor.JsonWebKeySet.Keys.Add(JsonWebKeyConverter.ConvertFromX509SecurityKey(
                new X509SecurityKey(X509CertificateLoader.LoadCertificate(certificate.RawData))));

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        // Replicas may start together: one at a time, inside the execution strategy so a transient failure retries it.
        db.Database.CreateExecutionStrategy().Execute(() =>
        {
            db.ChangeTracker.Clear();
            using var transaction = db.Database.BeginTransaction();
            db.Database.ExecuteSql($"SELECT pg_advisory_xact_lock({LockKey})");
            var application = applications.FindByClientIdAsync(ClientId).AsTask().GetAwaiter().GetResult();
            if (application is null)
                applications.CreateAsync(descriptor).AsTask().GetAwaiter().GetResult();
            else
                applications.UpdateAsync(application, descriptor).AsTask().GetAwaiter().GetResult();
            transaction.Commit();
        });
    }
}
