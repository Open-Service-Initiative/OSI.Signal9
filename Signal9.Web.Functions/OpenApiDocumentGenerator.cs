using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Writers;
using Signal9.Shared.DTOs;
using System.Text;

namespace Signal9.Web.Functions;

/// <summary>
/// Generates OpenAPI documentation for the Signal9 Web Functions API
/// </summary>
public static class OpenApiDocumentGenerator
{
    /// <summary>
    /// Generates the complete OpenAPI document for Signal9 Web Functions
    /// </summary>
    public static OpenApiDocument GenerateDocument()
    {
        var document = new OpenApiDocument
        {
            Info = new OpenApiInfo
            {
                Title = "Signal9 Web Functions API",
                Version = "1.0.0",
                Description = "REST API for Signal9 Remote Monitoring and Management platform",
                Contact = new OpenApiContact
                {
                    Name = "Signal9 Support",
                    Email = "support@signal9.com"
                }
            },
            Servers = new List<OpenApiServer>
            {
                new OpenApiServer
                {
                    Url = "http://localhost:7072/api",
                    Description = "Local development server"
                },
                new OpenApiServer
                {
                    Url = "https://{function-app-name}.azurewebsites.net/api",
                    Description = "Production server"
                }
            }
        };

        // Add paths
        document.Paths = new OpenApiPaths();

        // Tenant endpoints
        AddTenantPaths(document);
        
        // Agent endpoints
        AddAgentPaths(document);

        // Add schemas
        AddSchemas(document);

        return document;
    }

    /// <summary>
    /// Generates OpenAPI document as JSON string
    /// </summary>
    public static string GenerateJson()
    {
        var document = GenerateDocument();
        var outputStream = new MemoryStream();
        var writer = new OpenApiJsonWriter(new StreamWriter(outputStream));
        document.SerializeAsV3(writer);
        writer.Flush();
        outputStream.Seek(0, SeekOrigin.Begin);
        return new StreamReader(outputStream).ReadToEnd();
    }

    /// <summary>
    /// Generates OpenAPI document as YAML string
    /// </summary>
    public static string GenerateYaml()
    {
        var document = GenerateDocument();
        var outputStream = new MemoryStream();
        var writer = new OpenApiYamlWriter(new StreamWriter(outputStream));
        document.SerializeAsV3(writer);
        writer.Flush();
        outputStream.Seek(0, SeekOrigin.Begin);
        return new StreamReader(outputStream).ReadToEnd();
    }

    private static void AddTenantPaths(OpenApiDocument document)
    {
        // GET /tenants
        document.Paths.Add("/tenants", new OpenApiPathItem
        {
            Operations = new Dictionary<OperationType, OpenApiOperation>
            {
                [OperationType.Get] = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Tenants" } },
                    Summary = "Get all tenants",
                    Description = "Retrieve all tenants with optional filtering and pagination",
                    Parameters = new List<OpenApiParameter>
                    {
                        new OpenApiParameter
                        {
                            Name = "parentTenantId",
                            In = ParameterLocation.Query,
                            Required = false,
                            Schema = new OpenApiSchema { Type = "string", Format = "uuid" },
                            Description = "Filter by parent tenant ID"
                        },
                        new OpenApiParameter
                        {
                            Name = "tenantType",
                            In = ParameterLocation.Query,
                            Required = false,
                            Schema = new OpenApiSchema { Type = "string" },
                            Description = "Filter by tenant type"
                        },
                        new OpenApiParameter
                        {
                            Name = "isActive",
                            In = ParameterLocation.Query,
                            Required = false,
                            Schema = new OpenApiSchema { Type = "boolean" },
                            Description = "Filter by active status"
                        },
                        new OpenApiParameter
                        {
                            Name = "page",
                            In = ParameterLocation.Query,
                            Required = false,
                            Schema = new OpenApiSchema { Type = "integer", Default = new Microsoft.OpenApi.Any.OpenApiInteger(1) },
                            Description = "Page number"
                        },
                        new OpenApiParameter
                        {
                            Name = "pageSize",
                            In = ParameterLocation.Query,
                            Required = false,
                            Schema = new OpenApiSchema { Type = "integer", Default = new Microsoft.OpenApi.Any.OpenApiInteger(20) },
                            Description = "Items per page"
                        }
                    },
                    Responses = new OpenApiResponses
                    {
                        ["200"] = new OpenApiResponse
                        {
                            Description = "Success",
                            Content = new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new OpenApiMediaType
                                {
                                    Schema = new OpenApiSchema
                                    {
                                        Reference = new OpenApiReference
                                        {
                                            Type = ReferenceType.Schema,
                                            Id = "PaginatedTenantResponse"
                                        }
                                    }
                                }
                            }
                        }
                    }
                },
                [OperationType.Post] = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Tenants" } },
                    Summary = "Create a new tenant",
                    Description = "Create a new tenant in the system",
                    RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType
                            {
                                Schema = new OpenApiSchema
                                {
                                    Reference = new OpenApiReference
                                    {
                                        Type = ReferenceType.Schema,
                                        Id = "CreateTenantRequest"
                                    }
                                }
                            }
                        }
                    },
                    Responses = new OpenApiResponses
                    {
                        ["201"] = new OpenApiResponse
                        {
                            Description = "Created",
                            Content = new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new OpenApiMediaType
                                {
                                    Schema = new OpenApiSchema
                                    {
                                        Reference = new OpenApiReference
                                        {
                                            Type = ReferenceType.Schema,
                                            Id = "TenantDto"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });

        // GET /tenants/{tenantId}
        document.Paths.Add("/tenants/{tenantId}", new OpenApiPathItem
        {
            Operations = new Dictionary<OperationType, OpenApiOperation>
            {
                [OperationType.Get] = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Tenants" } },
                    Summary = "Get tenant by ID",
                    Description = "Retrieve a specific tenant by its ID",
                    Parameters = new List<OpenApiParameter>
                    {
                        new OpenApiParameter
                        {
                            Name = "tenantId",
                            In = ParameterLocation.Path,
                            Required = true,
                            Schema = new OpenApiSchema { Type = "string", Format = "uuid" },
                            Description = "The tenant ID"
                        }
                    },
                    Responses = new OpenApiResponses
                    {
                        ["200"] = new OpenApiResponse
                        {
                            Description = "Success",
                            Content = new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new OpenApiMediaType
                                {
                                    Schema = new OpenApiSchema
                                    {
                                        Reference = new OpenApiReference
                                        {
                                            Type = ReferenceType.Schema,
                                            Id = "TenantDto"
                                        }
                                    }
                                }
                            }
                        },
                        ["404"] = new OpenApiResponse
                        {
                            Description = "Tenant not found"
                        }
                    }
                }
            }
        });
    }

    private static void AddAgentPaths(OpenApiDocument document)
    {
        // GET /agents
        document.Paths.Add("/agents", new OpenApiPathItem
        {
            Operations = new Dictionary<OperationType, OpenApiOperation>
            {
                [OperationType.Get] = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Agents" } },
                    Summary = "Get all agents",
                    Description = "Retrieve all agents with optional filtering and pagination",
                    Parameters = new List<OpenApiParameter>
                    {
                        new OpenApiParameter
                        {
                            Name = "tenantId",
                            In = ParameterLocation.Query,
                            Required = false,
                            Schema = new OpenApiSchema { Type = "string", Format = "uuid" },
                            Description = "Filter by tenant ID"
                        },
                        new OpenApiParameter
                        {
                            Name = "status",
                            In = ParameterLocation.Query,
                            Required = false,
                            Schema = new OpenApiSchema { Type = "string" },
                            Description = "Filter by agent status"
                        },
                        new OpenApiParameter
                        {
                            Name = "groupName",
                            In = ParameterLocation.Query,
                            Required = false,
                            Schema = new OpenApiSchema { Type = "string" },
                            Description = "Filter by group name"
                        },
                        new OpenApiParameter
                        {
                            Name = "page",
                            In = ParameterLocation.Query,
                            Required = false,
                            Schema = new OpenApiSchema { Type = "integer", Default = new Microsoft.OpenApi.Any.OpenApiInteger(1) },
                            Description = "Page number"
                        },
                        new OpenApiParameter
                        {
                            Name = "pageSize",
                            In = ParameterLocation.Query,
                            Required = false,
                            Schema = new OpenApiSchema { Type = "integer", Default = new Microsoft.OpenApi.Any.OpenApiInteger(20) },
                            Description = "Items per page"
                        }
                    },
                    Responses = new OpenApiResponses
                    {
                        ["200"] = new OpenApiResponse
                        {
                            Description = "Success",
                            Content = new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new OpenApiMediaType
                                {
                                    Schema = new OpenApiSchema
                                    {
                                        Reference = new OpenApiReference
                                        {
                                            Type = ReferenceType.Schema,
                                            Id = "PaginatedAgentResponse"
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        });
    }

    private static void AddSchemas(OpenApiDocument document)
    {
        document.Components = new OpenApiComponents
        {
            Schemas = new Dictionary<string, OpenApiSchema>
            {
                ["TenantDto"] = new OpenApiSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, OpenApiSchema>
                    {
                        ["id"] = new OpenApiSchema { Type = "string", Format = "uuid" },
                        ["name"] = new OpenApiSchema { Type = "string" },
                        ["code"] = new OpenApiSchema { Type = "string", Nullable = true },
                        ["description"] = new OpenApiSchema { Type = "string", Nullable = true },
                        ["parentTenantId"] = new OpenApiSchema { Type = "string", Format = "uuid", Nullable = true },
                        ["tenantType"] = new OpenApiSchema { Type = "string", Nullable = true },
                        ["contactEmail"] = new OpenApiSchema { Type = "string", Nullable = true },
                        ["contactPhone"] = new OpenApiSchema { Type = "string", Nullable = true },
                        ["plan"] = new OpenApiSchema { Type = "string" },
                        ["maxAgents"] = new OpenApiSchema { Type = "integer" },
                        ["isActive"] = new OpenApiSchema { Type = "boolean" },
                        ["createdAt"] = new OpenApiSchema { Type = "string", Format = "date-time" },
                        ["updatedAt"] = new OpenApiSchema { Type = "string", Format = "date-time", Nullable = true },
                        ["agentCount"] = new OpenApiSchema { Type = "integer" }
                    }
                },
                ["AgentDto"] = new OpenApiSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, OpenApiSchema>
                    {
                        ["id"] = new OpenApiSchema { Type = "string" },
                        ["name"] = new OpenApiSchema { Type = "string" },
                        ["machineName"] = new OpenApiSchema { Type = "string" },
                        ["operatingSystem"] = new OpenApiSchema { Type = "string" },
                        ["status"] = new OpenApiSchema { Type = "string" },
                        ["isOnline"] = new OpenApiSchema { Type = "boolean" },
                        ["lastSeen"] = new OpenApiSchema { Type = "string", Format = "date-time", Nullable = true },
                        ["tenantId"] = new OpenApiSchema { Type = "string", Format = "uuid", Nullable = true },
                        ["tenantName"] = new OpenApiSchema { Type = "string", Nullable = true }
                    }
                },
                ["CreateTenantRequest"] = new OpenApiSchema
                {
                    Type = "object",
                    Required = new HashSet<string> { "name" },
                    Properties = new Dictionary<string, OpenApiSchema>
                    {
                        ["name"] = new OpenApiSchema { Type = "string" },
                        ["code"] = new OpenApiSchema { Type = "string", Nullable = true },
                        ["description"] = new OpenApiSchema { Type = "string", Nullable = true },
                        ["parentTenantId"] = new OpenApiSchema { Type = "string", Format = "uuid", Nullable = true },
                        ["tenantType"] = new OpenApiSchema { Type = "string", Nullable = true },
                        ["contactEmail"] = new OpenApiSchema { Type = "string", Nullable = true },
                        ["contactPhone"] = new OpenApiSchema { Type = "string", Nullable = true }
                    }
                },
                ["PaginatedTenantResponse"] = new OpenApiSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, OpenApiSchema>
                    {
                        ["items"] = new OpenApiSchema
                        {
                            Type = "array",
                            Items = new OpenApiSchema
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.Schema,
                                    Id = "TenantDto"
                                }
                            }
                        },
                        ["page"] = new OpenApiSchema { Type = "integer" },
                        ["pageSize"] = new OpenApiSchema { Type = "integer" },
                        ["totalCount"] = new OpenApiSchema { Type = "integer" },
                        ["totalPages"] = new OpenApiSchema { Type = "integer" }
                    }
                },
                ["PaginatedAgentResponse"] = new OpenApiSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, OpenApiSchema>
                    {
                        ["items"] = new OpenApiSchema
                        {
                            Type = "array",
                            Items = new OpenApiSchema
                            {
                                Reference = new OpenApiReference
                                {
                                    Type = ReferenceType.Schema,
                                    Id = "AgentDto"
                                }
                            }
                        },
                        ["page"] = new OpenApiSchema { Type = "integer" },
                        ["pageSize"] = new OpenApiSchema { Type = "integer" },
                        ["totalCount"] = new OpenApiSchema { Type = "integer" },
                        ["totalPages"] = new OpenApiSchema { Type = "integer" }
                    }
                }
            }
        };
    }
}
