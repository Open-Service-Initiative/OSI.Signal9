# PostgreSQL holds all of Signal9's data

Signal9 moves from SQL Server to PostgreSQL 18 for everything it stores: tenants, identity, keys, commands, alerts, audit and telemetry. On Azure this is Azure Database for PostgreSQL Flexible Server, signed into with managed identity through Entra and reached over a private endpoint. Self-hosted, it is the official `postgres:18` container. EF Core uses the Npgsql provider. We use no extensions, so Azure and self-hosted behave identically.

SQL Server's editions were the deciding problem for self-hosters. Developer edition isn't licensed for production, Express caps a database at 50 GB, and Standard costs money. PostgreSQL has no editions, and on Azure its compute costs roughly a third of Azure SQL's per vCore. Data Protection, OpenIddict and the distributed token cache all have PostgreSQL stores.

## Considered Options

- **Stay on SQL Server and Azure SQL.** Rejected: self-hosters must pick between an unlicensed, a capped and a paid edition, and Azure SQL costs more for the same load.
- **MySQL.** Rejected: Pomelo, the most widely used provider, has no EF Core 10 release; Oracle's `MySql.EntityFrameworkCore` supports EF Core 10, but Aspire has no Azure MySQL integration, and Azure Database for MySQL brings no advantage over PostgreSQL here.
- **A separate time-series database (TimescaleDB, ClickHouse, Azure Data Explorer) for telemetry.** Rejected. Azure's TimescaleDB is the Apache-2 edition, without compression or continuous aggregates. ClickHouse can't run reliably on Container Apps, whose only persistent volumes are network file shares. Azure Data Explorer has no free self-hosted equivalent. RMM vendors whose backends are public all use a plain relational database (ADR-0012).
- **Azure HorizonDB.** Rejected for now: it is in preview.

## Consequences

- Only the migration service's identity is an Entra administrator of the server. It creates non-admin roles for the hosts (`pgaadauth_create_principal_with_oid`) and grants them data access, overriding Aspire's default of making every referencing app an administrator. Self-hosted, each host has its own PostgreSQL role.
- The AppHost chooses `AddAzurePostgresFlexibleServer` for azd and `AddPostgres` for Compose, because `RunAsContainer` does nothing at publish time.
- Storage on Flexible Server can grow but never shrink, so telemetry is sized deliberately (ADR-0012).
- The tenant tree's single-writer lock becomes `pg_advisory_xact_lock`. The test fixture uses a PostgreSQL container.
- ADR-0001's reasons still hold on PostgreSQL. `ltree` is rejected for the same reasons as materialized paths.
