# Capacity gates

All values below describe evidence required to advance an environment, not achieved capacity. No latency target is a required PR gate until representative measurement is established.

| Gate | Evidence required |
|---|---|
| Developer | Deterministic small simulation; low-rate anonymous smoke; no correctness regressions |
| QA | Repeatable workload and dataset plan; measured concurrency and saturation point; error/latency baseline; SQL bottleneck identified |
| Regional production | Representative regional mix and peak profile; documented p95/p99 and error rate; recovery and operational capacity evidence |
| National production | Multi-region demand projection validated against measured capacity; DB/pool limits and recovery behavior reviewed |
| Global production | Regional skew/hotspot and failure recovery evidence at validated scale; no extrapolation from logical population alone |

Every gate should record logical users, real concurrent clients, saturation point, latency/error rates, database bottlenecks, hotspot correctness, and recovery behavior. A projected 50M logical population is not 50M live connections.

## Proposed engineering budgets (not yet proven)

- Ordinary read-like endpoint: p95 ≤ 250 ms.
- Normal write: p95 ≤ 300 ms.
- 5xx rate: < 0.1% under a validated target load.
- Reservation correctness when that endpoint exists: zero duplicate winners and zero over-capacity commits.

These are proposals for measurement, not current SLO claims and not mandatory CI thresholds.
