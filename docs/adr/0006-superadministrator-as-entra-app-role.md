# Superadministrator is an Entra app role, honoured only for the deploying directory

Deployment-wide authority comes from an app role on the operator's own Entra app registration, not from Signal9's own role assignments. Setup assigns it to the deployer, recovery happens through Entra, and PIM can manage it. Because the app registration is multi-tenant, any consenting directory could assign the same role. So the role counts only when the token's directory ID (`tid`) is the deploying directory. This is the one exception to authorization being evaluated live from Signal9's own data (ADR-0007).
