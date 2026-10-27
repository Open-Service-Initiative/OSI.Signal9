using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// Signing and encryption keys rotate every 90 days, the next key is published 14 days early, and running hosts reload
/// keys without a restart (ADR-0008, story 126). The fake clock drives every rotation.
/// </summary>
[TestClass]
public sealed class KeyRotationTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new();
    private readonly TestContext _testContext;
    private readonly DateTimeOffset _start;

    public KeyRotationTests(TestContext testContext)
    {
        _testContext = testContext;
        _start = _factory.Time.GetUtcNow();
    }

    private CancellationToken Ct => _testContext.CancellationToken;

    private DateTimeOffset Start => _start;

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
        var published = await GetSigningCertificatesAsync();
        var initial = await SignInAsync();
        AdvanceTo(Start.AddDays(90).AddSeconds(-1));
        var before = await SignInAsync();

        AdvanceTo(Start.AddDays(90));
        var after = await SignInAsync();

        // Signed with the key whose certificate the JWKS publishes with that NotBefore.
        Assert.AreEqual(Start.UtcDateTime, SigningCertificate(published, before).NotBefore.ToUniversalTime());
        Assert.AreEqual(Start.AddDays(90).UtcDateTime,
            SigningCertificate(await GetSigningCertificatesAsync(), after).NotBefore.ToUniversalTime());
        // The encryption key, which only auth and api hold, changes at the same instant.
        Assert.AreEqual(EncryptionKeyId(initial), EncryptionKeyId(before));
        Assert.AreNotEqual(EncryptionKeyId(before), EncryptionKeyId(after));
    }

    [TestMethod]
    public async Task A_running_api_accepts_tokens_from_a_key_that_activated_after_it_started()
    {
        var first = await SignInAsync();
        Assert.AreEqual(HttpStatusCode.OK, await CallApiAsync(first.AccessToken));

        AdvanceTo(Start.AddDays(90));
        var rotated = await SignInAsync();

        Assert.AreEqual(Start.AddDays(90).UtcDateTime,
            SigningCertificate(await GetSigningCertificatesAsync(), rotated).NotBefore.ToUniversalTime());
        Assert.AreNotEqual(EncryptionKeyId(first), EncryptionKeyId(rotated));
        Assert.AreEqual(HttpStatusCode.OK, await CallApiAsync(rotated.AccessToken));
    }

    [TestMethod]
    public async Task A_retired_key_still_validates_unexpired_tokens()
    {
        var first = (await GetSigningCertificatesAsync()).Single();
        Assert.AreEqual(HttpStatusCode.OK, await CallApiAsync((await SignInAsync()).AccessToken));
        AdvanceTo(Start.AddDays(90).AddMinutes(-5));
        var tokens = await SignInAsync();

        AdvanceTo(Start.AddDays(90).AddMinutes(1));

        Assert.AreEqual(first.Thumbprint, SigningCertificate([first], tokens).Thumbprint);
        Assert.AreEqual(HttpStatusCode.OK, await CallApiAsync(tokens.AccessToken));
    }

    [TestMethod]
    public async Task A_retired_key_is_kept_until_the_longest_refresh_token_lifetime_has_passed()
    {
        var first = (await GetSigningCertificatesAsync()).Single();
        AdvanceTo(Start.AddDays(90).AddMinutes(-1));
        var tokens = await SignInAsync(OpenIddictConstants.Scopes.OfflineAccess, "tenants.read");
        Assert.AreEqual(first.Thumbprint, SigningCertificate([first], tokens).Thumbprint);

        // A refresh token from before the rotation still works after it, until its 14 idle days are up.
        AdvanceTo(Start.AddDays(90 + 13));
        var refreshed = await _factory.RefreshPersonTokensAsync(tokens.RefreshToken!);
        Assert.AreEqual(HttpStatusCode.OK, refreshed.StatusCode, await refreshed.Content.ReadAsStringAsync(Ct));

        // Retired at 90 days; refresh tokens it covers live at most 90 days more.
        AdvanceTo(Start.AddDays(180).AddSeconds(-1));
        Assert.Contains(first.Thumbprint, (await GetSigningCertificatesAsync()).Select(c => c.Thumbprint));

        AdvanceTo(Start.AddDays(180));
        Assert.DoesNotContain(first.Thumbprint, (await GetSigningCertificatesAsync()).Select(c => c.Thumbprint));
    }

    /// <summary>A Superadministrator signing in now, who needs no role assignment to refresh.</summary>
    private Task<PersonTokens> SignInAsync(params string[] scopes) =>
        _factory.PersonTokensAsync(_factory.Superadministrator(), scopes.Length > 0 ? scopes : ["tenants.read"]);

    private async Task<HttpStatusCode> CallApiAsync(string accessToken)
    {
        using var api = _factory.CreateAnonymousClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return (await api.GetAsync("api/v1/tenants", Ct)).StatusCode;
    }

    /// <summary>
    /// The published certificate of the key that signed the sign-in's tokens, by the key ID in its ID token's header:
    /// the access token is signed with the same key, but encrypted, so only auth and api can read its signature.
    /// </summary>
    private static X509Certificate2 SigningCertificate(IEnumerable<X509Certificate2> published, PersonTokens tokens)
    {
        var kid = new JsonWebTokenHandler().ReadJsonWebToken(tokens.IdToken).Kid;
        return published.Single(c => new X509SecurityKey(c).KeyId == kid);
    }

    /// <summary>The key ID in the access token's JWE header, which names the key that encrypted it.</summary>
    private static string EncryptionKeyId(PersonTokens tokens) => new JsonWebTokenHandler().ReadJsonWebToken(tokens.AccessToken).Kid;

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
