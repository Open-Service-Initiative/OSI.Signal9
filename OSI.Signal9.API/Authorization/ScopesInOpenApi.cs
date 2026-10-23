using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Authorization;

/// <summary>
/// Shows in the OpenAPI document how to get a token and which scopes each operation requires (story 65).
/// </summary>
internal static class ScopesInOpenApi
{
    public const string SchemeName = "oauth2";

    public static OpenApiOptions AddScopes(this OpenApiOptions options, Uri issuer)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = "Signal9's authorization server. Discovery: " + new Uri(issuer, ".well-known/openid-configuration"),
                Flows = new OpenApiOAuthFlows
                {
                    ClientCredentials = new OpenApiOAuthFlow
                    {
                        TokenUrl = new Uri(issuer, AuthorizationServer.TokenEndpoint),
                        Scopes = Scopes.All.ToDictionary(),
                    },
                },
            };
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            var required = context.Description.ActionDescriptor.EndpointMetadata.OfType<RequireScopeAttribute>().ToList();
            if (required.Count == 0)
                return Task.CompletedTask;

            // Each attribute must be satisfied by one of its scopes. OpenAPI lists alternatives as separate requirements,
            // each naming every scope it needs.
            IEnumerable<List<string>> alternatives = [[]];
            foreach (var attribute in required)
                alternatives = alternatives.SelectMany(scopes => attribute.Scopes.Select(scope => (List<string>)[.. scopes, scope]));

            operation.Security = alternatives
                .Select(scopes => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference(SchemeName, context.Document)] = scopes })
                .ToList();
            operation.Responses ??= new OpenApiResponses();
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "No valid access token." });
            operation.Responses.TryAdd("403", new OpenApiResponse { Description = "The access token lacks a required scope." });
            return Task.CompletedTask;
        });
        return options;
    }
}
