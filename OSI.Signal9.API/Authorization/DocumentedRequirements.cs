using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace OSI.Signal9.API.Authorization;

/// <summary>
/// An authorization requirement beyond scopes that the OpenAPI document shows on every operation needing it (story 65),
/// such as <c>[RequireRecentMfa]</c> (#120) and <c>[RequireAncestorAdministrator]</c> (#122). Implement it on the
/// requirement an <see cref="IAuthorizationRequirementData"/> attribute returns, and the attribute is documented.
/// </summary>
public interface IDocumentedRequirement
{
    /// <summary>A stable kebab-case name for tools, such as <c>recent-mfa</c>.</summary>
    string Name { get; }

    /// <summary>One sentence for people: what a caller needs to satisfy it.</summary>
    string Description { get; }
}

/// <summary>
/// Lists each operation's <see cref="IDocumentedRequirement"/>s in its <see cref="Extension"/> extension, for tools, and
/// at the end of its description, for people reading the reference.
/// </summary>
internal sealed class DocumentedRequirements : IOpenApiOperationTransformer
{
    public const string Extension = "x-signal9-requirements";

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var requirements = context.Description.ActionDescriptor.EndpointMetadata.OfType<IAuthorizationRequirementData>()
            .SelectMany(data => data.GetRequirements()).OfType<IDocumentedRequirement>()
            .DistinctBy(requirement => requirement.Name).ToList();
        if (requirements.Count == 0)
            return Task.CompletedTask;

        operation.Extensions ??= new Dictionary<string, IOpenApiExtension>();
        operation.Extensions[Extension] = new JsonNodeExtension(new JsonArray([.. requirements.Select(requirement =>
            new JsonObject { ["name"] = requirement.Name, ["description"] = requirement.Description })]));
        var requires = string.Join("\n", requirements.Select(requirement => $"- {requirement.Description}"));
        operation.Description = string.IsNullOrEmpty(operation.Description)
            ? $"Requires:\n\n{requires}"
            : $"{operation.Description}\n\nRequires:\n\n{requires}";
        return Task.CompletedTask;
    }
}
