using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using OSI.Signal9.API;
using OSI.Signal9.API.Infrastructure;
using OSI.Signal9.Contracts;

namespace OSI.Signal9.IntegrationTests;

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

    /// <summary>
    /// An endpoint needing a recent MFA requires only scopes listed as needing one: that list is what stops an application
    /// authenticating with a client secret from ever holding them, or anyone granting them without MFA (#124).
    /// </summary>
    [TestMethod]
    public void Every_endpoint_needing_a_recent_mfa_requires_only_scopes_listed_as_needing_one()
    {
        var violations = (
            from controller in typeof(ApiHost).Assembly.GetTypes().Where(t => t.IsAssignableTo(typeof(ControllerBase)))
            from action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            where action.GetCustomAttribute<OSI.Signal9.API.Authorization.RequireRecentMfaAttribute>() is not null
            from scope in action.GetCustomAttributes<OSI.Signal9.API.Authorization.RequireScopeAttribute>().SelectMany(r => r.Scopes)
            where !OSI.Signal9.Server.Identity.Scopes.RequiringRecentMfa.Contains(scope)
            select $"{controller.Name}.{action.Name} ({scope})").ToList();

        Assert.IsEmpty(violations, $"Add these scopes to Scopes.RequiringRecentMfa: {string.Join(", ", violations)}");
    }

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
