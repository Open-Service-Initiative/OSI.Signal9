using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace OSI.Signal9.IntegrationTests;

[TestClass]
public sealed class OpenApiTests : IDisposable
{
    private readonly Signal9ApiFactory _factory = new(migrated: false);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Single_resource_gets_document_the_etag_if_none_match_and_304()
    {
        var get = (await DocumentAsync()).Paths["/api/v1/tenants/{tenantId}"]!.Operations![HttpMethod.Get];

        Assert.IsTrue(HasHeaderParameter(get, "If-None-Match"));
        Assert.IsTrue(get.Responses!["200"].Headers!.ContainsKey("ETag"));
        Assert.IsTrue(get.Responses["304"].Headers!.ContainsKey("ETag"));
    }

    [TestMethod]
    public async Task Writes_document_if_match_the_412_and_the_new_etag()
    {
        var tenant = (await DocumentAsync()).Paths["/api/v1/tenants/{tenantId}"]!.Operations!;
        var create = (await DocumentAsync()).Paths["/api/v1/tenants"]!.Operations![HttpMethod.Post];

        foreach (var write in new[] { tenant[HttpMethod.Put], tenant[HttpMethod.Delete], create })
        {
            Assert.IsTrue(HasHeaderParameter(write, "If-Match"), write.OperationId);
            Assert.IsTrue(write.Responses!.ContainsKey("412"), write.OperationId);
        }
        Assert.IsTrue(tenant[HttpMethod.Put].Responses!["200"].Headers!.ContainsKey("ETag"));
        Assert.IsTrue(create.Responses!["201"].Headers!.ContainsKey("ETag"));
        Assert.IsFalse(tenant[HttpMethod.Delete].Responses!["204"].Headers?.ContainsKey("ETag") == true);
    }

    [TestMethod]
    public async Task Collections_do_not_document_etags()
    {
        var list = (await DocumentAsync()).Paths["/api/v1/tenants"]!.Operations![HttpMethod.Get];

        Assert.IsFalse(HasHeaderParameter(list, "If-None-Match"));
        Assert.IsFalse(list.Responses!.ContainsKey("304"));
    }

    private Task<OpenApiDocument> DocumentAsync() =>
        _factory.Services.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1").GetOpenApiDocumentAsync(TestContext.CancellationToken);

    private static bool HasHeaderParameter(OpenApiOperation operation, string name) =>
        operation.Parameters?.Any(p => p.In == ParameterLocation.Header && p.Name == name) == true;

    public void Dispose() => _factory.Dispose();
}
