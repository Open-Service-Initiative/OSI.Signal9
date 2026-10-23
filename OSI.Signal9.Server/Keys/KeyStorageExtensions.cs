using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Internal;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using OpenIddict.Validation;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.Server.Keys;

public static class KeyStorageExtensions
{
    /// <summary>The Data Protection application name every Signal9 host shares, so they share one key ring.</summary>
    public const string DataProtectionApplicationName = "Signal9";

    /// <summary>Registers the key-encryption key that the <c>KeyEncryption</c> section chooses.</summary>
    public static IServiceCollection AddSignal9KeyEncryption(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(_ =>
        {
            var options = configuration.GetSection(KeyEncryptionOptions.SectionName).Get<KeyEncryptionOptions>()
                ?? new KeyEncryptionOptions();
            Validator.ValidateObject(options, new ValidationContext(options), validateAllProperties: true);
            return options.Provider switch
            {
                KeyEncryptionProvider.Certificate => (KeyEncryptionKey)CertificateKeyEncryptionKey.Load(options.Certificate),
                KeyEncryptionProvider.KeyVault => new KeyVaultKeyEncryptionKey(
                    options.KeyVault.KeyId ?? throw new InvalidOperationException(
                        "KeyEncryption:KeyVault:KeyId is required for the KeyVault provider."),
                    ManagedIdentity(options.KeyVault.ManagedIdentityClientId)),
                _ => throw new InvalidOperationException($"Unknown key-encryption provider {options.Provider}."),
            };
        });
        return services;
    }

    /// <summary>
    /// Stores the authorization server's keys and the Data Protection key ring in the database, both wrapped by the
    /// key-encryption key (ADR-0008).
    /// </summary>
    public static IServiceCollection AddSignal9Keys(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSignal9KeyEncryption(configuration);
        services.TryAddSingleton<StoredKeys>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<OpenIddictServerOptions>, ConfigureStoredKeys>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOptionsChangeTokenSource<OpenIddictServerOptions>, StoredKeysChangeTokenSource>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IOptionsChangeTokenSource<OpenIddictValidationOptions>, LocalServerKeysChangeTokenSource>());

        // Data Protection protects only cookies and the token cache.
        services.AddDataProtection()
            .SetApplicationName(DataProtectionApplicationName)
            .PersistKeysToDbContext<Signal9DbContext>();
        services.AddOptions<KeyManagementOptions>()
            .Configure<KeyEncryptionKey>((options, kek) => options.XmlEncryptor = new KekXmlEncryptor(kek))
            .PostConfigure<IActivator, ILoggerFactory>(FailClosedOnUnreadableRing);
        return services;
    }

    /// <summary>
    /// A host whose key-encryption key can't read any key in a non-empty ring is misconfigured. Rather than add a key
    /// of its own to the shared ring, which no other host could read, it stops generating keys, so protecting and
    /// unprotecting fail on it until its key-encryption key is fixed.
    /// </summary>
    private static void FailClosedOnUnreadableRing(KeyManagementOptions options, IActivator activator, ILoggerFactory loggers)
    {
        IReadOnlyCollection<IKey> keys;
        try
        {
            keys = new XmlKeyManager(Options.Create(options), activator, loggers).GetAllKeys();
        }
        catch (DbException exception)
        {
            // An unreachable or unmigrated database says nothing about the key-encryption key, and hosts may start
            // before migrations are applied. Data Protection retries the ring itself; it can't add a key until the
            // database is back.
            loggers.CreateLogger(typeof(KeyStorageExtensions)).LogWarning(exception,
                "Couldn't read the Data Protection key ring to check that this host's key-encryption key can decrypt it.");
            return;
        }

        if (keys.Count > 0 && !keys.Any(CanDecrypt))
        {
            loggers.CreateLogger(typeof(KeyStorageExtensions)).LogCritical(
                "This host's key-encryption key can't decrypt any of the {Count} keys in the Data Protection key ring, so it won't add one; protecting and unprotecting will fail until the key-encryption key is fixed.",
                keys.Count);
            options.AutoGenerateKeys = false;
        }
    }

    private static bool CanDecrypt(IKey key)
    {
        try
        {
            _ = key.Descriptor;
            return true;
        }
        catch (Exception)
        {
            // Wrong key-encryption key, or a corrupt key element: either way this host can't use it.
            return false;
        }
    }

    // Managed identity only: never a developer, CLI or environment credential that could stand in for it on Azure.
    private static TokenCredential ManagedIdentity(string? clientId) =>
        new ManagedIdentityCredential(clientId is { Length: > 0 }
            ? ManagedIdentityId.FromUserAssignedClientId(clientId)
            : ManagedIdentityId.SystemAssigned);
}
