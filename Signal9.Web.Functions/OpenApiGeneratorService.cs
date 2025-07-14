using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Signal9.Web.Functions;

/// <summary>
/// Background service to generate static OpenAPI files at startup
/// </summary>
public class OpenApiGeneratorService : BackgroundService
{
    private readonly ILogger<OpenApiGeneratorService> _logger;
    private readonly IHostEnvironment _environment;

    public OpenApiGeneratorService(ILogger<OpenApiGeneratorService> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Only generate files in development environment
        if (!_environment.IsDevelopment())
        {
            return;
        }

        try
        {
            _logger.LogInformation("Generating static OpenAPI specification files...");

            // Generate JSON specification
            var jsonSpec = OpenApiDocumentGenerator.GenerateJson();
            var jsonPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "docs", "openapi.json");
            var jsonDirectory = Path.GetDirectoryName(jsonPath);
            
            if (!Directory.Exists(jsonDirectory))
            {
                Directory.CreateDirectory(jsonDirectory!);
            }
            
            await File.WriteAllTextAsync(jsonPath, jsonSpec, stoppingToken);
            _logger.LogInformation("Generated OpenAPI JSON specification at: {Path}", jsonPath);

            // Generate YAML specification
            var yamlSpec = OpenApiDocumentGenerator.GenerateYaml();
            var yamlPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "docs", "openapi.yaml");
            
            await File.WriteAllTextAsync(yamlPath, yamlSpec, stoppingToken);
            _logger.LogInformation("Generated OpenAPI YAML specification at: {Path}", yamlPath);

            _logger.LogInformation("OpenAPI specification files generated successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate OpenAPI specification files");
        }
    }
}
