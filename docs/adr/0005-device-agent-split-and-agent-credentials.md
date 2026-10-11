# Devices and agents are separate, and agents hold credentials issued by Signal9

A device is the managed machine and keeps its history. An agent is one enrolled installation on it, with its own credential, and a device has at most one active agent (modelled on ImmyBot). An agent enrolls with a tenant's enrollment token. It then holds a non-exportable TPM/DPAPI key and authenticates with mTLS, with no refresh tokens, and its identity always comes from its token. Agents are never matched to devices by machine name: they're identified by SMBIOS UUID, or manufacturer plus serial, within the enrollment token's subtree, and anything ambiguous waits for a person to decide.

## Consequences

Enrollment tokens follow the industry norm (NinjaOne, Datto, ImmyBot): multi-use and non-expiring by default, revocable and rotatable. Optional use limits, expiry and an inherited "hold for approval" setting are available. The main protection against a leaked token is visibility, not friction.
