using System.ComponentModel.DataAnnotations;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Core;
using OSI.Signal9.Contracts.ApiApplications;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// OpenIddict's application manager, with <c>tls_client_auth</c> as RFC 8705 §2.1.2 defines it: a CA-issued
/// certificate is accepted when it chains to a trusted authority and matches the subject the client registered.
/// OpenIddict's default instead matches the certificate against the client's redirect URI hosts, which an
/// application acting as itself doesn't have. <c>self_signed_tls_client_auth</c> accepts only a certificate the client
/// registered, RSA or ECDSA (agents' TPM keys are ECDSA), which OpenIddict's own check would drop. Client certificates are checked for validity on the hosts'
/// <see cref="TimeProvider"/>, the clock every other expiry uses.
/// </summary>
public sealed class Signal9ApplicationManager<TApplication>(
    IOpenIddictApplicationCache<TApplication> cache,
    ILogger<OpenIddictApplicationManager<TApplication>> logger,
    IOptionsMonitor<OpenIddictCoreOptions> options,
    IOpenIddictApplicationStore<TApplication> store,
    TimeProvider time)
    : OpenIddictApplicationManager<TApplication>(cache, logger, options, store)
    where TApplication : class
{
    /// <summary>
    /// <c>self_signed_tls_client_auth</c> (RFC 8705 §2.2) for every client, agents and API applications alike, in place of
    /// OpenIddict's, which reads a JWK's certificate only for RSA keys. The certificate must be self-issued with the
    /// digitalSignature and clientAuth usages, and be exactly one the client registered in its key set as a signing
    /// JWK's <c>x5c</c>, holding that JWK's own key (RFC 7517 §4.7). Its chain, trusting only that certificate, then
    /// checks its signature, validity and the clientAuth usage under OpenIddict's policy.
    /// </summary>
    public override async ValueTask<bool> ValidateSelfSignedTlsClientCertificateAsync(TApplication application,
        X509Certificate2 certificate, X509ChainPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(policy);
        if (!await UsesAsync(application, TokenEndpointAuthMethods.SelfSignedTlsClientAuth, cancellationToken))
            return false;

        var clientId = await GetClientIdAsync(application, cancellationToken);
        if (!IsSelfIssuedClientCertificate(certificate))
        {
            Logger.LogInformation("The certificate client {ClientId} presented isn't a self-issued client certificate.", clientId);
            return false;
        }

        var keys = (await GetJsonWebKeySetAsync(application, cancellationToken))?.Keys ?? [];
        foreach (var key in keys)
        {
            using var registered = ClientCertificateKey.Certificate(key);
            if (registered is null || !registered.RawData.AsSpan().SequenceEqual(certificate.RawData))
                continue;

            var chainPolicy = Now(policy);
            chainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chainPolicy.CustomTrustStore.Clear();
            chainPolicy.CustomTrustStore.Add(registered);
            chainPolicy.ExtraStore.Clear();
            chainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            using var chain = new X509Chain { ChainPolicy = chainPolicy };
            try
            {
                if (chain.Build(certificate))
                    return true;
                Logger.LogInformation("The self-signed certificate of client {ClientId} isn't valid: {Status}.",
                    clientId, chain.ChainStatus.Select(status => status.Status).ToArray());
                return false;
            }
            finally
            {
                foreach (var element in chain.ChainElements)
                    element.Certificate.Dispose();
            }
        }

        Logger.LogInformation("Client {ClientId} presented a certificate it didn't register.", clientId);
        return false;
    }

    private static bool IsSelfIssuedClientCertificate(X509Certificate2 certificate) =>
        certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)
        && certificate.Extensions.OfType<X509KeyUsageExtension>().Any(u => u.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature))
        && certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.2"));

    /// <summary>An API application's key set is built from its credentials (<see cref="ApiApplicationClient"/>).</summary>
    public override async ValueTask<JsonWebKeySet?> GetJsonWebKeySetAsync(TApplication application,
        CancellationToken cancellationToken = default)
    {
        var properties = await GetPropertiesAsync(application, cancellationToken);
        return ApiApplicationClient.AuthMethod(properties) is null
            ? await base.GetJsonWebKeySetAsync(application, cancellationToken)
            : ApiApplicationClient.KeySet(ApiApplicationClient.Credentials(properties));
    }

    /// <summary>
    /// An API application's secrets are its unexpired <c>secret</c> credentials, any of which authenticates it, so a
    /// secret can be rotated without downtime. Only an application registered for client secrets has any.
    /// </summary>
    public override async ValueTask<bool> ValidateClientSecretAsync(TApplication application, string secret,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        var properties = await GetPropertiesAsync(application, cancellationToken);
        if (ApiApplicationClient.AuthMethod(properties) is not { } method)
            return await base.ValidateClientSecretAsync(application, secret, cancellationToken);
        if (method != TokenEndpointAuthMethods.ClientSecret || string.IsNullOrEmpty(secret))
            return false;

        var now = time.GetUtcNow();
        foreach (var credential in ApiApplicationClient.Credentials(properties))
        {
            if (credential.SecretHash is { } hash && credential.ExpiresAt > now
                && await ValidateClientSecretAsync(secret, hash, cancellationToken))
                return true;
        }
        Logger.LogInformation("Client {ClientId} presented no live secret.", await GetClientIdAsync(application, cancellationToken));
        return false;
    }

    /// <summary>Hashes a new client secret as OpenIddict stores its own, for an API application's credential.</summary>
    public ValueTask<string> HashClientSecretAsync(string secret, CancellationToken cancellationToken = default) =>
        ObfuscateClientSecretAsync(secret, cancellationToken);

    // A client that isn't an API application uses whatever credentials it was seeded with; an API application only
    // the method it registered (RFC 7591 token_endpoint_auth_method).
    private async ValueTask<bool> UsesAsync(TApplication application, string method, CancellationToken cancellationToken) =>
        ApiApplicationClient.AuthMethod(await GetPropertiesAsync(application, cancellationToken)) is not { } registered
        || registered == method;

    public override async ValueTask<bool> ValidatePublicKeyInfrastructureTlsClientCertificateAsync(
        TApplication application, X509Certificate2 certificate, X509ChainPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(policy);
        if (!await UsesAsync(application, TokenEndpointAuthMethods.TlsClientAuth, cancellationToken))
            return false;
        policy = Now(policy);

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

    /// <summary>OpenIddict's chain policy, checking validity at the current time on <see cref="TimeProvider"/>.</summary>
    private X509ChainPolicy Now(X509ChainPolicy policy)
    {
        var copy = policy.Clone();
        copy.VerificationTime = time.GetUtcNow().LocalDateTime;
        copy.VerificationTimeIgnored = false;
        return copy;
    }

    /// <summary>
    /// A confidential client normally needs a secret or a signing key. A <c>tls_client_auth</c> client has neither: its
    /// credential is the registered certificate subject.
    /// </summary>
    public override async IAsyncEnumerable<ValidationResult> ValidateAsync(TApplication application,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // An API application's credentials are added after it's registered, and live in its properties.
        var properties = await GetPropertiesAsync(application, cancellationToken);
        var authenticatesWithSubject = TlsClientSubject.From(properties) is not null
            || ApiApplicationClient.AuthMethod(properties) is not null;
        var missingCredential = OpenIddictResources.GetResourceString(OpenIddictResources.ID2113);

        await foreach (var result in base.ValidateAsync(application, cancellationToken))
        {
            if (authenticatesWithSubject && result.ErrorMessage == missingCredential)
                continue;
            yield return result;
        }
    }
}
