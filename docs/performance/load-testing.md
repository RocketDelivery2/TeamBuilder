# HTTP load testing

The HTTP runner uses k6 only. The simulator predicts workload; k6 measures actual HTTP responses. Never report projected simulator latency as API latency.

## Safe initial coverage

The smoke script performs anonymous GETs only against implemented liveness, readiness, public team listing, and public event listing routes. It does not call event-series, claim, open-spot, geo, notification, or matching APIs. Authentication is not required; an optional `AUTH_TOKEN` may be supplied through the environment and is never persisted.

## Configuration and profiles

`BASE_URL` is required and must be an absolute HTTP(S) URL. Optional inputs are `SCENARIO`, `DURATION`, `VUS`, and `ARRIVAL_RATE`. `smoke` defaults to 1 VU for 10 seconds. `baseline` has conservative defaults. `stress`, `spike`, and `soak` require explicit `ALLOW_HEAVY=true`; no default invokes high load. There is no distributed runner in this foundation.

Example:

```powershell
$env:BASE_URL = 'http://localhost:5000'
k6 run tests/performance/k6/smoke.js
```

Do not point scripts at production without explicit operational authorization and a reviewed load budget. The simulator's 50M logical-user setting does not create HTTP clients.

## Measured outputs

k6 reports request count, throughput, latency percentiles, and failed requests. Add server-side business-conflict, timeout, deadlock, and capacity-invariant metrics only when the real API/measurement system exposes them. Do not invent claim endpoints or interpret a k6 smoke run as a SQL concurrency proof.
