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

/// <summary>The machine key providers a Windows key can live in, in order of preference.</summary>
public enum MachineKeyProvider
{
    /// <summary>The TPM, through the Microsoft Platform Crypto Provider.</summary>
    Tpm,

    /// <summary>The Microsoft Software Key Storage Provider, which protects keys with DPAPI.</summary>
    Software,
}

/// <summary>Machine-wide, non-exportable ECDSA P-256 keys in a <see cref="MachineKeyProvider"/>: CNG, or a fake in tests.</summary>
public interface IMachineKeyProviders
{
    bool Exists(string name, MachineKeyProvider provider);

    /// <exception cref="CryptographicException">The provider can't create the key.</exception>
    ECDsa Create(string name, MachineKeyProvider provider);

    ECDsa Open(string name, MachineKeyProvider provider);

    void Delete(string name, MachineKeyProvider provider);
}

/// <summary>
/// Windows machine keys: in the TPM where one can hold them, otherwise in the software provider. Either way the export
/// policy is none, so the private key never leaves the provider.
/// </summary>
public sealed class CngAgentKeyStore(IMachineKeyProviders providers, ILogger<CngAgentKeyStore> logger) : IAgentKeyStore
{
    private static readonly MachineKeyProvider[] Providers = [MachineKeyProvider.Tpm, MachineKeyProvider.Software];

    public ECDsa Create(string name)
    {
        Delete(name);
        try
        {
            return providers.Create(name, MachineKeyProvider.Tpm);
        }
        catch (CryptographicException exception)
        {
            logger.LogWarning(exception, "No TPM key could be created; the agent key is protected with DPAPI instead");
            return providers.Create(name, MachineKeyProvider.Software);
        }
    }

    public ECDsa? Open(string name)
    {
        foreach (var provider in Providers)
        {
            if (providers.Exists(name, provider))
                return providers.Open(name, provider);
        }
        return null;
    }

    public void Delete(string name)
    {
        foreach (var provider in Providers)
        {
            if (providers.Exists(name, provider))
                providers.Delete(name, provider);
        }
    }
}

/// <summary>CNG machine keys.</summary>
[SupportedOSPlatform("windows")]
public sealed class CngMachineKeys : IMachineKeyProviders
{
    private static CngProvider Cng(MachineKeyProvider provider) => provider switch
    {
        MachineKeyProvider.Tpm => CngProvider.MicrosoftPlatformCryptoProvider,
        _ => CngProvider.MicrosoftSoftwareKeyStorageProvider,
    };

    public bool Exists(string name, MachineKeyProvider provider) =>
        CngKey.Exists(name, Cng(provider), CngKeyOpenOptions.MachineKey);

    public ECDsa Create(string name, MachineKeyProvider provider) =>
        new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, name, new CngKeyCreationParameters
        {
            Provider = Cng(provider),
            ExportPolicy = CngExportPolicies.None,
            KeyCreationOptions = CngKeyCreationOptions.MachineKey | CngKeyCreationOptions.OverwriteExistingKey,
            KeyUsage = CngKeyUsages.Signing,
        }));

    public ECDsa Open(string name, MachineKeyProvider provider) =>
        new ECDsaCng(CngKey.Open(name, Cng(provider), CngKeyOpenOptions.MachineKey));

    public void Delete(string name, MachineKeyProvider provider) =>
        CngKey.Open(name, Cng(provider), CngKeyOpenOptions.MachineKey).Delete();
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
