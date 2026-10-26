using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OSI.Signal9.Contracts.Audit;
using OSI.Signal9.Server.Data;
using OSI.Signal9.Server.Identity;

namespace OSI.Signal9.Auth;

/// <summary>
/// Redeeming an invitation (#121, stories 31-34): the link opens a page on auth, which first signs the person in with
/// Entra, then shows what the invitation offers. Accepting it (a POST, so link previewers and scanners that fetch the
/// link can't use it up) creates the person's user if they have none, grants the role assignment and marks the
/// invitation redeemed, in one transaction. The invitation is found only by its reference and the person only by their
/// Entra identity (<c>tid</c>, <c>oid</c>), never by an email claim.
/// </summary>
public static class InvitationRedemption
{
    /// <summary>Where a sign-in at Entra for an invitation returns to.</summary>
    public const string ReturnPath = "/" + InvitationReference.Path + "/";

    public static IEndpointConventionBuilder MapInvitationRedemption(this IEndpointRouteBuilder endpoints)
    {
        var invitation = endpoints.MapGroup(InvitationReference.Path + "/{reference:maxlength(100)}");
        invitation.MapGet("", ShowAsync);
        invitation.MapPost("", RedeemAsync);
        return invitation.AllowAnonymous().ExcludeFromDescription();
    }

    /// <summary>Why a signed-in person can't redeem an invitation; none of them uses it up.</summary>
    private enum Refusal
    {
        /// <summary>The invitation is pinned to another Entra directory (story 33).</summary>
        OtherDirectory,

        /// <summary>The invitation is for an existing user with another Entra identity (story 35).</summary>
        OtherPerson,
    }

    private static async Task<IResult> ShowAsync(string reference, HttpContext context, Signal9DbContext db,
        SignInSessions sessions, AuditLog audit, IAntiforgery antiforgery, TimeProvider time)
    {
        var offer = await FindAsync(db, reference, context.RequestAborted);
        if (Unavailable(offer, time) is { } unavailable)
            return unavailable;

        if (Enum.TryParse<EntraSignIn.Rejection>(context.Request.Query[EntraSignIn.RejectionParameter], out var rejection))
            return Page(StatusCodes.Status403Forbidden, "Sign-in refused", EntraSignIn.Describe(rejection));

        if (await SignedInAsync(context, sessions) is not { } signIn)
            return SignInFirst(reference);
        if (await RefuseAsync(context, db, audit, offer!, signIn) is { } refused)
            return refused;

        var tokens = antiforgery.GetAndStoreTokens(context);
        var html = HtmlEncoder.Default;
        return Markup(StatusCodes.Status200OK, $"Join {offer!.TenantName}?", $"""
            <p>You've been invited to <strong>{html.Encode(offer.TenantName)}</strong> as a <strong>{html.Encode(offer.RoleName)}</strong>: {html.Encode(offer.RoleDescription)}</p>
            <p>You're signed in as {html.Encode(signIn.Principal.FindFirst("name")?.Value ?? signIn.Person.Subject)}. Accepting gives this account the role; the invitation then can't be used again.</p>
            <form method="post" action="{html.Encode(ReturnPath + reference)}">
            <input type="hidden" name="{html.Encode(tokens.FormFieldName)}" value="{html.Encode(tokens.RequestToken ?? "")}">
            <button type="submit">Accept</button>
            </form>
            """);
    }

    private static async Task<IResult> RedeemAsync(string reference, HttpContext context, Signal9DbContext db,
        SignInSessions sessions, AuditLog audit, IAntiforgery antiforgery, TimeProvider time, IdentityOptions identity)
    {
        var cancellationToken = context.RequestAborted;
        // Only the page auth showed in this person's session can accept it (OWASP CSRF prevention).
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            return Page(StatusCodes.Status400BadRequest, "Invitation not confirmed",
                "Accept the invitation on the page its link opens.");
        }

        var offer = await FindAsync(db, reference, cancellationToken);
        if (Unavailable(offer, time) is { } unavailable)
            return unavailable;
        // A session that ended since the page was shown signs in again, then shows the page again.
        if (await SignedInAsync(context, sessions) is not { } signIn)
            return SignInFirst(reference);
        if (await RefuseAsync(context, db, audit, offer!, signIn) is { } refused)
            return refused;

        var invitation = offer!.Invitation;
        var person = signIn.Person;
        var now = time.GetUtcNow().UtcDateTime;
        var user = await db.Users.IgnoreTenantScope(TenantScopeBypass.InvitationRedemption)
            .SingleOrDefaultAsync(u => u.EntraTenantId == person.TenantId && u.EntraObjectId == person.ObjectId, cancellationToken);
        if (user is null)
        {
            // A new user belongs to the tenant that invited them, whose Administrators then govern them (#63).
            user = new User
            {
                Id = Guid.CreateVersion7(time.GetUtcNow()),
                TenantId = invitation.TenantId,
                EntraTenantId = person.TenantId,
                EntraObjectId = person.ObjectId,
                DisplayName = DisplayName(signIn.Principal, person),
                CreatedAt = now,
            };
            db.Users.Add(user);
            // As when an Administrator creates a user (#117).
            var created = Entry(context, signIn, invitation, AuditActions.UserCreated, AuditOutcome.Succeeded, user.Id);
            created.TargetType = "user";
            created.TargetId = user.Id;
            created.RoleId = null;
            audit.Add(created);
        }
        else if (await db.RoleAssignments.IgnoreTenantScope(TenantScopeBypass.InvitationRedemption).AnyAsync(a =>
                     a.UserId == user.Id && a.TenantId == invitation.TenantId && a.RoleId == invitation.RoleId, cancellationToken))
        {
            return Page(StatusCodes.Status409Conflict, "You already have this role",
                $"You're already a {offer.RoleName} at {offer.TenantName}, so the invitation is still unused.");
        }

        var assignment = new RoleAssignment
        {
            Id = Guid.CreateVersion7(time.GetUtcNow()),
            TenantId = invitation.TenantId,
            UserId = user.Id,
            RoleId = invitation.RoleId,
            CreatedAt = now,
        };
        db.RoleAssignments.Add(assignment);
        invitation.RedeemedAt = now;
        invitation.RedeemedByUserId = user.Id;
        audit.Add(Entry(context, signIn, invitation, AuditActions.InvitationRedeemed, AuditOutcome.Succeeded, user.Id));
        // The grant itself, as when an Administrator grants a role (#116).
        var granted = Entry(context, signIn, invitation, AuditActions.RoleAssignmentCreated, AuditOutcome.Succeeded, user.Id);
        granted.TargetType = "role-assignment";
        granted.TargetId = assignment.Id;
        audit.Add(granted);
        try
        {
            // One transaction. The invitation's row version makes it single use: a concurrent redemption or revocation
            // changes it, and this one then saves nothing.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Page(StatusCodes.Status410Gone, "Invitation already used",
                "This invitation was just used or revoked. Ask whoever invited you for a new one.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Page(StatusCodes.Status409Conflict, "Try again",
                "Another invitation was being redeemed for you at the same time. Open this invitation's link again.");
        }

        var dashboard = identity.WebUri is { } web
            ? $"""<p><a href="{HtmlEncoder.Default.Encode(web.ToString())}">Open the Signal9 dashboard</a></p>"""
            : "";
        return Page(StatusCodes.Status200OK, "Invitation accepted",
            $"You're now a {offer.RoleName} at {offer.TenantName}.", dashboard);
    }

    /// <summary>Sends the person to Entra to sign in, then back to the invitation's page.</summary>
    private static IResult SignInFirst(string reference) =>
        Results.Challenge(new OpenIdConnectChallengeProperties { RedirectUri = ReturnPath + reference }, [EntraSignIn.ChallengeScheme]);

    private sealed record Offer(Invitation Invitation, string TenantName, string RoleName, string RoleDescription);

    private sealed record SignIn(ClaimsPrincipal Principal, EntraPerson Person);

    /// <summary>The invitation the reference names, tracked for redemption, with what it offers.</summary>
    private static async Task<Offer?> FindAsync(Signal9DbContext db, string reference, CancellationToken cancellationToken)
    {
        var hash = InvitationReference.Hash(reference);
        var invitation = await db.Invitations.IgnoreTenantScope(TenantScopeBypass.InvitationRedemption)
            .SingleOrDefaultAsync(i => i.ReferenceHash == hash, cancellationToken);
        if (invitation is null)
            return null;
        var tenant = await db.Tenants.AsNoTracking().IgnoreTenantScope(TenantScopeBypass.InvitationRedemption)
            .Where(t => t.Id == invitation.TenantId).Select(t => t.Name).SingleAsync(cancellationToken);
        var role = await db.Roles.AsNoTracking().SingleAsync(r => r.Id == invitation.RoleId, cancellationToken);
        return new Offer(invitation, tenant, role.Name, role.Description);
    }

    /// <summary>The page for an invitation that can't be redeemed at all, or null when it can.</summary>
    private static IResult? Unavailable(Offer? offer, TimeProvider time)
    {
        if (offer is null)
            return Page(StatusCodes.Status404NotFound, "Invitation not found",
                "This link doesn't match any invitation. Check you copied all of it.");

        var invitation = offer.Invitation;
        var reason = invitation.RedeemedAt is not null ? "has already been used"
            : invitation.RevokedAt is not null ? "has been withdrawn"
            : invitation.ExpiresAt <= time.GetUtcNow().UtcDateTime ? "has expired"
            : null;
        // Gone for good (RFC 9110 §15.5.11): an invitation never becomes usable again.
        return reason is null ? null : Page(StatusCodes.Status410Gone, "Invitation no longer valid",
            $"This invitation {reason}. Ask whoever invited you for a new one.");
    }

    /// <summary>
    /// The person signed in at auth, unless their session has since ended at Entra (front-channel logout, story 51), in
    /// which case they sign in again.
    /// </summary>
    private static async Task<SignIn?> SignedInAsync(HttpContext context, SignInSessions sessions)
    {
        var session = await context.AuthenticateAsync(EntraSignIn.CookieScheme);
        if (!session.Succeeded || EntraPerson.From(session.Principal!) is not { } person)
            return null;
        if (session.Principal!.FindFirst("sid")?.Value is { } sid
            && await sessions.HasEndedAsync(person.TenantId, sid,
                EntraSignIn.SignedInAt(session.Properties!) ?? DateTimeOffset.MinValue, context.RequestAborted))
        {
            await context.SignOutAsync(EntraSignIn.CookieScheme);
            return null;
        }
        return new SignIn(session.Principal!, person);
    }

    /// <summary>
    /// Refuses a person the invitation isn't for, by Entra identity alone: its directory pin, or the existing user it
    /// names. Refusals are audited and leave the invitation unused.
    /// </summary>
    private static async Task<IResult?> RefuseAsync(HttpContext context, Signal9DbContext db, AuditLog audit, Offer offer,
        SignIn signIn)
    {
        var invitation = offer.Invitation;
        Refusal? refusal = null;
        if (invitation.EntraTenantId is { } pinned && pinned != signIn.Person.TenantId)
            refusal = Refusal.OtherDirectory;
        else if (invitation.UserId is { } userId && !await db.Users.IgnoreTenantScope(TenantScopeBypass.InvitationRedemption)
                     .AnyAsync(u => u.Id == userId && u.EntraTenantId == signIn.Person.TenantId
                         && u.EntraObjectId == signIn.Person.ObjectId, context.RequestAborted))
            refusal = Refusal.OtherPerson;
        if (refusal is null)
            return null;

        var entry = Entry(context, signIn, invitation, AuditActions.InvitationRedeemed, AuditOutcome.Failed, userId: null);
        entry.Detail = refusal.ToString();
        await audit.WriteAsync(entry, context.RequestAborted);
        return Page(StatusCodes.Status403Forbidden, "This invitation isn't for you", refusal switch
        {
            Refusal.OtherDirectory => "It can only be accepted by someone signing in from another Entra directory.",
            _ => "It was sent to someone else. Ask whoever invited you to invite you.",
        });
    }

    private static AuditEntry Entry(HttpContext context, SignIn signIn, Invitation invitation, string action,
        AuditOutcome outcome, Guid? userId) => new()
        {
            Action = action,
            Outcome = outcome,
            TenantId = invitation.TenantId,
            ActorKind = AuditActorKind.Person,
            ActorSubject = signIn.Person.Subject,
            TargetType = "invitation",
            TargetId = invitation.Id,
            UserId = userId,
            RoleId = invitation.RoleId,
            ClientIp = context.Connection.RemoteIpAddress?.ToString(),
        };

    /// <summary>The person's name as Entra gives it, for the dashboard to show; their identity is never taken from it.</summary>
    private static string DisplayName(ClaimsPrincipal signIn, EntraPerson person) =>
        signIn.FindFirst("name")?.Value is { Length: > 0 } name ? name[..Math.Min(name.Length, 200)] : person.Subject;

    /// <summary>
    /// One of the invitation's pages. Its URL holds the reference, so it can't be framed, cached or scripted, and never
    /// sends a referrer.
    /// </summary>
    private static IResult Page(int status, string title, string message, string extraHtml = "") =>
        new InvitationPage(status, title, $"<p>{HtmlEncoder.Default.Encode(message)}</p>\n{extraHtml}");

    private static IResult Markup(int status, string title, string bodyHtml) => new InvitationPage(status, title, bodyHtml);

    private sealed class InvitationPage(int status, string title, string bodyHtml) : IResult
    {
        public Task ExecuteAsync(HttpContext context)
        {
            var html = HtmlEncoder.Default;
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
            headers.XFrameOptions = "DENY";
            headers.CacheControl = "no-store";
            headers["Referrer-Policy"] = "no-referrer";
            return Results.Content($"""
                <!doctype html>
                <html lang="en">
                <head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>{html.Encode(title)} - Signal9</title></head>
                <body>
                <main>
                <h1>{html.Encode(title)}</h1>
                {bodyHtml}
                </main>
                </body>
                </html>
                """, "text/html; charset=utf-8", statusCode: status).ExecuteAsync(context);
        }
    }
}
