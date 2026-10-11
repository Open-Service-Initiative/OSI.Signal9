# Tenants form a tree of any depth, and access flows only downward

Tenants nest to any depth (capped by configuration, 20 by default). Access granted at a tenant covers that tenant and all its descendants, and never reaches up or sideways. This lets one deployment model anything from a single enterprise to a consultant overseeing several MSPs. It replaces the earlier rule that tenants could only nest one level deep. Every record is stored once, against its owning tenant, and ancestors see it through subtree queries.

## Considered Options

- **Parent pointer only, with recursive queries.** Rejected. LINQ can't express the recursion, the database re-walks the tree on every query, and SQL Server estimates recursive queries poorly.
- **Materialized path, `hierarchyid` or nested sets.** Rejected. A materialized path is an unenforced string that gets close to SQL Server's index-key limit at depth 20. `hierarchyid` is SQL Server-only and enforces no tree structure. Nested sets corrupt easily and are expensive to move.
- **Caching each caller's visible tenant IDs in the app.** Rejected. A stale cache becomes an authorization bug, and large MSPs push the ID list into EF Core's fragile fallback near SQL Server's 2,100-parameter limit.

## Consequences

Each tenant's parent pointer is the source of truth. A closure table (ancestor, descendant, depth) is derived from it and is only ever written by a single tree service, in the same transaction. That service runs one change at a time and checks for cycles and the depth cap. The central tenant filter is keyed on the caller's few assigned tenants, not on every tenant they can see. A test rebuilds the closure table from the parent pointers to catch drift.
