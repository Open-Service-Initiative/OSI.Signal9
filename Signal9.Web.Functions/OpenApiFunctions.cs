using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;

namespace Signal9.Web.Functions;

/// <summary>
/// Functions for serving OpenAPI documentation
/// </summary>
public class OpenApiFunctions
{
    private readonly ILogger<OpenApiFunctions> _logger;

    public OpenApiFunctions(ILogger<OpenApiFunctions> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Get OpenAPI specification in JSON format
    /// </summary>
    [Function("GetOpenApiJson")]
    public async Task<HttpResponseData> GetOpenApiJsonAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "openapi.json")] HttpRequestData req)
    {
        _logger.LogInformation("Serving OpenAPI JSON specification");

        try
        {
            var openApiJson = OpenApiDocumentGenerator.GenerateJson();
            
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(openApiJson);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating OpenAPI JSON");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to generate OpenAPI specification");
            return errorResponse;
        }
    }

    /// <summary>
    /// Get OpenAPI specification in YAML format
    /// </summary>
    [Function("GetOpenApiYaml")]
    public async Task<HttpResponseData> GetOpenApiYamlAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "openapi.yaml")] HttpRequestData req)
    {
        _logger.LogInformation("Serving OpenAPI YAML specification");

        try
        {
            var openApiYaml = OpenApiDocumentGenerator.GenerateYaml();
            
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/yaml");
            await response.WriteStringAsync(openApiYaml);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating OpenAPI YAML");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to generate OpenAPI specification");
            return errorResponse;
        }
    }

    /// <summary>
    /// Serve Swagger UI for interactive API documentation
    /// </summary>
    [Function("GetSwaggerUI")]
    public async Task<HttpResponseData> GetSwaggerUIAsync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "swagger")] HttpRequestData req)
    {
        _logger.LogInformation("Serving Swagger UI");

        try
        {
            var swaggerHtml = GenerateSwaggerUI();
            
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/html");
            await response.WriteStringAsync(swaggerHtml);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error serving Swagger UI");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync("Failed to serve Swagger UI");
            return errorResponse;
        }
    }

    private static string GenerateSwaggerUI()
    {
        return """
            <!DOCTYPE html>
            <html>
            <head>
                <title>Signal9 Web Functions API</title>
                <link rel="stylesheet" type="text/css" href="https://unpkg.com/swagger-ui-dist@5.9.0/swagger-ui.css" />
                <style>
                    html {
                        box-sizing: border-box;
                        overflow: -moz-scrollbars-vertical;
                        overflow-y: scroll;
                    }
                    *, *:before, *:after {
                        box-sizing: inherit;
                    }
                    body {
                        margin:0;
                        background: #fafafa;
                    }
                </style>
            </head>
            <body>
                <div id="swagger-ui"></div>
                <script src="https://unpkg.com/swagger-ui-dist@5.9.0/swagger-ui-bundle.js"></script>
                <script src="https://unpkg.com/swagger-ui-dist@5.9.0/swagger-ui-standalone-preset.js"></script>
                <script>
                window.onload = function() {
                    const ui = SwaggerUIBundle({
                        url: './openapi.json',
                        dom_id: '#swagger-ui',
                        deepLinking: true,
                        presets: [
                            SwaggerUIBundle.presets.apis,
                            SwaggerUIStandalonePreset
                        ],
                        plugins: [
                            SwaggerUIBundle.plugins.DownloadUrl
                        ],
                        layout: "StandaloneLayout"
                    });
                };
                </script>
            </body>
            </html>
            """;
    }
}
