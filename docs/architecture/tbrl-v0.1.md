# TeamBuilder Roster Language (TBRL) v0.1

**Status:** Draft interchange design. This is not a current API request shape,
database schema, or promise that a production ingestion endpoint exists.

## Purpose

TBRL is a versioned, provider-neutral JSON format for importing and exchanging
organizations, activities, teams, participants, schedules, venues, and event
rosters. It lets TeamBuilder accept amateur and professional feeds without
making a provider's database model or TeamBuilder's internal GUIDs part of the
contract.

The machine-readable JSON Schema is
[`../schemas/tbrl-0.1.schema.json`](../schemas/tbrl-0.1.schema.json). The
following compact example illustrates its intended shape; values are
illustrative and do not identify a real provider or feed.

```json
{
  "schemaVersion": "0.1",
  "exportedAt": "2026-09-20T18:00:00Z",
  "source": {
    "namespace": "league.example",
    "sourceId": "spring-league-feed",
    "sourceUpdatedAt": "2026-09-20T17:45:00Z",
    "license": {
      "identifier": "provider-permitted-use",
      "url": "https://league.example/terms",
      "attribution": "Example League",
      "permittedUses": ["team-management", "event-roster"]
    }
  },
  "organizations": [
    {
      "externalRef": {
        "namespace": "league.example",
        "id": "org-42"
      },
      "name": "Example Community League",
      "sourceUpdatedAt": "2026-09-20T17:45:00Z"
    }
  ],
  "activities": [
    {
      "code": "basketball",
      "name": "Basketball",
      "taxonomyVersion": "1",
      "roles": [
        {
          "code": "guard",
          "label": "Guard",
          "externalLabels": ["Point Guard", "Shooting Guard"]
        },
        {
          "code": "forward",
          "label": "Forward",
          "externalLabels": ["Small Forward", "Power Forward"]
        },
        {
          "code": "center",
          "label": "Center",
          "externalLabels": ["Centre"]
        }
      ]
    }
  ],
  "teams": [
    {
      "externalRef": {
        "namespace": "league.example",
        "id": "team-17"
      },
      "organizationRef": {
        "namespace": "league.example",
        "id": "org-42"
      },
      "activityCode": "basketball",
      "name": "Tuesday Owls",
      "sourceUpdatedAt": "2026-09-20T17:45:00Z"
    }
  ],
  "participants": [
    {
      "externalRef": {
        "namespace": "league.example",
        "id": "person-991"
      },
      "displayName": "Jordan R.",
      "activityCode": "basketball",
      "roleCodes": ["guard"],
      "availability": [
        {
          "status": "available",
          "from": "2026-09-22T21:00:00Z",
          "until": "2026-09-22T23:00:00Z"
        }
      ],
      "sourceUpdatedAt": "2026-09-20T17:45:00Z"
    }
  ],
  "eventSeries": [
    {
      "externalRef": {
        "namespace": "league.example",
        "id": "series-4"
      },
      "teamRef": {
        "namespace": "league.example",
        "id": "team-17"
      },
      "activityCode": "basketball",
      "name": "Tuesday Run",
      "timeZone": "America/Chicago",
      "localStartDate": "2026-09-22",
      "localStartTime": "17:00:00",
      "durationMinutes": 120,
      "rrule": "FREQ=WEEKLY;BYDAY=TU",
      "localEndDate": "2026-12-22",
      "sourceUpdatedAt": "2026-09-20T17:45:00Z"
    }
  ],
  "eventOccurrences": [
    {
      "externalRef": {
        "namespace": "league.example",
        "id": "game-2026-09-22"
      },
      "seriesRef": {
        "namespace": "league.example",
        "id": "series-4"
      },
      "teamRef": {
        "namespace": "league.example",
        "id": "team-17"
      },
      "activityCode": "basketball",
      "name": "Tuesday Run",
      "startsAt": "2026-09-22T22:00:00Z",
      "endsAt": "2026-09-23T00:00:00Z",
      "timeZone": "America/Chicago",
      "status": "scheduled",
      "venue": {
        "name": "North Gym",
        "address": "100 Example Avenue",
        "latitude": 41.8819,
        "longitude": -87.6278
      },
      "rosterRequirements": [
        {
          "code": "guards",
          "roleCode": "guard",
          "quantity": 2
        },
        {
          "code": "forwards",
          "roleCode": "forward",
          "quantity": 2
        },
        {
          "code": "centers",
          "roleCode": "center",
          "quantity": 1
        }
      ],
      "rosterAssignments": [
        {
          "participantRef": {
            "namespace": "league.example",
            "id": "person-991"
          },
          "roleCode": "guard",
          "status": "confirmed",
          "sourceUpdatedAt": "2026-09-20T17:45:00Z"
        }
      ],
      "sourceUpdatedAt": "2026-09-20T17:45:00Z"
    }
  ]
}
```

## Contract rules

### Version and compatibility

- `schemaVersion` identifies the TBRL contract version, not the exporting
  software version. A consumer must reject unsupported major versions rather
  than silently reinterpret them.
- v0.1 is a draft. Changes that alter required-field meaning or state
  semantics require a new version; compatible optional additions may be
  introduced under documented compatibility rules.
- Consumers validate against the versioned JSON Schema before normalization.
  Validation errors identify a JSON Pointer path and do not partially ingest
  a document.
- Unknown optional provider data should be preserved in adapter-side
  provenance where possible, but must not be treated as canonical TeamBuilder
  fields by accident.

### Identifiers and references

Every imported entity uses `externalRef` with both a `namespace` and an `id`.
The namespace identifies the system that owns the identifier; an ID by itself
is never globally unique. Namespace values should be stable provider/source
identifiers under the adapter's control, not user-entered display names.

External IDs are opaque strings: preserve exact source values and do not
assume UUID/GUID syntax. A TeamBuilder internal database ID is optional
adapter metadata and is not required by the interchange schema. References
such as `teamRef`, `seriesRef`, `organizationRef`, and `participantRef` use
the same namespace/ID pair as their target.

### Activity-specific roles

Activities define taxonomy-versioned role codes. A code such as `guard`,
`tank`, or `participant` is interpreted within its activity, not as a
universal role enum. Provider labels can map to canonical codes through
`externalLabels`; adapters retain the original source label when normalizing
records. Roles are optional for activities that only require a participant
count.

### Schedule, venue, and occurrence

`eventSeries` describes recurrence intent using an RFC 5545-compatible RRULE,
an IANA timezone, local start date/time, duration, and an explicit local end
date. RRULE expansion follows local wall-clock time in that zone. The adapter
must not replace a recurring local schedule with a fixed UTC offset.

`eventOccurrences` represent dated instances and carry UTC `startsAt` and
`endsAt`, their IANA timezone, venue, roster requirements, and assignments.
Occurrence-level status supports independent cancellation or editing without
rewriting the series. Timezone gap/overlap policy is agreed by producer and
consumer; source-resolved UTC instants are preserved.

Coordinates are decimal WGS 84 latitude/longitude. Address precision is
subject to the source license and privacy policy; omit address or coordinates
when disclosure is not permitted.

### Availability and roster states

Participant availability is source-reported intent for a time window, not a
reservation. `RosterAssignment.status` describes the source's current
participation state. TBRL exchange does not itself grant a participant a spot:
the TeamBuilder ingestion and reservation services must apply current
authorization, capacity, and concurrency rules.

Requirements express quantities and optional role codes rather than
numbered/fixed slots. Multiple requirements can be represented for a single
occurrence, but producers should avoid overlapping totals that a consumer
could interpret as double-counting the same capacity. An untyped
`participants` requirement may be used when role composition is not known.
For example, a generic five-person game should use one requirement with
`code: "participants"` and `quantity: 5`, instead of combining it with
role-specific requirements for the same five people.

### Provenance, licensing, and timestamps

`source` identifies the feed and includes its latest source timestamp when
available. Entity-level `sourceUpdatedAt` records source time for each
record; `exportedAt` records when the document was generated. These are
distinct from TeamBuilder's own received/processed timestamps.

License metadata includes the license/terms identifier, URL when available,
attribution, and permitted uses. Adapters must preserve this information with
the imported data and any downstream copies/projections. TBRL does not
authorize collection or scraping. Import only data the source permits the
consumer to use.

## Ingestion and idempotency

The expected ingestion stages are **Source Adapter → Parse → TBRL → Schema
Validation → Normalization → Entity Resolution / De-duplication →
TeamBuilder Ingestion API → canonical persistence → domain events →
search/refill projections**.

Use a source-scoped idempotency key based on stable external identifiers and
source version/timestamp (or an explicit feed batch ID). Store the original
source reference and payload digest with an import receipt. A retry of an
identical idempotency key and content returns the prior result; the same key
with conflicting content is rejected or quarantined rather than silently
overwriting canonical records.

Entity resolution may attach a source participant to an existing TeamBuilder
Player only when approved identifiers and confidence policy support the
match. Ambiguous records require review. Never deduplicate people solely on a
display name or infer that a provider ID equals an internal player ID.

Persist canonical changes and corresponding outbox events transactionally.
Downstream search/refill projections consume events idempotently and can be
rebuilt. Preserve source provenance and licensing metadata across that
boundary.

## Out of scope for v0.1

- Authentication, permissions, or proof that a source is authorized.
- A guarantee that imported assignments are currently confirmed or that
  imported availability is still valid.
- Binary/media payloads, payment data, medical data, or identity documents.
- A global participant identity or globally unique unnamespaced IDs.
- TeamBuilder internal database keys as required interchange fields.
- A final canonical activity/role vocabulary or a production ingestion API.
