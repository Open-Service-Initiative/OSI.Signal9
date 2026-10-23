using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;

namespace OSI.Signal9.API.Tests;

/// <summary>Rules every endpoint must follow, so resources added later inherit them.</summary>
[TestClass]
public sealed class ArchitectureTests
{
    [TestMethod]
    public void Every_single_resource_get_and_every_write_takes_part_in_optimistic_concurrency()
    {
        var controllers = typeof(ApiHost).Assembly.GetTypes().Where(t => t.IsAssignableTo(typeof(ControllerBase)) && !t.IsAbstract);

        var violations = NonConditionalEndpoints(controllers).ToList();

        Assert.IsEmpty(violations, $"Add [OptimisticConcurrency] to: {string.Join(", ", violations)}");
    }

    [TestMethod]
    public void The_concurrency_rule_catches_endpoints_that_dont_take_part()
    {
        var violations = NonConditionalEndpoints([typeof(UnconditionalController)]).ToList();

        CollectionAssert.AreEquivalent(
            new[] { "UnconditionalController.Get", "UnconditionalController.GetUntyped", "UnconditionalController.Create", "UnconditionalController.Delete", "UnconditionalController.Approve" },
            violations);
    }

    /// <summary>
    /// Endpoints that must be <see cref="OptimisticConcurrencyAttribute"/> but aren't: every write, and every GET except a
    /// collection (one returning <see cref="PagedResult{T}"/>).
    /// </summary>
    private static IEnumerable<string> NonConditionalEndpoints(IEnumerable<Type> controllers) =>
        from controller in controllers
        from action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        let methods = action.GetCustomAttributes<HttpMethodAttribute>().SelectMany(a => a.HttpMethods).ToList()
        where methods.Count > 0
        where methods.Any(m => !HttpMethods.IsGet(m)) || !ReturnsCollection(action)
        where action.GetCustomAttribute<OptimisticConcurrencyAttribute>() is null
        select $"{controller.Name}.{action.Name}";

    private static bool ReturnsCollection(MethodInfo action) =>
        action.GetCustomAttributes<ProducesResponseTypeAttribute>()
            .Any(p => p.StatusCode == StatusCodes.Status200OK && IsPagedResult(p.Type))
        || IsPagedResult(Unwrap(action.ReturnType));

    private static bool IsPagedResult(Type? type) => type is { IsGenericType: true } && type.GetGenericTypeDefinition() == typeof(PagedResult<>);

    private static Type Unwrap(Type type)
    {
        while (type.IsGenericType && type.GetGenericTypeDefinition() is var definition
            && (definition == typeof(Task<>) || definition == typeof(ValueTask<>) || definition == typeof(ActionResult<>)))
            type = type.GetGenericArguments()[0];
        return type;
    }

    [Route("unconditional")]
    private sealed class UnconditionalController : ControllerBase
    {
        [HttpGet]
        public PagedResult<string> List() => throw new NotSupportedException();

        [HttpGet("{id}")]
        public ActionResult<string> Get(string id) => throw new NotSupportedException();

        [HttpGet("{id}/untyped")]
        public IActionResult GetUntyped(string id) => throw new NotSupportedException();

        [HttpPost]
        public IActionResult Create() => throw new NotSupportedException();

        [HttpDelete("{id}")]
        public IActionResult Delete(string id) => throw new NotSupportedException();

        [HttpPost("{id}:approve")]
        public IActionResult Approve(string id) => throw new NotSupportedException();

        [HttpGet("{id}/conditional")]
        [OptimisticConcurrency]
        public IActionResult Conditional(string id) => throw new NotSupportedException();
    }
}
