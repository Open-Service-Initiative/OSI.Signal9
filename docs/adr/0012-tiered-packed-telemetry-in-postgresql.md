# Telemetry is tiered and packed per device-day inside PostgreSQL

Agents send one record a minute with the minimum, average and maximum of each metric. Signal9 keeps one-minute data for 14 days, hourly rollups (average, minimum, maximum and sample count) for 13 months and daily rollups for 5 years, all in PostgreSQL (ADR-0011). One-minute and hourly data are packed into one `bytea` row per device per day with our own codec: fixed-point integers, a per-metric delta, zigzag varints, then .NET's built-in Brotli. Daily rollups and each device's latest values are plain rows. New data lands in plain rows partitioned by day, and one nightly job in `api` packs the previous day, computes rollups and drops expired partitions.

This is the shape RMM vendors use. Those whose backends are public run plain relational databases, store data every 15 to 60 minutes (Tactical's 2 minutes is the finest found), keep full detail for days, keep rollups for months or years, and expire data by dropping partitions. Our tiers still give finer data than any of them.

Measured on synthetic data, packing takes 28.6 bytes per device-minute against 305 for a plain row and 108 for PostgreSQL's own compressed arrays. At 100k devices the database holds about 270 GB, roughly $60 a month of disk. Keeping 90 days of one-minute data the old way would have taken about 2.2 TB.

## Considered Options

- **One-minute data for 90 days in PostgreSQL.** Rejected: 15–60× finer than any competitor stores, and about $470 a month of disk at 100k devices.
- **Days 15–90 of one-minute data as one blob per device-day in Blob Storage.** Deferred, not rejected: about $80 a month at 100k devices, and the bytes are identical to the packed rows, so it can be added behind the same store if customers ask for longer minute data.
- **Parquet as the storage format, or a lake queried by DuckDB.** Rejected: Parquet is about 40% larger than our codec on realistic data, needs a second encoder with a native dependency, and costs two round trips per point read. DuckDB.NET has one maintainer, and its engine would need a new host. An optional Parquet export of rollups for BI tools may come later.
- **PostgreSQL's compressed arrays (lz4) instead of our own codec.** Rejected: about 4× the disk, because PostgreSQL only compresses values over about 2 KB and lz4 can't exploit slowly changing series.
- **Packing daily rollups too.** Rejected: queries across devices (fleet views, reports) would have to decode every device's data, which pushes a summary table into the design. Plain daily rows cost about 57 GB at 100k devices.

## Consequences

- **Every query across devices reads plain rows:** latest values, alerts, the ~48-hour landing table and daily rollups. Packed data is only read for one device's chart, decoded in `api`.
- **The codec is ours.** It needs a written format spec, a version byte and golden-file tests. A new metric needs a new format version.
- **Precision is fixed at ingest:** 0.01% for percentages, MiB for sizes, KiB/s for throughput and 0.1 ms for latency.
- **Alerts never depend on stored telemetry.** Monitors are evaluated on the agent.
- **Erasing a device's telemetry is a plain delete,** because every row belongs to one device.
- **Ingest drives write volume (about 40–50 GB of WAL a day at 100k devices)**, which affects backup cost on small disks and must be measured.
