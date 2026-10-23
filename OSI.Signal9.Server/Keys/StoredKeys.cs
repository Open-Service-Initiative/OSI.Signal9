using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Server;
using OpenIddict.Validation;
using OSI.Signal9.Server.Data;

namespace OSI.Signal9.Server.Keys;

/// <summary>
/// The authorization server's signing and encryption keys, stored in the database and wrapped by the key-encryption
/// key (ADR-0008, story 126). Every host loads the same keys. Each key is active for 90 days from its certificate's
/// NotBefore, and the next one is published 14 days before it activates. Whichever host first finds a key missing or
/// due generates it, and removes retired keys once <see cref="RetiredRetention"/> has passed, one host at a time under
/// a PostgreSQL advisory lock.
/// </summary>
/// <remarks>
/// Every host reloads the keys without a restart: <see cref="GetChangeToken"/> fires at the next instant the key set
/// changes, when a key is published, activates, retires or is removed, so options built from the keys are rebuilt.
/// </remarks>
public sealed class StoredKeys(IServiceScopeFactory scopes, KeyEncryptionKey kek, TimeProvider time,
    ILogger<StoredKeys> logger) : IDisposable
{
    /// <summary>Key of the transaction-level advisory lock held while keys are generated or removed.</summary>
    public const long LockKey = 0x5167_6E39_4B65_7973; // "Sig9Keys"

    /// <summary>How long a key is active: its certificate's validity period.</summary>
    public static readonly TimeSpan ActivePeriod = TimeSpan.FromDays(90);

    /// <summary>How long before it activates the next key is published, so validators can fetch it in time.</summary>
    public static readonly TimeSpan PublicationLead = TimeSpan.FromDays(14);

    /// <summary>
    /// How long a key is kept once it retires, still validating the tokens it signed and decrypting the ones it
    /// encrypted: the longest refresh-token lifetime, the 90-day absolute cap on a refresh-token chain (spec #63).
    /// </summary>
    public static readonly TimeSpan RetiredRetention = TimeSpan.FromDays(90);

    /// <summary>The size of every generated RSA key.</summary>
    public const int KeySize = 3072;

    /// <summary>How soon a host retries when reloading the keys failed.</summary>
    private static readonly TimeSpan ReloadRetry = TimeSpan.FromMinutes(1);

    /// <summary>The longest a timer can wait, about 49 days.</summary>
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly Lock _gate = new();
    private CancellationTokenSource _changes = new();
    private ITimer? _timer;

    /// <summary>
    /// Loads every stored key with its private key, first generating any key that's missing or due and removing
    /// retired keys past their retention.
    /// </summary>
    public IReadOnlyList<(StoredKeyUse Use, X509Certificate2 Certificate)> Load()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Signal9DbContext>();

        if (Due(db).Any)
            UpdateDue(db);

        var keys = db.StoredKeys.AsNoTracking().OrderBy(k => k.ActivatesAt).ToList();
        ScheduleReload(NextChange(keys, time.GetUtcNow().UtcDateTime));
        return [.. keys.Select(key => (key.Use, Open(key)))];
    }

    /// <summary>A token that fires when the key set next changes; the keys should then be loaded again.</summary>
    public IChangeToken GetChangeToken()
    {
        lock (_gate)
            return new CancellationChangeToken(_changes.Token);
    }

    /// <summary>
    /// What's due now: each use's active key if it has none, its next key once that's due to be published, and the
    /// removal of retired keys past their retention.
    /// </summary>
    private Pending Due(Signal9DbContext db)
    {
        var now = time.GetUtcNow();
        var keys = db.StoredKeys.ToList();

        var generate = new List<(StoredKeyUse, DateTimeOffset)>();
        foreach (var use in Enum.GetValues<StoredKeyUse>())
        {
            var latest = keys.Where(k => k.Use == use).Select(k => (DateTime?)k.ExpiresAt).Max();
            if (latest is not { } expiresAt || expiresAt <= now.UtcDateTime)
                generate.Add((use, now));
            else if (expiresAt - PublicationLead <= now.UtcDateTime)
                generate.Add((use, new DateTimeOffset(expiresAt, TimeSpan.Zero)));
        }
        return new Pending(generate, [.. keys.Where(k => k.ExpiresAt + RetiredRetention <= now.UtcDateTime)]);
    }

    /// <param name="Generate">The keys to generate, by use and activation.</param>
    /// <param name="Remove">The retired keys past their retention.</param>
    private sealed record Pending(List<(StoredKeyUse Use, DateTimeOffset ActivatesAt)> Generate, List<StoredKey> Remove)
    {
        public bool Any => Generate.Count > 0 || Remove.Count > 0;
    }

    private void UpdateDue(Signal9DbContext db) =>
        // A user-initiated transaction runs inside the execution strategy, so transient failures retry it whole.
        db.Database.CreateExecutionStrategy().Execute(() =>
        {
            db.ChangeTracker.Clear();
            using var transaction = db.Database.BeginTransaction();
            db.Database.ExecuteSql($"SELECT pg_advisory_xact_lock({LockKey})");
            // Another host may have done it while this one waited for the lock.
            var due = Due(db);
            db.StoredKeys.AddRange(due.Generate.Select(key => Generate(key.Use, key.ActivatesAt)));
            db.StoredKeys.RemoveRange(due.Remove);
            db.SaveChanges();
            transaction.Commit();
        });

    /// <summary>
    /// The next instant after <paramref name="now"/> at which a key is published, activates, retires or is removed.
    /// </summary>
    private static DateTime NextChange(List<StoredKey> keys, DateTime now) =>
        keys.GroupBy(k => k.Use).Select(g => g.Max(k => k.ExpiresAt) - PublicationLead)
            .Concat(keys.SelectMany(k => new[] { k.ActivatesAt, k.ExpiresAt, k.ExpiresAt + RetiredRetention }))
            .Where(instant => instant > now)
            .DefaultIfEmpty(now + ReloadRetry)
            .Min();

    private void ScheduleReload(DateTime at)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            var delay = at - time.GetUtcNow().UtcDateTime;
            // Timers can't wait longer than about 49 days, so a longer wait is made in steps.
            _timer = time.CreateTimer(_ => ReloadAt(at), null, Clamp(delay, TimeSpan.Zero, MaxTimerDelay),
                Timeout.InfiniteTimeSpan);
        }
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;

    private void ReloadAt(DateTime at)
    {
        if (time.GetUtcNow().UtcDateTime < at)
        {
            ScheduleReload(at);
            return;
        }

        CancellationTokenSource changed;
        lock (_gate)
        {
            changed = _changes;
            // The new token is in place before the old one fires, so listeners re-register on the new one.
            _changes = new CancellationTokenSource();
        }

        try
        {
            // Options listening on the token are rebuilt here, which loads the keys and schedules the next reload.
            changed.Cancel();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Reloading the stored keys failed; retrying in {Delay}.", ReloadRetry);
            ScheduleReload(time.GetUtcNow().UtcDateTime + ReloadRetry);
        }
        finally
        {
            changed.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _changes.Dispose();
        }
    }

    private StoredKey Generate(StoredKeyUse use, DateTimeOffset activation)
    {
        // Certificates hold whole seconds, so the activation is truncated to match the certificate's NotBefore.
        var activatesAt = activation.AddTicks(-(activation.Ticks % TimeSpan.TicksPerSecond));
        var id = Guid.CreateVersion7(time.GetUtcNow());

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

/// <summary>Rebuilds OpenIddict's server options, and with them its keys, whenever the stored keys change.</summary>
internal sealed class StoredKeysChangeTokenSource(StoredKeys keys) : IOptionsChangeTokenSource<OpenIddictServerOptions>
{
    public string Name => Options.DefaultName;

    public IChangeToken GetChangeToken() => keys.GetChangeToken();
}

/// <summary>
/// Rebuilds the validation options of a host that validates tokens with its local server (<c>UseLocalServer</c>), which
/// copies the server's keys only when its options are built. It fires after the server options are rebuilt, never
/// before, so it always copies the new keys.
/// </summary>
internal sealed class LocalServerKeysChangeTokenSource : IOptionsChangeTokenSource<OpenIddictValidationOptions>, IDisposable
{
    private readonly Lock _gate = new();
    private readonly IDisposable? _registration;
    private CancellationTokenSource _changes = new();

    public LocalServerKeysChangeTokenSource(IOptionsMonitor<OpenIddictServerOptions> server) =>
        _registration = server.OnChange(_ => Changed());

    public string Name => Options.DefaultName;

    public IChangeToken GetChangeToken()
    {
        lock (_gate)
            return new CancellationChangeToken(_changes.Token);
    }

    private void Changed()
    {
        CancellationTokenSource changed;
        lock (_gate)
        {
            changed = _changes;
            _changes = new CancellationTokenSource();
        }

        using (changed)
            changed.Cancel();
    }

    public void Dispose()
    {
        _registration?.Dispose();
        lock (_gate)
            _changes.Dispose();
    }
}
