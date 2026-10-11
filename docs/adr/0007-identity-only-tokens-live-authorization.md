# Access tokens carry identity only, and authorization is evaluated live

Tokens carry the subject, client and granted scopes, but never roles or tenant access. Each request is checked against the token's scopes, the caller's role at the target tenant and the tenant subtree, with only a short cache. That way, removing a role or transferring a tenant takes effect immediately, not when a token expires. With arbitrarily deep trees and many role assignments, the access claims would also make tokens too large. The one exception is Superadministrator (ADR-0006).
