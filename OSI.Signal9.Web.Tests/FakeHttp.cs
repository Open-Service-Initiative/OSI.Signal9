using System.Net;
using System.Net.Http.Json;
using System.Text;
using OSI.Signal9.Contracts.Bff;
using OSI.Signal9.Contracts.Serialization;

namespace OSI.Signal9.Web.Tests;

/// <summary>
/// Stands in for web: answers each request from <see cref="Respond"/> (404 by default) and records what was sent.
/// </summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_requests) return [.. _requests]; }
    }

    /// <summary>Answers bff/user as this person, and everything else with <paramref name="api"/>.</summary>
    public void SignedInAs(string name, Func<HttpRequestMessage, HttpResponseMessage?>? api = null) =>
        Respond = request => request.RequestUri!.AbsolutePath == "/" + BffRoutes.User
            ? Json(HttpStatusCode.OK, new BffUser("subject-1", name))
            : api?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound);

    public void SignedOut() =>
        Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_requests)
            _requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers, request.Content?.Headers.ContentType?.MediaType, body));
        var response = Respond(request);
        response.RequestMessage = request;
        return response;
    }

    public static HttpResponseMessage Json<T>(HttpStatusCode status, T value) =>
        new(status) { Content = JsonContent.Create(value, options: Signal9Json.Options) };

    public static HttpResponseMessage Problem(HttpStatusCode status, string title, string? detail = null) =>
        new(status)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { type = "about:blank", title, status = (int)status, detail }),
                Encoding.UTF8, "application/problem+json"),
        };
}

internal sealed record RecordedRequest(
    HttpMethod Method, Uri Uri, System.Net.Http.Headers.HttpRequestHeaders Headers, string? ContentType, string? Body)
{
    public string PathAndQuery => Uri.PathAndQuery;

    public string? Header(string name) => Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}
