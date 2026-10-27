using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;

namespace OSI.Signal9.Auth.Pages;

/// <summary>
/// The pages auth renders itself, as Razor components with static server-side rendering, which encodes everything it
/// writes. Each carries the same security headers: it loads nothing (<c>default-src 'none'</c>), posts its forms only
/// where it says, can't be framed except by whoever it says, and is never cached or sends a referrer.
/// </summary>
internal static class AuthPages
{
    /// <summary>Renders <typeparamref name="TPage"/> with <paramref name="parameters"/>.</summary>
    /// <param name="formAction">The CSP <c>form-action</c> sources: where the page's forms may post.</param>
    /// <param name="frameAncestors">The CSP <c>frame-ancestors</c> sources: who may frame the page.</param>
    public static IResult Render<TPage>(int statusCode, object parameters, string formAction = "'self'",
        string frameAncestors = "'none'") where TPage : IComponent =>
        new Page<TPage>(new RazorComponentResult<TPage>(parameters) { StatusCode = statusCode }, formAction, frameAncestors);

    /// <summary>A page that only tells the person something, with an optional link onwards.</summary>
    public static IResult Message(int statusCode, string title, string message, Uri? link = null, string? linkText = null,
        string formAction = "'self'", string frameAncestors = "'none'") =>
        Render<MessagePage>(statusCode, new { Title = title, Message = message, Link = link, LinkText = linkText },
            formAction, frameAncestors);

    private sealed class Page<TPage>(RazorComponentResult<TPage> page, string formAction, string frameAncestors) : IResult
        where TPage : IComponent
    {
        public Task ExecuteAsync(HttpContext context)
        {
            // Set as the page is sent, after anything earlier in the request (such as antiforgery) set its own.
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy =
                $"default-src 'none'; form-action {formAction}; frame-ancestors {frameAncestors}; base-uri 'none'";
            // For browsers without CSP frame-ancestors; it can't name who may frame the page, so only when no one may.
            if (frameAncestors == "'none'")
                headers.XFrameOptions = "DENY";
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            return page.ExecuteAsync(context);
        }
    }
}
