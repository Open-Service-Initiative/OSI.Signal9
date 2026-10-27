namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// The fixture's browsers forget a cookie only when a host deletes it. Protected cookie values are random base64url,
/// so any text, "1970" included, can turn up inside one; a sign-in whose session cookie was dropped is sent back to
/// Entra, and the test sees Entra's state instead of its client's.
/// </summary>
[TestClass]
public sealed class CookieJarTests
{
    [TestMethod]
    [DataRow("__Host-signal9-sign-inC2=CfDJ8x1970Qa; path=/; secure; samesite=lax; httponly")]
    [DataRow(".AspNetCore.OpenIdConnect.Nonce.CfDJ81970abc=N; expires=Mon, 26 Oct 2026 09:30:00 GMT; path=/signin-oidc; secure")]
    [DataRow("signal9=abc; expires=Wed, 01 Jan 2070 00:00:00 GMT; path=/")]
    public void A_cookie_is_kept_whatever_its_value_and_expiry(string setCookie)
    {
        var jar = new Dictionary<string, string>();

        CookieJar.Apply(jar, setCookie);

        var (name, value) = Pair(setCookie);
        Assert.AreEqual(value, jar[name]);
    }

    [TestMethod]
    [DataRow("signal9=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure")]
    [DataRow("signal9=gone; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/; secure")]
    [DataRow("signal9=gone; max-age=0; path=/")]
    [DataRow("signal9=; path=/")]
    public void A_deleted_cookie_is_forgotten(string setCookie)
    {
        var jar = new Dictionary<string, string> { ["signal9"] = "kept" };

        CookieJar.Apply(jar, setCookie);

        Assert.IsFalse(jar.ContainsKey("signal9"), setCookie);
    }

    private static (string Name, string Value) Pair(string setCookie)
    {
        var pair = setCookie.Split(';')[0];
        var separator = pair.IndexOf('=', StringComparison.Ordinal);
        return (pair[..separator], pair[(separator + 1)..]);
    }
}
