using System.Net;

namespace OSI.Signal9.Web.Client.Services;

/// <summary>The API refused a request. Carries the problem details (RFC 9457) it answered with, if any.</summary>
public sealed class ApiException(
    HttpStatusCode status, string title, string? detail, IReadOnlyList<string> errors, bool sessionEnded = false)
    : Exception(detail is null ? title : $"{title}: {detail}")
{
    public HttpStatusCode Status { get; } = status;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;

    /// <summary>Validation messages from a 400, one per field error.</summary>
    public IReadOnlyList<string> Errors { get; } = errors;

    /// <summary>
    /// The API answered 401 and web confirmed the session ended. Not an error to show: the dashboard asks the person to
    /// sign in again.
    /// </summary>
    public bool SessionEnded { get; } = sessionEnded;
}
