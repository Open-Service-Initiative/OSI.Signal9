using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace OSI.Signal9.Web.Client.Services;

/// <summary>
/// RFC 9470's step-up challenge: 401 with <c>WWW-Authenticate: Bearer error="insufficient_user_authentication"</c>,
/// usually with the <c>max_age</c> the next authentication must meet.
/// </summary>
public static partial class StepUpChallenge
{
    public const string Error = "insufficient_user_authentication";

    /// <summary>
    /// The <c>max_age</c> the response asks the person to authenticate within, in seconds (0, a fresh sign-in, when it
    /// names none), or <see langword="null"/> when it isn't a step-up challenge.
    /// </summary>
    public static long? MaxAge(HttpResponseMessage response)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return null;

        foreach (var challenge in response.Headers.WwwAuthenticate)
        {
            if (!string.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || challenge.Parameter is not { } parameters
                || !ErrorParameter().IsMatch(parameters))
                continue;
            var maxAge = MaxAgeParameter().Match(parameters);
            return maxAge.Success ? long.Parse(maxAge.Groups[1].ValueSpan, CultureInfo.InvariantCulture) : 0;
        }
        return null;
    }

    [GeneratedRegex("""(?:^|[\s,])error\s*=\s*"?insufficient_user_authentication"?(?:$|[\s,])""")]
    private static partial Regex ErrorParameter();

    [GeneratedRegex("""(?:^|[\s,])max_age\s*=\s*"?(\d{1,9})"?(?:$|[\s,])""")]
    private static partial Regex MaxAgeParameter();
}
