using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Validation;
using OSI.Signal9.Server.Keys;

namespace OSI.Signal9.API.Tests;

/// <summary>
/// Signing and encryption keys rotate every 90 days, the next key is published 14 days early, and running hosts reload
/// keys without a restart (ADR-0008, story 126). The fake clock drives every rotation.
/// </summary>
[TestClass]
public sealed class KeyRotationTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;

    public KeyRotationTests(TestContext testContext) => _testContext = testContext;

    private CancellationToken Ct => _testContext.CancellationToken;

    private DateTimeOffset Start => new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task The_next_key_is_published_14_days_before_it_activates_and_lasts_90_days()
    {
        var first = (await GetSigningCertificatesAsync()).Single();

        AdvanceTo(Start.AddDays(76).AddSeconds(-1));
        Assert.HasCount(1, await GetSigningCertificatesAsync(), "Not published before 14 days ahead.");

        AdvanceTo(Start.AddDays(76));
        var published = await GetSigningCertificatesAsync();

        Assert.HasCount(2, published);
        var next = published.Single(c => c.Thumbprint != first.Thumbprint);
        Assert.AreEqual(Start.AddDays(90).UtcDateTime, next.NotBefore.ToUniversalTime());
        Assert.AreEqual(first.NotAfter, next.NotBefore);
        Assert.AreEqual(Start.AddDays(180).UtcDateTime, next.NotAfter.ToUniversalTime());
    }

    [TestMethod]
    public async Task A_key_activates_at_its_certificates_NotBefore()
    {
        await _factory.SeedClientAsync("reporting-app");
        await _factory.RequestTokenAsync("reporting-app");
        AdvanceTo(Start.AddDays(90).AddSeconds(-1));
        var before = KeysOf((await _factory.RequestTokenAsync("reporting-app")).Value);

        AdvanceTo(Start.AddDays(90));
        var after = KeysOf((await _factory.RequestTokenAsync("reporting-app")).Value);

        Assert.AreEqual(Start.UtcDateTime, before.Signing.NotBefore.ToUniversalTime());
        Assert.AreEqual(Start.UtcDateTime, before.Encryption.NotBefore.ToUniversalTime());
        Assert.AreEqual(Start.AddDays(90).UtcDateTime, after.Signing.NotBefore.ToUniversalTime());
        Assert.AreEqual(Start.AddDays(90).UtcDateTime, after.Encryption.NotBefore.ToUniversalTime());
    }

    [TestMethod]
    public async Task A_running_api_accepts_tokens_from_a_key_that_activated_after_it_started()
    {
        await _factory.SeedClientAsync("reporting-app", "tenants.read");
        Assert.AreEqual(HttpStatusCode.OK, await CallApiAsync((await _factory.RequestTokenAsync("reporting-app", "tenants.read")).Value));

        AdvanceTo(Start.AddDays(90));
        var token = (await _factory.RequestTokenAsync("reporting-app", "tenants.read")).Value;

        Assert.AreEqual(Start.AddDays(90).UtcDateTime, KeysOf(token).Signing.NotBefore.ToUniversalTime());
        Assert.AreEqual(HttpStatusCode.OK, await CallApiAsync(token));
    }

    [TestMethod]
    public async Task A_retired_key_still_validates_unexpired_tokens()
    {
        await _factory.SeedClientAsync("reporting-app", "tenants.read");
        await CallApiAsync((await _factory.RequestTokenAsync("reporting-app", "tenants.read")).Value);
        AdvanceTo(Start.AddDays(90).AddMinutes(-5));
        var token = (await _factory.RequestTokenAsync("reporting-app", "tenants.read")).Value;

        AdvanceTo(Start.AddDays(90).AddMinutes(1));

        Assert.AreEqual(Start.UtcDateTime, KeysOf(token).Signing.NotBefore.ToUniversalTime());
        Assert.AreEqual(HttpStatusCode.OK, await CallApiAsync(token));
    }

    [TestMethod]
    public async Task A_retired_key_is_kept_until_the_longest_refresh_token_lifetime_has_passed()
    {
        await _factory.SeedClientAsync("reporting-app");
        var first = (await GetSigningCertificatesAsync()).Single();
        await CallApiAsync((await _factory.RequestTokenAsync("reporting-app")).Value);

        // Retired at 90 days; refresh tokens it covers live at most 90 days more.
        AdvanceTo(Start.AddDays(180).AddSeconds(-1));
        Assert.Contains(first.Thumbprint, (await GetSigningCertificatesAsync()).Select(c => c.Thumbprint));
        Assert.Contains(first.Thumbprint, ApiSigningKeyThumbprints());

        AdvanceTo(Start.AddDays(180));
        Assert.DoesNotContain(first.Thumbprint, (await GetSigningCertificatesAsync()).Select(c => c.Thumbprint));
        Assert.DoesNotContain(first.Thumbprint, ApiSigningKeyThumbprints());
        Assert.DoesNotContain(first.NotBefore, _factory.Auth.Services.GetRequiredService<StoredKeys>().Load()
            .Select(k => k.Certificate.NotBefore), "Removed from the database too.");
    }

    /// <summary>The keys the running api validates token signatures with.</summary>
    private string[] ApiSigningKeyThumbprints() =>
        [.. _factory.Services.GetRequiredService<IOptionsMonitor<OpenIddictValidationOptions>>().CurrentValue
            .Configuration!.SigningKeys.OfType<X509SecurityKey>().Select(k => k.Certificate.Thumbprint)];

    private async Task<HttpStatusCode> CallApiAsync(string accessToken)
    {
        using var api = _factory.CreateAnonymousClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return (await api.GetAsync("api/v1/tenants", Ct)).StatusCode;
    }

    /// <summary>The stored keys that encrypted an access token and signed the token inside it.</summary>
    private (X509Certificate2 Signing, X509Certificate2 Encryption) KeysOf(string accessToken)
    {
        var keys = _factory.Auth.Services.GetRequiredService<StoredKeys>().Load();
        X509Certificate2 Find(StoredKeyUse use, string? kid) =>
            keys.Single(k => k.Use == use && new X509SecurityKey(k.Certificate).KeyId == kid).Certificate;

        var handler = new JsonWebTokenHandler();
        var encrypted = handler.ReadJsonWebToken(accessToken);
        var encryption = Find(StoredKeyUse.Encryption, encrypted.Kid);
        var signed = handler.ReadJsonWebToken(handler.DecryptToken(encrypted,
            new TokenValidationParameters { TokenDecryptionKeys = [new X509SecurityKey(encryption)] }));
        return (Find(StoredKeyUse.Signing, signed.Kid), encryption);
    }

    private void AdvanceTo(DateTimeOffset instant) => _factory.Time.Advance(instant - _factory.Time.GetUtcNow());

    private async Task<X509Certificate2[]> GetSigningCertificatesAsync()
    {
        using var client = _factory.Auth.CreateClient();
        var jwks = await client.GetFromJsonAsync<JsonElement>(".well-known/jwks", Ct);
        return [.. jwks.GetProperty("keys").EnumerateArray().Select(key =>
            X509CertificateLoader.LoadCertificate(Convert.FromBase64String(key.GetProperty("x5c")[0].GetString()!)))];
    }

    public void Dispose() => _factory.Dispose();
}
