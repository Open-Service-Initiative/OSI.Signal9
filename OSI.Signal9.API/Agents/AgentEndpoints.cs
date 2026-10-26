using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using OSI.Signal9.Contracts.Agents;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Agents;

/// <summary>
/// Marks an endpoint agents call (enrollment, their own endpoints and the hub): each request must name a version the
/// deployment supports (<see cref="AgentProtocol.VersionHeader"/>), or it's refused. An architecture test makes every
/// endpoint requiring the agent scope one.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AgentEndpointAttribute : Attribute;

public static class AgentEndpoints
{
    /// <summary>
    /// Refuses requests to agent endpoints that name an unsupported agent version or none: 400 with an
    /// <see cref="AgentProtocol.UnsupportedVersion.ProblemType"/> problem giving the supported range. After
    /// authorization, so only a caller allowed to call the endpoint learns the range; a caller that isn't an agent is
    /// left to the endpoint.
    /// </summary>
    public static IApplicationBuilder UseAgentVersions(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        // Only agents are judged by their version: enrollment, where the caller is anonymous, and authenticated
        // agents. Anyone else is refused by the endpoint itself (403), as at any endpoint whose caller it isn't.
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<AgentEndpointAttribute>() is null
            || (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null && context.User.AgentId() is null))
        {
            await next(context);
            return;
        }

        var versions = context.RequestServices.GetRequiredService<AgentVersions>();
        var header = context.Request.Headers[AgentProtocol.VersionHeader].ToString();
        if (versions.Supported(header) is { } version)
        {
            context.Items[typeof(AgentEndpointAttribute)] = version;
            await next(context);
            return;
        }

        await Results.Problem(
            type: AgentProtocol.UnsupportedVersion.ProblemType,
            title: "Agent version not supported",
            detail: versions.Refusal(header),
            statusCode: StatusCodes.Status400BadRequest,
            extensions: new Dictionary<string, object?>
            {
                ["minimumVersion"] = versions.Minimum.ToString(),
                ["maximumVersion"] = versions.Maximum.ToString(),
            }).ExecuteAsync(context);
    });

    /// <summary>The supported version the calling agent named, at an agent endpoint.</summary>
    public static SemanticVersion AgentVersion(this HttpContext context) =>
        context.Items[typeof(AgentEndpointAttribute)] as SemanticVersion
        ?? throw new InvalidOperationException("Not an agent endpoint.");
}

/// <summary>Documents an agent endpoint's version header and the refusal of an unsupported version.</summary>
internal sealed class AgentVersionOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<AgentEndpointAttribute>().Any())
            return Task.CompletedTask;

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = AgentProtocol.VersionHeader,
            In = ParameterLocation.Header,
            Required = true,
            Description = "The calling agent's version, a semantic version (SemVer 2.0.0).",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        operation.Responses ??= new OpenApiResponses();
        const string refusal = "Or the agent's version isn't one the deployment supports: an `application/problem+json` of type `"
            + AgentProtocol.UnsupportedVersion.ProblemType + "`, whose `minimumVersion` and `maximumVersion` give the supported range.";
        operation.Responses["400"] = operation.Responses.TryGetValue("400", out var existing)
            ? new OpenApiResponse { Description = $"{existing.Description} {refusal}", Content = existing.Content }
            : new OpenApiResponse { Description = refusal };
        return Task.CompletedTask;
    }
}
