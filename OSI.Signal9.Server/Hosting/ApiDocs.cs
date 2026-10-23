namespace OSI.Signal9.Server.Hosting;

/// <summary>
/// Where the public API reference lives (#135): <c>api</c> generates the OpenAPI document, and <c>web</c> serves it,
/// with Scalar, at <see cref="Path"/> (stories 132-133). The document has the same path on both hosts, so web forwards
/// it unchanged.
/// </summary>
public static class ApiDocs
{
    public const string Path = "/docs";

    /// <summary>The OpenAPI document's route; <c>v1</c> is the only document.</summary>
    public const string DocumentPattern = Path + "/openapi/{documentName}.json";
}
