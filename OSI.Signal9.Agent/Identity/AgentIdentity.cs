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
/// deployments or root tenant trees never collide.
/// </summary>
public sealed class AgentIdentityStore(IAgentKeyStore keys, IOptions<AgentOptions> options, TimeProvider time)
{
    /// <summary>How long the agent's certificate is valid; the API accepts at most 398 days.</summary>
    public static readonly TimeSpan CertificateLifetime = TimeSpan.FromDays(365);

    private static readonly Oid ClientAuthentication = new("1.3.6.1.5.5.7.3.2");

    private string Instance => options.Value.InstanceId;

    /// <summary>The name of the instance's key in the key store.</summary>
    public string KeyName => $"Signal9 Agent {Instance}";

    /// <summary>The instance's state file, which holds nothing secret: its agent ID and public certificate.</summary>
    public string StatePath => Path.Combine(options.Value.StateDirectory, Instance, "agent.json");

    /// <summary>The instance's identity, or null if it hasn't enrolled (or its key is gone).</summary>
    public AgentIdentity? Load()
    {
        if (!File.Exists(StatePath))
            return null;
        var state = JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath))
            ?? throw new InvalidDataException($"The agent state file {StatePath} is empty.");
        using var key = keys.Open(KeyName);
        if (key is null)
            return null;
        using var certificate = X509Certificate2.CreateFromPem(state.Certificate);
        return new AgentIdentity(state.AgentId, certificate.CopyWithPrivateKey(key));
    }

    /// <summary>
    /// Generates the instance's new key, replacing any earlier one, and its self-signed client certificate: the
    /// digitalSignature key usage and the clientAuth extended key usage, as RFC 8705 client authentication needs.
    /// </summary>
    public X509Certificate2 CreateCertificate()
    {
        using var key = keys.Create(KeyName);
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
    public void Save(Guid agentId, X509Certificate2 certificate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new State(agentId, certificate.ExportCertificatePem())));
        File.Move(temporary, StatePath, overwrite: true);
    }

    private sealed record State(Guid AgentId, string Certificate);
}
