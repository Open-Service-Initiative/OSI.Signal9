using OidcScopes = OpenIddict.Abstractions.OpenIddictConstants.Scopes;

namespace OSI.Signal9.Server.Identity;

/// <summary>
/// The scope catalogue (#63, ADR-0010): every scope a token can carry. Each is <c>{noun}.{verb}</c> with one of five
/// verbs: <c>read</c>, <c>write</c>, <c>delete</c>, <c>transfer</c> and <c>execute</c>. The OpenID Connect scopes and
/// <c>agent</c> are special-purpose. The authorization server issues only these, and every api endpoint requires one.
/// </summary>
public static class Scopes
{
    public static class Tenants
    {
        public const string Read = "tenants.read";
        public const string Write = "tenants.write";
        public const string Delete = "tenants.delete";
        public const string Transfer = "tenants.transfer";
    }

    public static class Devices
    {
        public const string Read = "devices.read";
        public const string Write = "devices.write";
        public const string Delete = "devices.delete";
        /// <summary>Separate from <see cref="Write"/>: changing who controls a machine is granted deliberately.</summary>
        public const string Transfer = "devices.transfer";
    }

    public static class Commands
    {
        public const string Read = "commands.read";
        /// <summary>Predefined actions only. Running code needs <see cref="Scripts.Execute"/> or <see cref="Terminal.Execute"/>.</summary>
        public const string Execute = "commands.execute";
    }

    public static class Scripts
    {
        public const string Read = "scripts.read";
        public const string Write = "scripts.write";
        public const string Delete = "scripts.delete";
        public const string Execute = "scripts.execute";
    }

    public static class Terminal
    {
        public const string Execute = "terminal.execute";
    }

    public static class Telemetry
    {
        public const string Read = "telemetry.read";
    }

    public static class Enrollment
    {
        public const string Read = "enrollment.read";
        public const string Write = "enrollment.write";
    }

    public static class Users
    {
        public const string Read = "users.read";
        public const string Write = "users.write";
        public const string Transfer = "users.transfer";
    }

    public static class RoleAssignments
    {
        public const string Read = "roleassignments.read";
        public const string Write = "roleassignments.write";
    }

    /// <summary>Defining roles, separate from assigning them (<see cref="RoleAssignments"/>).</summary>
    public static class Roles
    {
        public const string Read = "roles.read";
        public const string Write = "roles.write";
    }

    public static class Audit
    {
        public const string Read = "audit.read";
    }

    public static class ApiApplications
    {
        public const string Read = "apiapplications.read";
        public const string Write = "apiapplications.write";
    }

    public static class Integrations
    {
        public const string Read = "integrations.read";
        public const string Write = "integrations.write";
    }

    /// <summary>Deployment-wide settings (#76). Needs a Superadministrator as well as the scope.</summary>
    public static class Deployment
    {
        public const string Read = "deployment.read";
        public const string Write = "deployment.write";
    }

    /// <summary>Only agents can obtain it: what an agent sends about itself and the work it's given.</summary>
    public const string Agent = "agent";

    /// <summary>Every scope in the catalogue, with what it allows.</summary>
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        [Tenants.Read] = "View tenants.",
        [Tenants.Write] = "Create and update tenants.",
        [Tenants.Delete] = "Delete tenants.",
        [Tenants.Transfer] = "Move tenants to another parent tenant.",
        [Devices.Read] = "View devices and their agents.",
        [Devices.Write] = "Update devices.",
        [Devices.Delete] = "Delete devices.",
        [Devices.Transfer] = "Move devices to another tenant.",
        [Commands.Read] = "View commands.",
        [Commands.Execute] = "Run predefined commands on devices.",
        [Scripts.Read] = "View saved scripts.",
        [Scripts.Write] = "Create and update saved scripts.",
        [Scripts.Delete] = "Delete saved scripts.",
        [Scripts.Execute] = "Run saved scripts on devices.",
        [Terminal.Execute] = "Run ad-hoc code and terminal sessions on devices.",
        [Telemetry.Read] = "View telemetry.",
        [Enrollment.Read] = "View enrollment tokens.",
        [Enrollment.Write] = "Create, rotate and revoke enrollment tokens.",
        [Users.Read] = "View users.",
        [Users.Write] = "Create, update and delete users.",
        [Users.Transfer] = "Move users to another owning tenant.",
        [RoleAssignments.Read] = "View role assignments.",
        [RoleAssignments.Write] = "Assign and remove roles.",
        [Roles.Read] = "View role definitions.",
        [Roles.Write] = "Define roles.",
        [Audit.Read] = "View the audit log.",
        [ApiApplications.Read] = "View API applications.",
        [ApiApplications.Write] = "Register, update and remove API applications.",
        [Integrations.Read] = "View integrations.",
        [Integrations.Write] = "Configure integrations.",
        [Deployment.Read] = "View deployment settings.",
        [Deployment.Write] = "Change deployment settings.",
        [OidcScopes.OpenId] = "Sign in with OpenID Connect.",
        [OidcScopes.Profile] = "Read the signed-in person's profile.",
        [OidcScopes.OfflineAccess] = "Stay signed in with a refresh token.",
        [Agent] = "Act as an enrolled agent.",
    };
}
