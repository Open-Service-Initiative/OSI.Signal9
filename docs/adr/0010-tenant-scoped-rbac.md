# Access control is tenant-scoped RBAC, shaped like Azure RBAC

Signal9's access control follows Azure RBAC:
- A **role** is a named set of scopes.
- A **role assignment** grants one role to one user at one tenant, and covers that tenant and its descendants, as Azure assignments cover everything below their scope.
- **Effective access** is the token's scopes ∩ the role's scopes at the target tenant ∩ the tenant subtree, evaluated live.

Roles are stored as data, not code. The built-in roles (Administrator, Technician, Viewer) are seeded and can't be changed, so custom roles can be added later without reshaping the model.

## Considered Options

- **Permissions attached to each endpoint (`[RequirePermission]`).** Rejected: it duplicates ASP.NET's own scope attributes. Roles act on the same scopes that OAuth clients request.
- **Roles as a fixed enum in code.** Rejected: MSPs expect custom technician roles, and an enum would have to be reworked to support them.
- **Custom roles in this pass.** Deferred to their own spec. They interact with the Administrator protections and with delegating access management, and that needs its own design.

## Consequences

Scopes are split by risk so that roles, custom ones especially, can be granular:
- arbitrary script execution (`scripts.execute`) is separate from predefined commands (`commands.execute`) and always needs a recent MFA;
- changing a device's or tenant's owner (`.transfer`) and deleting things whose history would be lost (`.delete`) are separate from editing them (`.write`);
- defining roles (`roles.*`) is separate from assigning them (`roleassignments.*`).

Every scope is `{noun}.{verb}`, using one of five verbs: `read`, `write`, `delete`, `transfer` and `execute`. Each has a single meaning across every noun, so the catalogue stays predictable as it grows.
