using Microsoft.Net.Http.Headers;

namespace OSI.Signal9.IntegrationTests;

/// <summary>
/// How the fixture's browsers keep one host's cookies. Cookie expiry is ignored, because the hosts set it from the
/// fixture's clock rather than the real one; only a header that deletes a cookie removes it.
/// </summary>
internal static class CookieJar
{
    /// <summary>The <c>Cookie</c> header that sends every cookie in <paramref name="jar"/>.</summary>
    public static string Header(IReadOnlyDictionary<string, string> jar) =>
        string.Join("; ", jar.Select(c => $"{c.Key}={c.Value}"));

    /// <summary>
    /// Keeps the cookie that one <c>Set-Cookie</c> header sets, or forgets it if the header deletes it: with an empty
    /// value, a zero <c>Max-Age</c>, or an <c>Expires</c> at the Unix epoch (ASP.NET Core deletes with 1970). Only the
    /// attributes count; a cookie's value can contain any text.
    /// </summary>
    public static void Apply(IDictionary<string, string> jar, string setCookie)
    {
        var cookie = SetCookieHeaderValue.Parse(setCookie);
        var (name, value) = (cookie.Name.ToString(), cookie.Value.ToString());
        if (value.Length == 0 || cookie.MaxAge <= TimeSpan.Zero || cookie.Expires < DateTimeOffset.UnixEpoch.AddYears(1))
            jar.Remove(name);
        else
            jar[name] = value;
    }
}
