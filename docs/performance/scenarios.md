# Simulation scenarios

Scenario definitions are versioned and deterministic; `Seed` controls the generated distribution.

| Scenario | Purpose |
|---|---|
| `Tuesday5PM` | Regional after-work discovery, event browsing, hosts, joins, and departures |
| `SaturdayPickupPeak` | Distributed local sports discovery and short-lived demand |
| `WorldFirstLaunch` | Read-heavy gaming launch and role-hotspot claim intent |
| `MassDeparture` | Compressed departure and replacement-demand fan-out |
| `NotificationBurst` | Potential notification recipients and follow-up workload; sends nothing |
| `OneSpot100KClaimants` | 100,000 abstract intents targeting one scarce opening |
| `FiftyMillionPopulation` | 50 million logical users represented without per-user allocation |
| `ManyTeamsDistributedLoad` | Future distributed discovery mix |
| `OneSpotContention` | Future abstract contention scenario |
| `RecurringEventDiscovery` | Future recurring-event browse demand |
| `JoinLeaveRefillCycles` | Future join/leave/refill demand |
| `WorldFirstRoleHotspot` | Future role-weighted gaming hotspot |

## OneSpot100KClaimants contract

This scenario emits workload intent only; it does not call a claim route or simulate persistence. The target future invariant is **remaining capacity = 1, N simultaneous valid claimants, exactly 1 winner, every other outcome a clean business rejection, zero over-capacity**. The current application does not expose a reservation/claim endpoint, so there is no correctness result or winner count in this projection.

`NotificationBurst` reports a planning fan-out estimate using five recipients per notification candidate; it does not send messages or predict actual provider delivery.

## Population is not concurrency

`--logical-users` is the represented population; `--concurrent-users` is a distinct workload parameter. A 50M logical population does not imply 50M simultaneous clients, OS threads, tasks, or HTTP connections.
