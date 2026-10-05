# Rapid-Refill Platform Architecture

**Status:** Product and architecture direction; not a description of current
runtime capabilities or an API contract.

## Product outcome

Optimize for **time to restore a complete roster**. A person looking for a
basketball game Tuesday at 5 PM should be able to discover nearby games, see
which participant roles are available, and claim a suitable opening. If a
confirmed participant leaves, is removed, or does not arrive, the opening
should become visible promptly and matching should begin without waiting for a
lead to rebuild the roster manually.

The same model must support pickup sports, organized amateur sports,
professional-style roster feeds, esports groups, clubs, recurring leagues,
one-time events, and ad-hoc local games. Do not make the domain model specific
to basketball or to a single provider's roster format.

## Domain boundaries

### Persistent team membership and event participation

Keep two different relationships:

- **TeamMembership** represents ongoing membership in an organization or
  team. It does not imply that the person is participating in any particular
  event.
- **EventOccurrence roster** represents people actually expected to play or
  participate in one occurrence. A person may be a team member without being
  assigned to an occurrence, and an occurrence may admit guests who are not
  persistent team members.

An event roster is described using:

- **RosterRequirement**: the number and kinds of participants required by an
  occurrence, such as ten participants with five guards and five forwards, or
  a single generic participant requirement.
- **RosterAssignment**: a participant's relationship to an occurrence and,
  when applicable, to a requirement and activity-specific role.
- **OpenSpot**: derived or persisted unfilled capacity for a requirement. It
  represents quantity, not a pre-numbered jersey, chair, or database slot.
- **SpotReservation**: a time-limited claim on capacity while a candidate
  responds to an offer.
- **ReplacementOffer**: a targeted or broadcast invitation to fill an open
  spot; it refers to the opening and carries its own delivery/response state.

For example, a five-on-five basketball occurrence can require ten participants
even when its persistent team has twenty members. Requirements can express
role counts or an untyped participant total; neither membership count nor
`Team.OwnerId` determines event capacity.

For the current capacity-accounting phase, active `TeamMember` rows remain
authoritative for persistent team membership. `Team.OwnerId` is
administrative; the owner consumes capacity only when represented by an
active membership row. No `TeamVacancy` or `JoinAsMember` concept is implied
by this design. Event-roster capacity is a separate future domain concern.

## Rapid-refill lifecycle

An occurrence's assignment and opening state should follow an explicit
transition sequence:

1. A participant holds a confirmed `RosterAssignment`.
2. The participant leaves, is removed by an authorized roster manager, or
   fails the event's no-show/check-in rule.
3. The assignment transitions out of capacity-consuming states. The system
   records the reason and time; the assignment is retained for audit rather
   than silently erased.
4. The occurrence recalculates its requirements and exposes an `OpenSpot`
   for the newly unfilled quantity.
5. Matching selects candidates using eligibility, activity/role, availability,
   location, timing, and event/team rules.
6. The system creates `ReplacementOffer` records and/or exposes the opening in
   discovery results. Notification is an optimization; the opening remains
   discoverable if a notification is delayed or fails.
7. A candidate atomically reserves capacity. The reservation has an explicit
   expiry and is released on decline, expiry, or cancellation.
8. The candidate confirms or checks in according to the event's policy. The
   system converts the reservation into a capacity-consuming assignment.
9. The opening closes when the requirement is satisfied; the occurrence can
   report its roster as ready/full.

Use explicit states and auditable timestamps for offers, reservations, and
assignments. No-show and cancellation policy should be configurable by the
organizer/event rather than inferred from a universal timer.

### Capacity and concurrency

Only one candidate may win the final available capacity. The write path must
enforce this in the database transaction, not by trusting a previously
displayed search result or an in-memory count:

- Re-read/conditionally update the occurrence requirement or capacity row
  inside a transaction.
- Enforce a unique active assignment/reservation rule for the relevant
  candidate and occurrence where required.
- Use a database constraint or atomic conditional update so concurrent
  reservations cannot make `reserved + confirmed` exceed required capacity.
- Give reservations a unique identity and expiry. Expiry processing must
  conditionally release only the reservation that is still current.
- Make offer acceptance and confirmation idempotent so retries cannot create
  duplicate assignments.

SQL Server transactions and optimistic concurrency are appropriate in the
current modular service. Concurrency behavior must be exercised with
SQL Server-backed tests; an in-memory provider cannot prove rowversion or
unique-index behavior.

## Team lead and roster-manager capabilities

The future authorization model should grant explicit capabilities to a team
lead or delegated roster manager, such as:

- add or remove a participant;
- invite a participant;
- open or close recruitment;
- request a replacement;
- approve or deny applicants;
- manage a recurring schedule; and
- manage an event lineup.

Model these as scoped capabilities/assignments and enforce them at the
application boundary. Do not infer new runtime permissions from the existing
`TeamRole` enum values, and do not change current permissions as part of this
design.

## Local discovery and geo search

Store event venues with canonical WGS 84 latitude/longitude and an IANA
timezone identifier. Retain the venue's human-readable name/address separately
from coordinates. A remote or private event may omit a public street address;
precise location disclosure is a product/privacy policy, not a search
requirement.

Offer common radius presets of **15, 25, and 50 miles** while allowing the
search API to accept arbitrary future radius values. Apply server-side bounds,
rate limits, and validation rather than hard-coding only those three choices.
For each search, support:

- activity/category and activity-specific role;
- a date/time window;
- distance from a point or approved location context;
- open participant capacity and requirement/role;
- event, occurrence, team, and organization properties; and
- sorting by closest to farthest, with stable secondary ordering.

Use a spatial query strategy suitable for SQL Server (for example its
`geography` type and spatial index) as the first implementation. Keep the
search contract independent of a particular storage engine so a dedicated
geo/search service or index can be introduced later. Define the distance
metric and boundary behavior consistently; radius filtering and sorting must
use the same canonical coordinates and units.

Do not expose a participant's home location as a side effect of proximity
matching. Candidate matching should use consented/coarse location or an
explicit search origin, and public results should reveal only the venue
precision the organizer is allowed to publish.

## Recurring events and occurrence overrides

Represent a schedule as an **EventSeries** and its dated instances as
**EventOccurrence** records. A series contains:

- a local start date and wall-clock time;
- an IANA timezone;
- an RRULE-compatible recurrence rule (RFC 5545 `RRULE` value);
- a local end date or another explicit series boundary; and
- duration and default event/roster properties.

For example, Tuesday at 5 PM between two dates is a weekly rule with
`FREQ=WEEKLY;BYDAY=TU`, interpreted from the series' local start date/time in
its timezone. Do not express a recurring wall-clock schedule as a fixed UTC
offset: UTC offset changes with daylight-saving rules.

Materialized occurrences carry their own resolved UTC start/end instants,
timezone, venue, requirements, and status. A lead may edit or cancel one
occurrence without altering the series. Store an explicit occurrence
override/cancellation or exception date so regeneration does not resurrect a
cancelled instance. Series edits should state whether they affect future
occurrences only, and must preserve already completed or individually
overridden occurrences.

Define and test behavior for nonexistent local times during spring-forward and
ambiguous local times during fall-back. Retain the series timezone and local
wall-clock intent alongside resolved UTC instants. Use a maintained timezone
database and a documented policy; do not silently shift recurrence times.

**Implemented (TB-EVENTS-002):** series creation, reads and logical
cancellation at `/api/v1/event-series`
([`docs/api.md`](../api.md#event-series--apiv1event-series)). The RRULE
parser accepts a strict V0.1 subset (`FREQ=DAILY|WEEKLY`, `INTERVAL` 1..52,
`BYDAY` weekdays) and the series end date bounds the recurrence. Time zones
are IANA ids validated with built-in .NET APIs. A spring-forward gap moves the
occurrence to the first valid local instant after the gap; a fall-back overlap
uses standard time. Creation materializes 21 local days from the series start
in the same transaction, through a reusable materializer, with
`UX_Events_SeriesId_ScheduledStartUtc` as the idempotency boundary. Not yet
implemented: a rolling-horizon worker, series editing/regeneration, exception
dates and per-occurrence overrides, the Venue API and geo discovery.

## Activity and position taxonomy

Roles belong to an activity taxonomy, not a global fixed enum:

| Activity | Example role codes |
|---|---|
| Basketball | `guard`, `forward`, `center` |
| MMORPG raid | `tank`, `healer`, `damage` |
| Generic pickup game | `participant` |

Store stable canonical role codes with display labels and taxonomy version.
Adapters may map source-specific labels to canonical codes, but retain the
original label and source mapping for traceability. A role is optional when
the activity or event only needs a participant count.

## Reputation and reliability

Use an event-driven reliability model, not an opaque single social-credit
number. Append immutable, attributable signals such as attendance, on-time
check-in, no-show, late cancellation, timely cancellation, responsible exit,
prompt replacement request, successful handoff, lead removal, and completed
event.

Signals need event/occurrence context, source, recorded time, and a correction
or supersession path; do not rewrite history when an event is disputed.
Later product views may derive transparent components with published
definitions and time windows. Reward timely notice and responsible handoff,
not the act of leaving itself. Explain effects and provide a way to contest
incorrect records. Do not use a reliability signal as a proxy for protected
characteristics or as the sole basis for a high-impact decision.

## Interoperability and ingestion

Use **TeamBuilder Roster Language (TBRL)** as a versioned, provider-neutral
JSON interchange contract. The draft v0.1 architecture, schema, and example
are in [`tbrl-v0.1.md`](tbrl-v0.1.md) and
[`../schemas/tbrl-0.1.schema.json`](../schemas/tbrl-0.1.schema.json).

The intended pipeline is:

```text
Source Adapter
  -> Parse
  -> TBRL
  -> Schema Validation
  -> Normalization
  -> Entity Resolution / De-duplication
  -> TeamBuilder Ingestion API
  -> canonical persistence
  -> domain events
  -> search/refill projections
```

Every import must be attributable to a source namespace and carry source
timestamps and licensing/provenance metadata. Use a stable idempotency key
scoped to the source and source record/version. Replaying the same input must
not duplicate teams, people, occurrences, assignments, or reliability
signals. Keep external identifiers namespaced by provider/source; TeamBuilder
database GUIDs are neither required in TBRL nor a substitute for source IDs.

Entity resolution should use approved identifiers and explicit confidence
rules. Ambiguous people or team matches should be quarantined for review rather
than merged on display-name similarity alone. Never assume authorization to
scrape a public website. Professional roster feeds must retain source,
attribution, license/terms, and permitted-use metadata through ingestion and
downstream projections.

## Evolution and scale

Start with today's **modular .NET service and SQL Server**. Keep domain
boundaries and module contracts explicit without prematurely splitting the
repository or deployment into microservices:

| Future boundary | Primary responsibility |
|---|---|
| Identity/Profile | External identity links, player profiles, consent |
| Team/Roster | Organizations, persistent teams, memberships, roster managers |
| Event/Scheduling | Event series, occurrences, venues, requirements |
| Geo/Search | Spatial discovery and indexed search projections |
| Refill/Matching | Open spots, eligibility, offers, reservations, handoff |
| Import/Interoperability | Source adapters, TBRL validation, provenance |
| Notification | Offer delivery and reminder channels |
| Reputation | Immutable reliability signals and transparent derived views |

Use a **transactional outbox from the beginning** for events that must leave
the owning SQL transaction, including roster changes, opened spots, accepted
offers, and import completion. Write the domain change and outbox message in
one database transaction. Dispatch asynchronously with at-least-once delivery;
consumers must be idempotent and use event IDs/aggregate versions to reject
duplicates or stale projections.

Initially, search/refill projections may be updated by background workers
within the same deployable service and SQL Server. Keep projections rebuildable
from canonical data and event history. Add a distributed cache or event bus
only when measured load, isolation, or availability requirements justify it;
neither cache nor search index becomes the authority for final capacity
reservation. Later extraction of a module must preserve its ownership rules,
outbox contract, idempotency, and transactional capacity enforcement.

## Phasing guardrails

1. Preserve current identity resolution and resource authorization; future
   lead capabilities are not a grant of current `TeamRole` permissions.
2. Keep persistent membership capacity distinct from an occurrence's
   participant requirements.
3. Make reservation correctness a database-enforced invariant before
   optimizing matching or notifications.
4. Add spatial search and timezone-aware recurrence with explicit contracts
   and provider-neutral tests.
5. Introduce TBRL ingestion only with schema validation, provenance,
   licensing, idempotency, and a review path for ambiguous entities.
6. Keep the modular monolith until an independently deployable boundary has a
   concrete operational need.
