using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// OpenIddict's application manager, with <c>tls_client_auth</c> as RFC 8705 §2.1.2 defines it: a CA-issued
/// certificate is accepted when it chains to a trusted authority and matches the subject the client registered.
/// OpenIddict's default instead matches the certificate against the client's redirect URI hosts, which an
/// application acting as itself doesn't have.
/// </summary>
public sealed class Signal9ApplicationManager<TApplication>(
    IOpenIddictApplicationCache<TApplication> cache,
    ILogger<OpenIddictApplicationManager<TApplication>> logger,
    IOptionsMonitor<OpenIddictCoreOptions> options,
    IOpenIddictApplicationStore<TApplication> store)
    : OpenIddictApplicationManager<TApplication>(cache, logger, options, store)
    where TApplication : class
{
    public override async ValueTask<bool> ValidatePublicKeyInfrastructureTlsClientCertificateAsync(
        TApplication application, X509Certificate2 certificate, X509ChainPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(policy);

        var clientId = await GetClientIdAsync(application, cancellationToken);
        if (TlsClientSubject.From(await GetPropertiesAsync(application, cancellationToken)) is not { } subject)
        {
            Logger.LogInformation("Client {ClientId} has no single registered tls_client_auth subject.", clientId);
            return false;
        }

        // The policy is OpenIddict's: the configured authorities only (custom root trust), and the clientAuth EKU.
        using var chain = new X509Chain { ChainPolicy = policy };
        try
        {
            if (!chain.Build(certificate) || chain.ChainElements.Count < 2)
            {
                Logger.LogInformation("The certificate of client {ClientId} doesn't chain to a trusted authority: {Status}.",
                    clientId, chain.ChainStatus.Select(status => status.Status).ToArray());
                return false;
            }

            if (!subject.Matches(certificate))
            {
                Logger.LogInformation("The certificate of client {ClientId} doesn't match its registered {Parameter}.",
                    clientId, subject.Parameter);
                return false;
            }

            return true;
        }
        catch (CryptographicException exception)
        {
            Logger.LogWarning(exception, "The certificate of client {ClientId} couldn't be validated.", clientId);
            return false;
        }
        finally
        {
            foreach (var element in chain.ChainElements)
                element.Certificate.Dispose();
        }
    }

    /// <summary>
    /// A confidential client normally needs a secret or a signing key. A <c>tls_client_auth</c> client has neither: its
    /// credential is the registered certificate subject.
    /// </summary>
    public override async IAsyncEnumerable<ValidationResult> ValidateAsync(TApplication application,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var authenticatesWithSubject =
            TlsClientSubject.From(await GetPropertiesAsync(application, cancellationToken)) is not null;
        var missingCredential = OpenIddictResources.GetResourceString(OpenIddictResources.ID2113);

        await foreach (var result in base.ValidateAsync(application, cancellationToken))
        {
            if (authenticatesWithSubject && result.ErrorMessage == missingCredential)
                continue;
            yield return result;
        }
    }
}
