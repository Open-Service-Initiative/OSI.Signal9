# Telemetry is tiered and packed per device-day inside PostgreSQL

Agents send one record a minute with the minimum, average and maximum of each metric. Signal9 keeps one-minute data for 14 days, hourly rollups (average, minimum, maximum and sample count) for 13 months and daily rollups for 5 years, all in PostgreSQL (ADR-0011). One-minute and hourly data are packed into one `bytea` row per device per day with our own codec: fixed-point integers, a per-metric delta, zigzag varints, then .NET's built-in Brotli. Daily rollups and each device's latest values are plain rows. New data lands in plain rows partitioned by day. One nightly job in `api` seals each day once its late-upload window has closed: it packs the day, computes rollups and drops expired partitions through narrow functions the migration service installs, because `api` has no schema rights.

This follows what RMM vendors with published details do. They run plain relational databases, keep full detail for days and rollups for months or years, and expire data by dropping partitions. Where their stored resolution is documented it is 15 to 60 minutes for most (Tactical's 2-minute checks are the finest default found), though some allow shorter check intervals, such as N-central's 1-minute CPU scans. Our tiers store finer data than any documented default.

Measured on synthetic data, packing takes 28.6 bytes per device-minute, against 305 for a plain row and 108 for PostgreSQL's own lz4-compressed `real[]` arrays. Telemetry alone comes to about 3.1 MB per device (51 values a minute, a 3-day landing window), about 310 GB at 100k devices, roughly $60 a month of Premium SSD. Alerts, execution records, the audit log, WAL and backups are budgeted separately.

## Considered Options

- **One-minute data for 90 days, using the same codec.** Rejected: about $118 a month of disk at 100k devices (Premium SSD, 1 TiB), finer and longer than any competitor's documented default, and disk on Flexible Server can't shrink once grown. (The earlier ~$470 figure came from an older layout of lz4-compressed float arrays per device-hour, not from the 90-day choice alone.)
- **Days 15–90 of one-minute data as one blob per device-day in Blob Storage.** Deferred, not rejected: about $80 a month at 100k devices, and the bytes are identical to the packed rows, so it can be added behind the same store if customers ask for longer minute data.
- **Parquet as the storage format, or a lake queried by DuckDB.** Rejected: Parquet measured about 40% larger than our codec on the realistic data, needs a second encoder with a native dependency, and costs two round trips per point read. DuckDB.NET has one maintainer, and its engine would need a new host. An optional Parquet export of rollups for BI tools may come later.
- **PostgreSQL's compressed arrays instead of our own codec.** Rejected: measured at about 4× the size. Delta and varint encoding exploits slowly changing series in a way general-purpose compression of raw floats doesn't.
- **Packing daily rollups too.** Rejected: queries across devices (fleet views, reports) would have to decode every device's data, which pushes a summary table into the design. Plain daily rows cost about 57 GB at 100k devices.

## Consequences

- **Every query across devices reads plain rows:** latest values, alerts, the landing table and daily rollups. Packed data is only read for one device at a time (charts and single-device reports), decoded in `api`.
- **Days are sealed two days later:** day D is packed at 02:00 UTC on D+2, after its last minute's 24-hour late window. Reads merge sealed packed rows with unsealed landing rows.
- **The codec is ours.** It needs a written format spec (including each metric's aggregate and sample-count weighting), a version byte and golden-file tests. A new metric needs a new format version.
- **Precision is fixed at ingest:** 0.01% for percentages, MiB for sizes, KiB/s for throughput and 0.1 ms for latency.
- **Alerts never depend on stored telemetry.** Monitors are evaluated on the agent, except those the server evaluates (Offline, Hardware change).
- **Erasing a device's telemetry is a plain delete,** because every row belongs to one device.
- **Ingest drives write volume (about 40–50 GB of WAL a day at 100k devices)**, which affects backup cost on small disks and must be measured.
