# Private QA release performance smoke

A pre-release sanity check for a 10–20 tester QA, run on one development machine. It shows the
release build behaves correctly under bursts much larger than QA will see. It is **not** a
capacity figure or an SLA: the numbers below describe this machine on this day and should only be
compared with later runs of the same commands on similar hardware.

## Machine

| | |
|---|---|
| Date | 2026-10-09 |
| Commit | branch `ai/claude/private-qa-deployment-readiness-44beel` (based on main `7516648`) |
| Host | Linux container, 4 vCPU (Intel Xeon @ 2.10 GHz), 15 GiB RAM, no GPU |
| Database | SQL Server 2022 in Docker on the same host |
| API | Release `teambuilder-api` image (LocalQA environment) for discovery; the in-process test host for the SQL tests |

Everything (load generator, API, SQL Server) shared the same 4 vCPU, so the latencies include
contention a real deployment would not have.

## Results

### Last-slot contention (100 and 200 claimants)

`FinalSpot_ManyClaimants_ExactlyOneWins_AndNoneOverbooks`: a 10-person game at 9/10, then N
players press **Join** at the same moment through the HTTP API against SQL Server.

| Claimants | Outcome | Race wall clock |
|---:|---|---:|
| 2 | 1 × 201, 1 × 409 `RequirementFull` | 31 ms |
| 20 | 1 × 201, 19 × 409 | 162 ms |
| 100 | 1 × 201, 99 × 409 | 545 ms |
| 200 | 1 × 201, 199 × 409 | 947 ms |

Exactly one winner every time, roster 10/10, no overbooking.

### Discovery (200 requests/second)

`scripts/qa/discovery-load.mjs` against the release API container: 50 seeded games near one
point, open-loop requests at a fixed rate for 30 s, each search returning a full page of 20
results. The discovery rate limit was raised for the run
(`RateLimiting__Discovery__PermitLimit=100000`); the shipped limit stays in place.

| Target rate | Requests | Achieved | Status | p50 | p95 | p99 | max |
|---:|---:|---:|---|---:|---:|---:|---:|
| 200/s | 6,000 | 200/s | 6,000 × 200 | 7.8 ms | 14.9 ms | 49.1 ms | 231.7 ms |
| 400/s | 12,000 | 399.9/s | 12,000 × 200 | 15.3 ms | 32.7 ms | 45.9 ms | 109.7 ms |

API container memory after both runs: 165 MiB. An earlier 200/s run made while the full .NET test
suite was also running on the machine gave p50 13 ms, p95 83 ms, p99 506 ms, max 1.09 s, still
with no errors.

### 1,000 push subscribers (fake gateway)

`Fanout_NotifiesAndPushesEverySubscriber`: a player leaves a full game with N subscribed devices;
the outbox worker creates the notifications and push ledger, then the push dispatcher prepares
and sends every push to a mocked gateway (no real browser endpoints).

| Subscribers | Leave commit | Outbox processing | Push dispatch | Per device |
|---:|---:|---:|---:|---:|
| 1 | 30 ms | 90 ms | 65 ms | 65 ms |
| 10 | 33 ms | 138 ms | 92 ms | 9.2 ms |
| 100 | 32 ms | 244 ms | 141 ms | 1.4 ms |
| 1,000 | 35 ms | 1,426 ms | 978 ms (10 batches) | 0.98 ms |

The leave never waits on fanout (its commit time is flat), and every subscriber gets exactly one
notification and one push.

### Multiple workers

`ReplayedOutboxMessages_AndConcurrentDispatchers_NeverDuplicateNotificationsOrPushes`: two
outbox processors on the same message, replayed twice, plus concurrent push dispatchers. Result:
6 notifications and 12 deliveries (6 subscribers × 2 devices), no duplicates. Passed in 3 s.

## Reproduce

```bash
# SQL Server tests (Docker required)
dotnet test tests/TeamBuilder.Tests --filter "FullyQualifiedName~FinalSpot_ManyClaimants|FullyQualifiedName~Fanout_NotifiesAndPushesEverySubscriber|FullyQualifiedName~ConcurrentDispatchers" \
  --logger "console;verbosity=detailed"

# Discovery: local QA stack with the discovery limit raised for the test only
printf 'services:\n  api:\n    environment:\n      RateLimiting__Discovery__PermitLimit: "100000"\n' > perf-override.yml
docker compose -f docker-compose.qa.yml -f perf-override.yml up -d --build --wait
TB_API_URL=http://localhost:5080 TEAMBUILDER_JWT_SIGNING_KEY=<local QA key> \
  TB_LOAD_RPS=200 TB_LOAD_SECONDS=30 node scripts/qa/discovery-load.mjs
```

Never run the discovery load against Production, and do not raise the discovery limit on a
shared QA deployment for longer than the run.

## Not measured

* Real Web Push providers (FCM, Apple, Mozilla) and their latency or throttling.
* A deployed environment (network hops, TLS, managed SQL tiers such as Azure SQL S0).
* Sustained load or soak; every run above is seconds long.
