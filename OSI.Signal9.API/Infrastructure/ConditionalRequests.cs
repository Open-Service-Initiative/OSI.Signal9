using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;

namespace OSI.Signal9.API.Infrastructure;

/// <summary>
/// Marks an endpoint that takes part in optimistic concurrency (RFC 9110 §8.8.3, §13): its successful responses
/// carry the resource's strong <c>ETag</c>, and it evaluates <c>If-Match</c> and <c>If-None-Match</c> through
/// <see cref="ConditionalRequests.Preconditions"/> before acting. Every single-resource GET and every write needs it
/// (an architecture test checks), it documents the headers and the 304/412 responses in OpenAPI, and it fails the
/// request if the endpoint doesn't keep its side of the bargain.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OptimisticConcurrencyAttribute : Attribute, IAsyncResultFilter
{
    /// <summary>
    /// Whether a successful response's content represents the target resource, and so carries its <c>ETag</c>. False
    /// for writes whose content only reports the outcome (a heartbeat's "work is waiting"); they still evaluate
    /// preconditions.
    /// </summary>
    public bool Representation { get; init; } = true;

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (StatusOf(context.Result) is { } status and >= 200 and < 300)
        {
            if (ConditionalRequests.HasPreconditions(http.Request) && !http.Items.ContainsKey(ConditionalRequests.EvaluatedKey))
                throw new InvalidOperationException(
                    $"{context.ActionDescriptor.DisplayName} ignored the request's preconditions; call Preconditions() before acting.");
            if (Representation && status != StatusCodes.Status204NoContent && !http.Response.Headers.ContainsKey(HeaderNames.ETag))
                throw new InvalidOperationException(
                    $"{context.ActionDescriptor.DisplayName} returned a representation without an ETag; call SetETag().");
        }

        await next();
    }

    private static int? StatusOf(IActionResult result) => result switch
    {
        IStatusCodeActionResult { StatusCode: { } status } => status,
        ObjectResult => StatusCodes.Status200OK,
        _ => null,
    };
}

/// <summary>Strong entity tags and precondition evaluation for <see cref="OptimisticConcurrencyAttribute"/> endpoints.</summary>
internal static class ConditionalRequests
{
    internal const string EvaluatedKey = "Signal9.PreconditionsEvaluated";

    /// <summary>
    /// A strong entity tag derived from a resource's row version and anything else its representation depends on
    /// (a strong validator changes whenever the representation does, RFC 9110 §8.8.1).
    /// </summary>
    public static EntityTagHeaderValue ETag(uint version, params ReadOnlySpan<object?> derived)
    {
        var text = new StringBuilder(version.ToString(CultureInfo.InvariantCulture));
        foreach (var part in derived)
            text.Append('\u001f').Append(Convert.ToString(part, CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()));
        return new EntityTagHeaderValue($"\"{Convert.ToBase64String(hash, 0, 16).TrimEnd('=').Replace('+', '-').Replace('/', '_')}\"");
    }

    /// <summary>
    /// Evaluates <c>If-Match</c> and <c>If-None-Match</c> (RFC 9110 §13.2.2) against the target resource's
    /// current entity tag. Call it once the target is known to exist (a missing one is a plain 404) and before acting.
    /// </summary>
    /// <param name="current">The current representation's entity tag, or null when the target has none
    /// (a collection being created in, or a write-only sub-resource).</param>
    /// <returns>The response to send instead (400 for an unparsable header, 304 or 412), or null to go ahead.</returns>
    public static ActionResult? Preconditions(this ControllerBase controller, EntityTagHeaderValue? current)
    {
        var request = controller.Request;
        controller.HttpContext.Items[EvaluatedKey] = true;

        if (request.Headers.IfMatch.Count > 0)
        {
            if (!EntityTagHeaderValue.TryParseStrictList(request.Headers.IfMatch, out var ifMatch))
                return Malformed(controller, HeaderNames.IfMatch);
            var matches = current is not null && ifMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: true));
            if (!matches)
                return PreconditionFailed(controller);
        }

        if (request.Headers.IfNoneMatch.Count > 0)
        {
            if (!EntityTagHeaderValue.TryParseStrictList(request.Headers.IfNoneMatch, out var ifNoneMatch))
                return Malformed(controller, HeaderNames.IfNoneMatch);
            var matches = current is not null && ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(current, useStrongComparison: false));
            if (matches)
            {
                if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
                {
                    controller.SetETag(current!);
                    return new StatusCodeResult(StatusCodes.Status304NotModified);
                }
                return PreconditionFailed(controller);
            }
        }

        return null;
    }

    /// <summary>Sets the <c>ETag</c> of the representation in the response.</summary>
    public static void SetETag(this ControllerBase controller, EntityTagHeaderValue etag) =>
        controller.Response.Headers.ETag = etag.ToString();

    /// <summary>
    /// The usual response to a single-resource GET: 304 when <c>If-None-Match</c> matches, 412 when <c>If-Match</c>
    /// doesn't, otherwise <paramref name="value"/> with its <c>ETag</c>.
    /// </summary>
    public static ActionResult<T> Conditional<T>(this ControllerBase controller, T value, EntityTagHeaderValue etag)
    {
        if (controller.Preconditions(etag) is { } refused)
            return refused;
        controller.SetETag(etag);
        return value;
    }

    internal static bool HasPreconditions(HttpRequest request) =>
        request.Headers.IfMatch.Count > 0 || request.Headers.IfNoneMatch.Count > 0;

    private static ObjectResult PreconditionFailed(ControllerBase controller) =>
        controller.Problem(statusCode: StatusCodes.Status412PreconditionFailed, title: "Precondition failed",
            detail: "The resource has changed since its ETag was read. Fetch it again and retry with the new ETag.");

    private static ObjectResult Malformed(ControllerBase controller, string header) =>
        controller.Problem(statusCode: StatusCodes.Status400BadRequest, title: $"Invalid {header} header",
            detail: $"{header} must be '*' or a list of entity tags (RFC 9110 §13.1).");
}

/// <summary>
/// Turns a write that lost a race (its row changed after it was read) into a problem response instead of a lost
/// update: 412 when the request carried a precondition, which is then known to be false, and 409 when it didn't.
/// </summary>
internal sealed class ConcurrencyConflictHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException)
            return false;

        var conditional = ConditionalRequests.HasPreconditions(httpContext.Request);
        httpContext.Response.StatusCode = conditional ? StatusCodes.Status412PreconditionFailed : StatusCodes.Status409Conflict;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = httpContext.Response.StatusCode,
                Title = conditional ? "Precondition failed" : "Concurrent update",
                Detail = "The resource changed while this request was being processed. Fetch it again and retry.",
            },
        });
    }
}

/// <summary>
/// Documents a <see cref="OptimisticConcurrencyAttribute"/> endpoint's side of optimistic concurrency in OpenAPI: the
/// <c>ETag</c> on its representations, <c>If-None-Match</c> and 304 on reads, and <c>If-Match</c> and 412 on writes.
/// </summary>
internal sealed class ConditionalOperationTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<OptimisticConcurrencyAttribute>().FirstOrDefault()
            is not { } concurrency)
            return;

        var read = HttpMethods.IsGet(context.Description.HttpMethod ?? string.Empty);
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = read ? HeaderNames.IfNoneMatch : HeaderNames.IfMatch,
            In = ParameterLocation.Header,
            Description = read
                ? "Entity tags of representations the client already has (RFC 9110 §13.1.2). If one matches, the response is 304 Not Modified."
                : "The ETag the client last read (RFC 9110 §13.1.1). If the resource has changed since, the response is 412 Precondition Failed and nothing changes.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });

        operation.Responses ??= [];
        foreach (var (status, response) in operation.Responses)
        {
            if (concurrency.Representation && status.StartsWith('2') && status != "204" && response is OpenApiResponse withBody)
                (withBody.Headers ??= new Dictionary<string, IOpenApiHeader>())[HeaderNames.ETag] = ETagHeader();
        }

        if (read)
        {
            operation.Responses["304"] = new OpenApiResponse
            {
                Description = "Not Modified: the client's representation is current.",
                Headers = new Dictionary<string, IOpenApiHeader> { [HeaderNames.ETag] = ETagHeader() },
            };
        }
        else
        {
            var problem = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), cancellationToken: cancellationToken);
            operation.Responses["412"] = new OpenApiResponse
            {
                Description = "Precondition Failed: the resource changed since the ETag in If-Match was read.",
                Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = problem } },
            };
        }
    }

    private static OpenApiHeader ETagHeader() => new()
    {
        Description = "Strong entity tag of the resource (RFC 9110 §8.8.3). Send it back in If-Match to update or delete it safely.",
        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
    };
}
