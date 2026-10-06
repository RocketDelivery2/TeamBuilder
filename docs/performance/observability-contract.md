# Performance observability contract

This is a future collection contract, not a production instrumentation change. OpenTelemetry and Application Insights are not introduced by the simulation foundation.

| Surface | Signals to collect |
|---|---|
| API | requests/second, p50/p95/p99, 4xx/5xx, active requests, timeouts |
| .NET runtime | CPU, working set, GC pause, Gen 0/1/2 counts, allocation rate, thread-pool queue, thread count |
| SQL Server | CPU, active connections, connection-pool saturation, query duration, logical reads where available, waits, deadlocks, timeouts, lock waits |
| Background workers | recurrence materialization throughput/failures, horizon lag, queue lag only if queues are later introduced |
| Product | vacancy-detection latency, time-to-fill, roster-ready transition latency |

Measurements should carry environment, build, scenario, seed, duration, load profile, and database SKU/version. Keep projected simulation outputs explicitly labeled `Projected`; measured telemetry must identify its source. Do not treat EF InMemory measurements as SQL Server behavior.
