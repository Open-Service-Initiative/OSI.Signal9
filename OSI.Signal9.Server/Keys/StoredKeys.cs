using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.Server.Keys;

/// <summary>
/// The authorization server's signing and encryption keys, stored in the database and wrapped by the key-encryption
/// key (ADR-0008, story 126). Every host loads the same keys; whichever host first finds no active key of a use
/// generates one, one host at a time under a PostgreSQL advisory lock.
/// </summary>
public sealed class StoredKeys(IServiceScopeFactory scopes, KeyEncryptionKey kek, TimeProvider time)
{
    /// <summary>Key of the transaction-level advisory lock held while keys are generated.</summary>
    public const long LockKey = 0x5167_6E39_4B65_7973; // "Sig9Keys"

    /// <summary>How long a key is active: its certificate's validity period.</summary>
    public static readonly TimeSpan ActivePeriod = TimeSpan.FromDays(90);

    /// <summary>The size of every generated RSA key.</summary>
    public const int KeySize = 3072;

    /// <summary>Loads every stored key with its private key, first generating any active key that's missing.</summary>
    public IReadOnlyList<(StoredKeyUse Use, X509Certificate2 Certificate)> Load()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();

        if (MissingUses(db).Count > 0)
            GenerateMissing(db);

        return [.. db.StoredKeys.AsNoTracking().OrderBy(k => k.ActivatesAt).AsEnumerable()
            .Select(key => (key.Use, Open(key)))];
    }

    private List<StoredKeyUse> MissingUses(Signal9DbContext db)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var active = db.StoredKeys.Where(k => k.ActivatesAt <= now && now < k.ExpiresAt).Select(k => k.Use).Distinct().ToList();
        return [.. Enum.GetValues<StoredKeyUse>().Except(active)];
    }

    private void GenerateMissing(Signal9DbContext db) =>
        // A user-initiated transaction runs inside the execution strategy, so transient failures retry it whole.
        db.Database.CreateExecutionStrategy().Execute(() =>
        {
            db.ChangeTracker.Clear();
            using var transaction = db.Database.BeginTransaction();
            db.Database.ExecuteSql($"SELECT pg_advisory_xact_lock({LockKey})");
            // Another host may have generated them while this one waited for the lock.
            foreach (var use in MissingUses(db))
                db.StoredKeys.Add(Generate(use));
            db.SaveChanges();
            transaction.Commit();
        });

    private StoredKey Generate(StoredKeyUse use)
    {
        // Certificates hold whole seconds, so the activation is truncated to match the certificate's NotBefore.
        var now = time.GetUtcNow();
        var activatesAt = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond));
        var id = Guid.CreateVersion7(now);

        using var rsa = RSA.Create(KeySize);
        var request = new CertificateRequest($"CN=Signal9 {use.ToString().ToLowerInvariant()} key {id}", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            use == StoredKeyUse.Signing ? X509KeyUsageFlags.DigitalSignature : X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        using var certificate = request.CreateSelfSigned(activatesAt, activatesAt + ActivePeriod);

        var privateKey = rsa.ExportPkcs8PrivateKey();
        try
        {
            var sealedKey = kek.Seal(privateKey, Context(id, use));
            return new StoredKey
            {
                Id = id,
                Use = use,
                ActivatesAt = certificate.NotBefore.ToUniversalTime(),
                ExpiresAt = certificate.NotAfter.ToUniversalTime(),
                Certificate = certificate.RawData,
                KekId = sealedKey.KekId,
                WrappedKey = sealedKey.WrappedKey,
                EncryptedPrivateKey = sealedKey.Ciphertext,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    private X509Certificate2 Open(StoredKey key)
    {
        var privateKey = kek.Open(new SealedSecret(key.KekId, key.WrappedKey, key.EncryptedPrivateKey), Context(key.Id, key.Use));
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(key.Certificate);
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(privateKey, out _);
            return certificate.CopyWithPrivateKey(rsa);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }
    }

    /// <summary>Binds each sealed private key to its own row, so it can't be swapped onto another.</summary>
    private static string Context(Guid id, StoredKeyUse use) => $"signal9/stored-key/{use}/{id}";
}

/// <summary>Gives OpenIddict the stored keys, in place of keys configured in code.</summary>
internal sealed class ConfigureStoredKeys(StoredKeys keys) : IConfigureOptions<OpenIddictServerOptions>
{
    public void Configure(OpenIddictServerOptions options)
    {
        foreach (var (use, certificate) in keys.Load())
        {
            var key = new X509SecurityKey(certificate);
            if (use == StoredKeyUse.Signing)
                options.SigningCredentials.Add(new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            else
                options.EncryptionCredentials.Add(new EncryptingCredentials(key, SecurityAlgorithms.RsaOAEP,
                    SecurityAlgorithms.Aes256CbcHmacSha512));
        }
    }
}
