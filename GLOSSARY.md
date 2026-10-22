# Signal9

Remote monitoring and management of machines, organized under a hierarchy of tenants that can model anything from a single enterprise to a consultant overseeing several MSPs.

## Deployment

**Deployment**:
One running installation of Signal9, with its own tenant trees, users and keys.
_Avoid_: Installation, environment

**Operator**:
The organization that runs a deployment and owns its infrastructure and Entra app registration. An organization, unlike a Superadministrator, who is a person; an operator's own MSP is usually also a tenant in its deployment.
_Avoid_: Host, provider, vendor

**Setup code**:
A one-time secret a new deployment writes to its logs while it has no Superadministrator. Presenting it is the only way to start first-run setup, and it stops working once setup completes.
_Avoid_: Bootstrap token, install code, admin password

## Tenancy

**Tenant**:
A node in the organizational hierarchy: any unit worth managing separately, such as a consultancy, an MSP, a client company or a client site. Tenants nest to a configurable maximum depth.
_Avoid_: Organization, customer, client, site, account

**Parent tenant**:
The tenant directly above another in the hierarchy. A tenant has at most one parent.

**Root tenant**:
A tenant with no parent.

**Descendants**:
Every tenant below a given tenant, at any depth. A tenant's data rolls up to all of its ancestors.
_Avoid_: Children (unless you mean only the direct ones), sub-tenants

**Tenant transfer**:
Moving a tenant, with its descendants, to a new parent tenant. One side proposes it and the other accepts it.
_Avoid_: Move, re-parent, migration

## Access

**User**:
A person known to Signal9, belonging to exactly one tenant, whether or not they can sign in yet. Being a user grants no access by itself.
_Avoid_: Account, member, login, end user and contact (as separate kinds of person)

**Role**:
A named set of things its holder may do, granted to a user only through a role assignment. Signal9 ships built-in roles that can't be changed.
_Avoid_: Permission set, group, profile

**Role assignment**:
A grant of one role to one user at one tenant (the assigned tenant), covering that tenant and its descendants. The only source of a user's access, apart from being a Superadministrator.
_Avoid_: Permission, membership, grant, home tenant (say "assigned tenant")

**Superadministrator**:
A user with full authority over the whole deployment, above every tenant tree. Not a role assignment.
_Avoid_: Owner, operator, global admin, root

**Administrator**:
A role that administers its assigned tenant and every descendant, short of a few irreversible actions on the assigned tenant itself.
_Avoid_: Admin (in docs and UI), manager

**Technician**:
A role that operates devices in its assigned tenant and descendants, including running commands and enrolling new devices, and that manages users who hold no role assignments.
_Avoid_: Operator, tech, engineer

**Viewer**:
A read-only role over its assigned tenant and descendants.
_Avoid_: Read-only user, auditor

**Invitation**:
A single-use, expiring offer of one specific role assignment to one person. Redeeming it grants the role assignment and creates the user first if they don't exist yet.
_Avoid_: Invite code, sign-up link

**API application**:
An external system granted OAuth access to the Signal9 API, registered at a tenant by an Administrator and confined to that tenant and its descendants. It acts either as itself or on behalf of a signed-in user.
_Avoid_: API client, service account, integration, app

**Integration**:
A connector built by Signal9 for a specific external product, such as a ticketing or billing system, that brings RMM-specific behaviour like quantity syncs or alert tickets. Enabled at a tenant and acts for that tenant and its descendants.
_Avoid_: Connector, plugin, API access

## Machines

**Device**:
A managed machine. Owns its telemetry and command history across reinstalls of the agent.
_Avoid_: Computer, endpoint, machine, asset

**Device status**:
Whether a device is Online or Offline, judged by its active agent checking in. A device with no active agent is Offline.
_Avoid_: Connectivity, health, state

**Maintenance mode**:
A toggle on a device marking planned downtime, during which it raises no alerts. Belongs to the device, so it survives agent reinstalls.
_Avoid_: Maintenance status, snooze

**Device transfer**:
Moving a device to a different tenant. Its agent, telemetry and command history go with it.
_Avoid_: Move, re-assign

**Agent**:
One enrolled installation of the Signal9 agent software on a device, holding its own credential. A device has at most one active agent.
_Avoid_: Client, sensor

**Identification**:
Deciding which device a newly enrolled agent belongs to: an existing device, or a new one. Ambiguous cases wait for a person to decide.
_Avoid_: Matching, pairing, onboarding

**Agent package**:
A signed build of the agent software for one operating system and install format, published by OSI with each release. One generic package serves every deployment.
_Avoid_: Installer build, MSI (unless you mean that format), binary

**Install parameters**:
The deployment address and enrollment token given to an agent package at install time, by command line, prompt or managed configuration.
_Avoid_: Install config, setup keys

**Enrollment token**:
A revocable secret, issued for one tenant, that lets new agents enroll into it. Reusable and non-expiring unless it is given a use limit or an expiry.
_Avoid_: Tenant code, install key, registration key

**Agent status**:
Whether an agent may receive commands: Active, or the first that applies of Revoked, Awaiting approval and Update required, all of which block commands.
_Avoid_: Command block, health, agent state

**Uninstall PIN**:
An optional secret set on a tenant, and inherited by its descendants, that must be supplied to remove the agent from a device.
_Avoid_: Uninstall password, tamper code

**Agent approval**:
A person confirming that a newly enrolled agent may receive commands. Needed only where the agent's tenant, or one of its ancestors, holds new agents for approval.
_Avoid_: Quarantine, pending, authorization
