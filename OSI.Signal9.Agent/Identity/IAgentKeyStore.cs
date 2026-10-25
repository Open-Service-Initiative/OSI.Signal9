using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace OSI.Signal9.Agent.Identity;

/// <summary>
/// Where an agent instance's private key lives: generated on the machine and never exportable, so its credential can't
/// be copied off it (story 98). Keys are ECDSA P-256, which TPMs support.
/// </summary>
public interface IAgentKeyStore
{
    /// <summary>Generates a new non-exportable key named <paramref name="name"/>, replacing any key of that name.</summary>
    ECDsa Create(string name);

    /// <summary>Opens the key named <paramref name="name"/>, or returns null if there's none.</summary>
    ECDsa? Open(string name);

    /// <summary>Deletes the key named <paramref name="name"/>, if there is one.</summary>
    void Delete(string name);
}

/// <summary>
/// Windows CNG machine keys: in the TPM through the Microsoft Platform Crypto Provider where one is available, otherwise
/// in the Microsoft Software Key Storage Provider, which protects them with DPAPI. Either way the export policy is
/// none, so the private key never leaves the provider.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CngAgentKeyStore(ILogger<CngAgentKeyStore> logger) : IAgentKeyStore
{
    private static readonly CngProvider[] Providers =
        [CngProvider.MicrosoftPlatformCryptoProvider, CngProvider.MicrosoftSoftwareKeyStorageProvider];

    public ECDsa Create(string name)
    {
        Delete(name);
        try
        {
            return Create(name, CngProvider.MicrosoftPlatformCryptoProvider);
        }
        catch (CryptographicException exception)
        {
            logger.LogWarning(exception, "No TPM key could be created; the agent key is protected with DPAPI instead");
            return Create(name, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        }
    }

    public ECDsa? Open(string name)
    {
        foreach (var provider in Providers)
        {
            if (CngKey.Exists(name, provider, CngKeyOpenOptions.MachineKey))
                return new ECDsaCng(CngKey.Open(name, provider, CngKeyOpenOptions.MachineKey));
        }
        return null;
    }

    private static ECDsaCng Create(string name, CngProvider provider) =>
        new(CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
        {
            Provider = provider,
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey | CngKeyCreationOptions.OverwriteExistingKey,
            KeyUsage = CngKeyUsages.Signing,
        }));

    public void Delete(string name)
    {
        foreach (var provider in Providers)
        {
            if (CngKey.Exists(name, provider, CngKeyOpenOptions.MachineKey))
                CngKey.Open(name, provider, CngKeyOpenOptions.MachineKey).Delete();
        }
    }
}

/// <summary>
/// Operating systems without a non-exportable key store the agent supports yet. Enrolling there fails rather than
/// falling back to an exportable key.
/// </summary>
public sealed class UnsupportedAgentKeyStore : IAgentKeyStore
{
    public ECDsa Create(string name) =>
        throw new PlatformNotSupportedException("The agent needs a non-exportable key store (TPM or DPAPI), which only Windows provides so far.");

    public ECDsa? Open(string name) => null;

    public void Delete(string name)
    {
    }
}
