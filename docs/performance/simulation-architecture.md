# Simulation architecture

The performance foundation has three distinct tools:

1. **Logical simulation** projects behavioral demand from aggregate population segments. It does not make HTTP calls or prove correctness.
2. **HTTP load generation** measures real endpoint behavior with k6. Its latency is measured, not projected.
3. **Dataset planning** calculates candidate row counts. Plans do not generate rows or connect to SQL Server.

## Logical population and concurrency

`LogicalUsers` describes the population represented by the model. `ConcurrentUsers` is a separate configured estimate of simultaneously active clients. For example, 50,000,000 logical users does not mean 50,000,000 live HTTP connections. The 50M scenario runs from aggregate counts and a bounded deterministic sample; it never allocates user/actor objects or tasks per logical user.

The current engine uses a seeded SplitMix64 stream to sample up to 10,000 actions and synthetic latency observations. It scales sampled action proportions into aggregate counts, and keeps a fixed-size histogram for percentiles. Runtime memory is bounded by the number of time buckets and histogram bins, not population or total projected actions. Histogram percentiles are approximate bin midpoints; simulated think-time and network-delay percentiles are not API p50/p95/p99.

## Core model

The foundation includes `SimulationScenario`, `PersonaArchetype`, `BehaviorWeights`, `SimulationRunConfiguration`, `SimulationEvent`, `SimulationEventType`, `SimulationSummary`, `BoundedHistogram`, and `DatasetScalePlan`. Scenario versions and deterministic run IDs make artifacts comparable. Synthetic IDs are deterministic hashes and contain no PII.

Every run specifies a scenario, seed, logical population, concurrent users, duration, and time scale. Equal configuration produces byte-stable summary and workload values. No wall clock is used for simulation decisions.

## Output artifacts

Runs emit three deterministic JSON files under `artifacts/performance/<run-id>/`:

- `summary.json`: aggregate action/request projections and synthetic latency distributions; `simulationKind` is always `Projected`.
- `scenario.json`: scenario definition and run configuration.
- `workload.json`: vendor-neutral time buckets with endpoint class, projected request arrivals, estimated concurrency, activity mix, and region mix.

Artifacts are ignored by Git. Endpoint classes distinguish implemented public reads/writes from abstract future action arrivals. A `FutureConceptual` workload bucket has no associated production route. No future claim/reservation route is fabricated.

Notification recipient and downstream-message counts use an explicit illustrative fan-out of five recipients per notification candidate. This is a planning assumption, not an observed delivery count.

## Scenario catalog

The implemented catalog covers Tuesday5PM, SaturdayPickupPeak, WorldFirstLaunch, MassDeparture, NotificationBurst, OneSpot100KClaimants, and FiftyMillionPopulation. Future-shaped scenarios—ManyTeamsDistributedLoad, OneSpotContention, RecurringEventDiscovery, JoinLeaveRefillCycles, and WorldFirstRoleHotspot—remain abstract workload definitions. Their presence does not imply production support.

OneSpot claim intent is a workload projection only. The target future invariant is: with remaining capacity equal to one and N simultaneous valid claimants, exactly one wins, every other outcome is a clean business rejection, and there are zero over-capacity commits. The current application does not expose a reservation/claim endpoint; the simulator does not test database concurrency or assert winners.

## Metrics contract

Summary fields prepare for request count, logical and concurrent users, projected throughput, synthetic p50/p95/p99, success/business-conflict/transport-or-server-error/timeout counts, deadlocks when a real measurement source provides them, exact winner count, capacity invariant violations, and scenario duration. Measurement-only counters and correctness results remain null in projected runs until a real endpoint or test supplies evidence.

Do not call projected metrics measured API performance. Do not infer SQL Server correctness from InMemory tests.

## Dataset planning and safety

The planner supports small, 100K, 1M, 10M, and 50M population plans for Players, Teams, TeamMemberships, EventSeries, EventOccurrences, Venues, and JoinRequests. Plans are output-only. 10M and 50M remain plan-only; there is no physical SQL generator or automatic bulk insert. RosterRequirements and RosterAssignments are future extension points, deliberately not coupled to in-progress roster domain work.

No cloud distributed execution, production instrumentation, microservice extraction, Redis, H3, queues, or distributed locks are introduced. Future boundaries such as discovery projections, reservation consistency, notifications, import, and analytics require evidence from measurement first.
