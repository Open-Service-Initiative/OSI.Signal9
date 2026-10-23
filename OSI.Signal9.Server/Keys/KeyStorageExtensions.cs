using System.ComponentModel.DataAnnotations;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
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

        // Data Protection protects only cookies and the token cache.
        services.AddDataProtection()
            .SetApplicationName(DataProtectionApplicationName)
            .PersistKeysToDbContext<Signal9DbContext>();
        services.AddOptions<KeyManagementOptions>()
            .Configure<KeyEncryptionKey>((options, kek) => options.XmlEncryptor = new KekXmlEncryptor(kek));
        return services;
    }

    // Managed identity only: never a developer, CLI or environment credential that could stand in for it on Azure.
    private static TokenCredential ManagedIdentity(string? clientId) =>
        new ManagedIdentityCredential(clientId is { Length: > 0 }
            ? ManagedIdentityId.FromUserAssignedClientId(clientId)
            : ManagedIdentityId.SystemAssigned);
}
