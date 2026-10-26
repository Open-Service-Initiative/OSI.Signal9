using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using OSI.Signal9.Agent.Identity;

namespace OSI.Signal9.Agent.Tests;

/// <summary>
/// The Windows key store's choice of provider (story 98): the TPM where it can create a key there, otherwise the
/// software provider, never an exportable key. CNG itself sits behind <see cref="IMachineKeyProviders"/>.
/// </summary>
[TestClass]
public sealed class CngAgentKeyStoreTests
{
    [TestMethod]
    public void A_new_key_goes_in_the_TPM_when_one_can_hold_it()
    {
        var providers = new FakeProviders();

        using var key = new CngAgentKeyStore(providers, NullLogger<CngAgentKeyStore>.Instance).Create("agent");

        Assert.AreEqual(MachineKeyProvider.Tpm, providers.Holder("agent"));
    }

    [TestMethod]
    public void Without_a_usable_TPM_the_key_goes_in_the_software_provider()
    {
        var providers = new FakeProviders { TpmFails = true };

        using var key = new CngAgentKeyStore(providers, NullLogger<CngAgentKeyStore>.Instance).Create("agent");

        Assert.AreEqual(MachineKeyProvider.Software, providers.Holder("agent"));
    }

    [TestMethod]
    public void Creating_a_key_replaces_one_of_that_name_in_either_provider()
    {
        var providers = new FakeProviders();
        providers.Add("agent", MachineKeyProvider.Software);

        using var key = new CngAgentKeyStore(providers, NullLogger<CngAgentKeyStore>.Instance).Create("agent");

        Assert.AreEqual(MachineKeyProvider.Tpm, providers.Holder("agent"));
        Assert.AreEqual(1, providers.Count("agent"));
    }

    [TestMethod]
    public void A_key_is_opened_from_whichever_provider_holds_it()
    {
        var providers = new FakeProviders();
        providers.Add("agent", MachineKeyProvider.Software);
        var store = new CngAgentKeyStore(providers, NullLogger<CngAgentKeyStore>.Instance);

        using var key = store.Open("agent");

        Assert.IsNotNull(key);
        Assert.IsNull(store.Open("other"));
    }

    [TestMethod]
    public void Deleting_a_key_removes_it_from_every_provider()
    {
        var providers = new FakeProviders();
        providers.Add("agent", MachineKeyProvider.Software);
        providers.Add("agent", MachineKeyProvider.Tpm);

        new CngAgentKeyStore(providers, NullLogger<CngAgentKeyStore>.Instance).Delete("agent");

        Assert.AreEqual(0, providers.Count("agent"));
    }

    /// <summary>Machine key providers in memory; the TPM can be made to refuse new keys, as one without room or support does.</summary>
    private sealed class FakeProviders : IMachineKeyProviders
    {
        private readonly List<(string Name, MachineKeyProvider Provider)> _keys = [];

        public bool TpmFails { get; init; }

        public void Add(string name, MachineKeyProvider provider) => _keys.Add((name, provider));

        public MachineKeyProvider? Holder(string name) => _keys.Where(k => k.Name == name).Select(k => (MachineKeyProvider?)k.Provider).SingleOrDefault();

        public int Count(string name) => _keys.Count(k => k.Name == name);

        public bool Exists(string name, MachineKeyProvider provider) => _keys.Contains((name, provider));

        public ECDsa Create(string name, MachineKeyProvider provider)
        {
            if (provider == MachineKeyProvider.Tpm && TpmFails)
                throw new CryptographicException("The TPM can't hold the key.");
            Add(name, provider);
            return ECDsa.Create(ECCurve.NamedCurves.nistP256);
        }

        public ECDsa Open(string name, MachineKeyProvider provider) =>
            Exists(name, provider) ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : throw new CryptographicException("No such key.");

        public void Delete(string name, MachineKeyProvider provider) => _keys.Remove((name, provider));
    }
}
