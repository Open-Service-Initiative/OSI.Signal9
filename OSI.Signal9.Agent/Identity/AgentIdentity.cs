using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OSI.Signal9.Agent.Identity;

/// <summary>An enrolled agent instance: its agent ID, which is its OAuth client ID, and its client certificate.</summary>
/// <param name="Certificate">The self-signed certificate, with the instance's non-exportable private key.</param>
public sealed record AgentIdentity(Guid AgentId, X509Certificate2 Certificate);

/// <summary>
/// One agent instance's identity on this machine: its key, in the <see cref="IAgentKeyStore"/>, and its agent ID and
/// certificate, in a state file. Both are named after <see cref="AgentOptions.InstanceId"/>, so instances for different
/// deployments or root tenant trees never collide. The key lives in one of two slots, <see cref="KeyName"/> and
/// <see cref="RotatedKeyName"/>: a rotation creates the new key in the other slot, and records it as the next identity
/// until the API has taken its certificate (story 102).
/// </summary>
public sealed class AgentIdentityStore(IAgentKeyStore keys, IOptions<AgentOptions> options, TimeProvider time)
{
    /// <summary>How long the agent's certificate is valid; the API accepts at most 398 days.</summary>
    public static readonly TimeSpan CertificateLifetime = TimeSpan.FromDays(365);

    /// <summary>A certificate is rotated once this close to its expiry, leaving months to retry if rotation fails.</summary>
    public static readonly TimeSpan RenewalMargin = TimeSpan.FromDays(120);

    private static readonly Oid ClientAuthentication = new("1.3.6.1.5.5.7.3.2");

    private string Instance => options.Value.InstanceId;

    /// <summary>The name of the instance's first key slot in the key store.</summary>
    public string KeyName => $"Signal9 Agent {Instance}";

    /// <summary>The name of the instance's other key slot, which each rotation alternates with <see cref="KeyName"/>.</summary>
    public string RotatedKeyName => $"Signal9 Agent {Instance} rotated";

    /// <summary>The instance's state file, which holds nothing secret: its agent ID and public certificates.</summary>
    public string StatePath => Path.Combine(options.Value.StateDirectory, Instance, "agent.json");

    /// <summary>When <paramref name="certificate"/> is due to be rotated.</summary>
    public static DateTimeOffset RenewalDue(X509Certificate2 certificate) =>
        new DateTimeOffset(certificate.NotAfter.ToUniversalTime()) - RenewalMargin;

    /// <summary>The instance's identity, or null if it hasn't enrolled (or its key is gone).</summary>
    public AgentIdentity? Load() =>
        ReadState() is { } state ? Open(state.AgentId, state.Current) : null;

    /// <summary>The identity a rotation under way is moving to, or null if there's none.</summary>
    public AgentIdentity? LoadNext() =>
        ReadState() is { Next: { } next } state ? Open(state.AgentId, next) : null;

    /// <summary>
    /// Generates a new key in the <paramref name="keyName"/> slot (<see cref="KeyName"/> by default), replacing any key
    /// there, and its self-signed client certificate: the digitalSignature key usage and the clientAuth extended key
    /// usage, as RFC 8705 client authentication needs.
    /// </summary>
    public X509Certificate2 CreateCertificate(string? keyName = null)
    {
        using var key = keys.Create(keyName ?? KeyName);
        var request = new CertificateRequest("CN=Signal9 Agent", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ClientAuthentication], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        // A little in the past, so the API's clock needn't be ahead of ours.
        var now = time.GetUtcNow();
        return request.CreateSelfSigned(now.AddMinutes(-5), now + CertificateLifetime);
    }

    /// <summary>Records that the instance enrolled as <paramref name="agentId"/> with <paramref name="certificate"/>.</summary>
    public void Save(Guid agentId, X509Certificate2 certificate) =>
        WriteState(new State(agentId, new Slot(KeyName, certificate.ExportCertificatePem()), Next: null));

    /// <summary>
    /// Starts a rotation: a new key in the slot the current one isn't using, and its certificate, recorded as the next
    /// identity before it's offered to the API, so it isn't lost if the API takes it but its answer never arrives.
    /// </summary>
    public AgentIdentity BeginRotation()
    {
        var state = ReadState() ?? throw new InvalidOperationException("The instance hasn't enrolled.");
        var keyName = state.Current.KeyName == KeyName ? RotatedKeyName : KeyName;
        using var certificate = CreateCertificate(keyName);
        var next = new Slot(keyName, certificate.ExportCertificatePem());
        WriteState(state with { Next = next });
        return Open(state.AgentId, next)!;
    }

    /// <summary>Makes the next identity the current one, once the API has taken its certificate, and deletes the old key.</summary>
    public void CompleteRotation()
    {
        var state = ReadState() ?? throw new InvalidOperationException("The instance hasn't enrolled.");
        if (state.Next is not { } next)
            return;
        WriteState(new State(state.AgentId, next, Next: null));
        keys.Delete(state.Current.KeyName);
    }

    /// <summary>Drops the next identity and its key, after the API refused its certificate.</summary>
    public void AbandonRotation()
    {
        if (ReadState() is not { Next: { } next } state)
            return;
        WriteState(state with { Next = null });
        keys.Delete(next.KeyName);
    }

    private AgentIdentity? Open(Guid agentId, Slot slot)
    {
        using var key = keys.Open(slot.KeyName);
        if (key is null)
            return null;
        using var certificate = X509Certificate2.CreateFromPem(slot.Certificate);
        return new AgentIdentity(agentId, certificate.CopyWithPrivateKey(key));
    }

    private State? ReadState() => File.Exists(StatePath)
        ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath))
            ?? throw new InvalidDataException($"The agent state file {StatePath} is empty.")
        : null;

    private void WriteState(State state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, StatePath, overwrite: true);
    }

    /// <summary>A key slot and the certificate of the key in it.</summary>
    private sealed record Slot(string KeyName, string Certificate);

    private sealed record State(Guid AgentId, Slot Current, Slot? Next);
}
