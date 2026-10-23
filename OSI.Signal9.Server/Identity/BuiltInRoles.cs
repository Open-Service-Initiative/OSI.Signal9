namespace OSI.Signal9.Server.Identity;

/// <summary>
/// The built-in roles and their scope bundles (#63, ADR-0010), seeded into the roles table. Changing a bundle needs a
/// migration. Superadministrator isn't one of them: it's an Entra app role with authority over everything (ADR-0006).
/// </summary>
public static class BuiltInRoles
{
    public sealed record Definition(Guid Id, string Name, string Description, IReadOnlyList<string> Scopes);

    private static readonly string[] ViewerScopes =
    [
        Identity.Scopes.Tenants.Read, Identity.Scopes.Devices.Read, Identity.Scopes.Commands.Read,
        Identity.Scopes.Scripts.Read, Identity.Scopes.Telemetry.Read, Identity.Scopes.Users.Read,
    ];

    private static readonly string[] TechnicianScopes =
    [
        .. ViewerScopes,
        Identity.Scopes.Devices.Write, Identity.Scopes.Devices.Transfer, Identity.Scopes.Commands.Execute,
        Identity.Scopes.Scripts.Write, Identity.Scopes.Scripts.Delete, Identity.Scopes.Scripts.Execute,
        Identity.Scopes.Terminal.Execute, Identity.Scopes.Users.Write, Identity.Scopes.Enrollment.Read,
        Identity.Scopes.Enrollment.Write,
    ];

    private static readonly string[] AdministratorScopes =
    [
        .. TechnicianScopes,
        Identity.Scopes.Tenants.Write, Identity.Scopes.Tenants.Delete, Identity.Scopes.Tenants.Transfer,
        Identity.Scopes.Devices.Delete, Identity.Scopes.RoleAssignments.Read, Identity.Scopes.RoleAssignments.Write,
        Identity.Scopes.Roles.Read, Identity.Scopes.Roles.Write, Identity.Scopes.Audit.Read,
        Identity.Scopes.ApiApplications.Read, Identity.Scopes.ApiApplications.Write, Identity.Scopes.Integrations.Read,
        Identity.Scopes.Integrations.Write, Identity.Scopes.Users.Transfer,
    ];

    public static readonly Definition Viewer = new(new Guid("0199a000-0000-7000-8000-000000000001"), "Viewer",
        "Read-only access to tenants, devices, commands, scripts, telemetry and users.", ViewerScopes);

    public static readonly Definition Technician = new(new Guid("0199a000-0000-7000-8000-000000000002"), "Technician",
        "Viewer, plus operating devices: running commands and scripts, enrolling devices and managing users without roles.",
        TechnicianScopes);

    public static readonly Definition Administrator = new(new Guid("0199a000-0000-7000-8000-000000000003"), "Administrator",
        "Technician, plus administering tenants, role assignments, API applications, integrations and the audit log.",
        AdministratorScopes);

    public static readonly IReadOnlyList<Definition> All = [Viewer, Technician, Administrator];
}
