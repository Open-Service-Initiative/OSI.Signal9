using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.API.Authorization;

/// <summary>
/// Shows in the OpenAPI document how to get a token and which scopes each operation requires (story 65). Each way to
/// get a token is its own flow, with only the scopes it can grant: a person (directly or through a delegated API
/// application) by authorization code, an API application acting as itself by client credentials, and an enrolled
/// agent by client credentials at api's mTLS token endpoint.
/// </summary>
internal static class ScopesInOpenApi
{
    public const string SchemeName = "oauth2";
    public const string AgentSchemeName = "agent";

    public static OpenApiOptions AddScopes(this OpenApiOptions options, IdentityOptions identity)
    {
        var issuer = identity.Issuer;
        var token = new Uri(issuer, AuthorizationServer.TokenEndpoint);
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[SchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = "Signal9's authorization server. Discovery: " + new Uri(issuer, ".well-known/openid-configuration")
                    + ". The authorization code flow needs PKCE (S256); a client authenticating with a certificate uses the"
                    + " token endpoint's mTLS alias that discovery names.",
                Flows = new OpenApiOAuthFlows
                {
                    AuthorizationCode = new OpenApiOAuthFlow
                    {
                        AuthorizationUrl = new Uri(issuer, AuthorizationServer.AuthorizationEndpoint),
                        TokenUrl = token,
                        RefreshUrl = token,
                        Scopes = Grantable(Scopes.IsGrantableToDelegatedApplication),
                    },
                    ClientCredentials = new OpenApiOAuthFlow
                    {
                        TokenUrl = token,
                        Scopes = Grantable(Scopes.IsGrantableToApplication),
                    },
                },
            };
            document.Components.SecuritySchemes[AgentSchemeName] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.OAuth2,
                Description = "An enrolled agent, authenticated by its certificate (self_signed_tls_client_auth, RFC 8705).",
                Flows = new OpenApiOAuthFlows
                {
                    ClientCredentials = new OpenApiOAuthFlow
                    {
                        TokenUrl = new Uri(identity.ApiUri, AuthorizationServer.TokenEndpoint),
                        Scopes = Grantable(scope => scope == Scopes.Agent),
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
                .Select(scopes => new OpenApiSecurityRequirement
                {
                    [new OpenApiSecuritySchemeReference(scopes.Contains(Scopes.Agent) ? AgentSchemeName : SchemeName, context.Document)] = scopes,
                })
                .ToList();
            operation.Responses ??= new OpenApiResponses();
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "No valid access token." });
            operation.Responses.TryAdd("403", new OpenApiResponse { Description = "The access token lacks a required scope." });
            return Task.CompletedTask;
        });
        return options;
    }

    private static Dictionary<string, string> Grantable(Func<string, bool> grantable) =>
        Scopes.All.Where(scope => grantable(scope.Key)).ToDictionary();
}
