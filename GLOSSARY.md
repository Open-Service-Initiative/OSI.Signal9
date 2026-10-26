# Signal9

Remote monitoring and management of machines, organized under a hierarchy of tenants that can model anything from a single enterprise to a consultant overseeing several MSPs.

## Deployment

**Deployment**:
One running installation of Signal9, with its own tenant trees, users and keys.
_Avoid_: Installation, environment

**Operator**:
The organization that runs a deployment and owns its infrastructure and Entra app registration. An organization, unlike a Superadministrator, who is a person; an operator's own MSP is usually also a tenant in its deployment.
_Avoid_: Host, provider, vendor

**Key-encryption key**:
The one key that wraps every key a deployment stores: its token signing and encryption keys and its Data Protection keys. A Key Vault key on Azure, a certificate otherwise. The deployment's single root of trust.
_Avoid_: KEK (in prose), master key, root key

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
A person known to Signal9, belonging to exactly one tenant (their owning tenant), whether or not they can sign in yet. Being a user grants no access by itself.
_Avoid_: Account, member, login, end user and contact (as separate kinds of person)

**Owning tenant**:
The tenant an object (a user, device, script, schedule and so on) directly belongs to. Whoever controls it controls the object: deleting the tenant deletes what it holds, including users along with all their role assignments. For a user it grants no access; that comes only from role assignments.
_Synonym_: home tenant
_Avoid_: Organisation (as a synonym)

**User transfer**:
Moving a user to a different owning tenant. Their role assignments, and so their access, stay as they are.
_Avoid_: Move, re-home

**Role**:
A named set of things its holder may do, granted to a user only through a role assignment. Signal9 ships built-in roles that can't be changed.
_Avoid_: Permission set, group, profile

**Role assignment**:
A grant of one role to one user at one tenant (the assigned tenant), covering that tenant and its descendants. The only source of a user's access, apart from being a Superadministrator.
_Avoid_: Permission, membership, grant, home tenant (that means owning tenant; here, say "assigned tenant")

**Session**:
A person's use of one client from one sign-in through auth, from one Entra session (`sid`). Every token the client gets for it belongs to it, so ending a session (sign-out, Entra's logout, Entra refusing the person, or losing their last role assignment) revokes them all.
_Avoid_: Login, authorization (OpenIddict's record behind a session), consent (which outlives sessions)

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
A managed machine. Owns its telemetry and command history across reinstalls of the agent. Deleting a device erases everything identifying about it, but leaves an anonymous record so its alerts and history still resolve.
_Avoid_: Computer, endpoint, machine, asset

**Hardware inventory**:
A device's current hardware, component by component: system, motherboard, CPUs, memory modules, disks, network adapters, GPUs, monitors, TPM and battery, with models, serials and MACs. Only the current state is kept.
_Avoid_: Asset record, audit, specs

**Hardware change**:
A recorded difference between a device's previous and new hardware inventory, such as a memory module removed or a disk serial changed. Kept for a limited time.
_Avoid_: Inventory history, asset change

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

**Agent instance**:
One independent copy of the agent running on a machine, for one deployment and one root tenant tree, with its own key, certificate, configuration and state. A machine can run several side by side; each one that enrolls is a separate agent.
_Avoid_: Side-by-side agent, second agent

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
Whether an agent may receive commands: Active, or the first that applies of Revoked, Needs decision, Awaiting approval and Update required, all of which block commands. An agent that needs a decision is waiting for Identification and has no device yet.
_Avoid_: Command block, health, agent state

**Uninstall PIN**:
An optional secret set on a tenant, and inherited by its descendants, that must be supplied to remove the agent from a device.
_Avoid_: Uninstall password, tamper code

**Agent approval**:
A person confirming that a newly enrolled agent may receive commands. Needed only where the agent's tenant, or one of its ancestors, holds new agents for approval.
_Avoid_: Quarantine, pending, authorization

## Remote work

**Command**:
A built-in RMM action for one agent, such as rebooting, restarting a service, updating the agent or uninstalling it. Runs at most once, and ends in one of these statuses: Succeeded, Failed, Timed out, Cancelled, Expired (never claimed in time) or Abandoned (claimed, but no result came back).
_Avoid_: Action, task, job, instruction

**Script**:
Code saved in Signal9 at an owning tenant, in PowerShell or CMD, with its own default timeout.
_Avoid_: Snippet, automation, task

**Sharing**:
A script's setting for who may see and run it: people at its owning tenant only (the default), who may still run it on devices in descendants, or also people at descendant tenants, who may run but not edit it.
_Avoid_: Access scope (scopes are permissions), visibility

**Script execution**:
One run of a saved script or of ad-hoc code on one device, recording the exact content that ran. Follows the same lifecycle and statuses as a command.
_Avoid_: Script run, job, deployment

**Terminal session**:
An interactive shell on a device, audited with a full transcript. Like ad-hoc code, it is arbitrary execution and needs its own permission.
_Avoid_: Remote shell, console

## Monitoring

**Telemetry**:
A device's performance measurements over time, such as CPU, memory, disk and network. The agent samples them locally and sends one record a minute with each metric's minimum, average and maximum.
_Avoid_: Metrics (for the stored history), performance data, stats

**Rollup**:
Telemetry summarised over an hour or a day as each metric's average, minimum, maximum and sample count. Kept far longer than one-minute data.
_Avoid_: Aggregate, summary, downsample

**Monitor**:
A condition set at a tenant that applies to every device in it and its descendants, such as CPU above a threshold for some minutes or a service not running. A descendant can turn off a monitor it inherits. Evaluated by the agent, except Offline and Hardware change, which the server evaluates.
_Avoid_: Check, alert rule, threshold, policy

**Alert**:
A problem raised when a device meets a monitor's condition. A device has at most one open alert per monitor; it is Open, Acknowledged or Resolved, and its severity can rise and fall while it stays open.
_Avoid_: Incident, notification, ticket, event

**Severity**:
How serious an alert is: Critical, Warning or Informational. A monitor sets a threshold for each severity, so one alert moves between them as the condition changes.
_Avoid_: Priority, level

**Webhook**:
A destination set at a tenant, and inherited, that receives alert events as signed HTTP requests, in Generic, Teams or Slack format.
_Avoid_: Notification channel, hook, callback, integration (that is a Signal9 connector)

## Reporting

**Report**:
A fixed, named view of data for a tenant (with or without its descendants) or one device over a period, made of sections. A section the reader lacks permission for is shown as unavailable rather than failing the whole report.
_Avoid_: Dashboard, export, analytics

**Report schedule**:
A report sent by email on a recurring basis, owned either by a user, whose access it runs with, or by its tenant, so it survives people leaving.
_Avoid_: Subscription, scheduled report, recurring report
