# TeamBuilder Integration API (v1)

The integration API lets another application, such as a league website, a scoreboard, a
club's member portal or a spreadsheet script, read the games a TeamBuilder host runs and who is
on each roster. It is read-only in v1.

- Base path: `/api/integration/v1`
- Contract: `teambuilder.roster.v1` ([JSON Schema](schemas/teambuilder-roster-v1.schema.json))
- OpenAPI 3.0 document: `GET /api/integration/v1/openapi.json` (anonymous, served in every environment)
- Authentication: an integration API key in the `X-Api-Key` header

The app's own API (`/api/v1`) is for TeamBuilder's clients and signed-in users. It is not a
stable integration contract and its Swagger document is only served in Development.

## Quick start

1. Sign in to TeamBuilder and create a key. The key is returned once, in `key`; store it in
   your application's secret store.

   ```bash
   curl -X POST https://teambuilder.example/api/v1/players/me/api-keys \
     -H "Authorization: Bearer $USER_TOKEN" -H "Content-Type: application/json" \
     -d '{"name":"League website","expiresInDays":90}'
   ```

   ```json
   {
     "key": "tbk_3f9a0c1d2e4b_q1n3...",
     "id": "7f0c2b9e-...",
     "name": "League website",
     "keyPrefix": "tbk_3f9a0c1d2e4b",
     "scopes": ["rosters:read"],
     "createdAtUtc": "2026-10-20T08:00:00Z",
     "expiresAtUtc": "2027-01-18T08:00:00Z",
     "lastUsedAtUtc": null,
     "revokedAtUtc": null,
     "isActive": true
   }
   ```

2. Check the key works:

   ```bash
   curl https://teambuilder.example/api/integration/v1/me -H "X-Api-Key: $TEAMBUILDER_API_KEY"
   ```

3. List your upcoming games, then read one roster:

   ```bash
   curl "https://teambuilder.example/api/integration/v1/occurrences?from=2026-10-20T00:00:00Z&to=2026-11-20T00:00:00Z" \
     -H "X-Api-Key: $TEAMBUILDER_API_KEY"
   curl https://teambuilder.example/api/integration/v1/occurrences/{occurrenceId}/roster \
     -H "X-Api-Key: $TEAMBUILDER_API_KEY"
   ```

## API keys

A key acts for the player who created it, and it can only read games that player hosts. A
key never authenticates anything under `/api/v1`, and a user's bearer token never
authenticates anything under `/api/integration/v1`.

| Call | Purpose |
|---|---|
| `POST /api/v1/players/me/api-keys` | Create a key. Body: `name` (required, up to 80 characters), `expiresInDays` (optional, 1 to 365; omit for no expiry), `scopes` (optional; default `["rosters:read"]`). 201 with the key, sent with `Cache-Control: no-store`. |
| `GET /api/v1/players/me/api-keys` | Your keys, active first. Lists show `keyPrefix` only, never the key. |
| `DELETE /api/v1/players/me/api-keys/{id}` | Revoke a key. It stops working on the next request. Idempotent 204; 404 for a key that is not yours. |

These calls need a signed-in, onboarded player (401 without a token, 403 before onboarding).

- **Format.** `tbk_` + a 12-character lowercase hex prefix + `_` + a 43-character base64url
  secret, 60 characters in total. The prefix is not secret; it identifies the key in lists and
  logs. Secret scanners can match `tbk_[0-9a-f]{12}_[A-Za-z0-9_-]{43}`.
- **Storage.** TeamBuilder stores a SHA-256 of the key, never the key itself. A lost key cannot
  be recovered: revoke it and create a new one.
- **Limits.** 10 active keys per player (409 `ApiKeyLimitReached` beyond that). Creating and
  revoking is limited to 20 changes an hour per player (`RateLimiting:ApiKeys`).
- **Last use.** `lastUsedAtUtc` is updated at most once a minute per key.
- **Rotation.** Create the new key, deploy it, then revoke the old one. Two keys can be active
  at once for exactly this.
- **Keep keys server-side.** A key is a credential for your account's games. Browsers cannot
  send `X-Api-Key` to TeamBuilder from another origin (CORS does not allow the header), so call
  the API from your server.

### Scopes

| Scope | Grants |
|---|---|
| `rosters:read` | `GET /me`, `GET /occurrences`, `GET /occurrences/{id}/roster` for games the key's owner hosts. |

Write scopes will be added as new values. An existing key never gains a scope it was not
created with. A key without the scope an endpoint needs gets 403.

## Endpoints

### `GET /api/integration/v1/me`

The calling key (`keyId`, `keyName`, `scopes`, `expiresAtUtc`) and the player it acts for
(`playerId`, `displayName`).

### `GET /api/integration/v1/occurrences`

Games the key's owner hosts that start in `[from, to)`, earliest first.

| Parameter | Default | Rules |
|---|---|---|
| `from` | now | ISO 8601; read as UTC unless it carries an offset. |
| `to` | `from` + 30 days | Must be after `from`; the window may span at most 92 days. |
| `page` | 1 | 1 or more. |
| `pageSize` | 50 | 1 to 100. |

The response is a page: `items` (occurrences), `totalCount`, `page`, `pageSize`,
`totalPages`, `hasPreviousPage`, `hasNextPage`. A bad parameter is 400 with a
`ValidationProblemDetails` naming the field.

Each occurrence carries `id`, `name`, `activityCode`, `status`, `startsAtUtc`, `endsAtUtc`,
`timeZoneId`, `seriesId`, `teamId`, `createdAtUtc`, `updatedAtUtc` and the roster totals
`requiredCount`, `supplyCount`, `openQuantity` and `isRosterReady`.

### `GET /api/integration/v1/occurrences/{occurrenceId}/roster`

One hosted game's roster. A game that does not exist and a game someone else hosts both
return the same 404, so a key cannot probe for other people's games.

```json
{
  "schema": "teambuilder.roster.v1",
  "generatedAtUtc": "2026-10-20T08:15:00Z",
  "occurrence": {
    "id": "c61e055e-21b6-41e6-a762-cbc2411b8851",
    "name": "Wednesday Basketball",
    "activityCode": "basketball",
    "status": "open",
    "startsAtUtc": "2026-10-22T00:00:00Z",
    "endsAtUtc": "2026-10-22T02:00:00Z",
    "timeZoneId": "America/New_York",
    "seriesId": null,
    "teamId": null,
    "requiredCount": 10,
    "supplyCount": 9,
    "openQuantity": 1,
    "isRosterReady": false,
    "createdAtUtc": "2026-10-19T18:00:00Z",
    "updatedAtUtc": "2026-10-20T07:06:03Z"
  },
  "venue": {
    "id": "5d1f...",
    "name": "North Gym",
    "addressLine1": "100 Example Avenue",
    "addressLine2": null,
    "city": "Bellevue",
    "stateOrProvince": "WA",
    "postalCode": "98004",
    "countryCode": "US",
    "latitude": 47.6101,
    "longitude": -122.2015,
    "timeZoneId": "America/New_York",
    "type": "indoor",
    "privacy": "private"
  },
  "requirements": [
    {
      "id": "0b7e...",
      "roleCode": "participant",
      "displayPosition": null,
      "sourceRoleLabel": null,
      "requiredCount": 10,
      "supplyCount": 9,
      "openQuantity": 1
    }
  ],
  "participants": [
    {
      "assignmentId": "9a33...",
      "playerId": "2c8d...",
      "username": "sam",
      "displayName": "Sam R.",
      "requirementId": "0b7e...",
      "roleCode": "participant",
      "status": "confirmed",
      "source": "player",
      "joinedAtUtc": "2026-10-20T07:08:00Z",
      "confirmedAtUtc": "2026-10-20T07:08:00Z",
      "checkedInAtUtc": null
    }
  ]
}
```

- `participants` lists players holding a spot now (`reserved`, `confirmed`, `checked_in`,
  `active`), in join order. People who left, were removed or did not show are not listed.
- Participants carry public profile fields only: never an email address or a sign-in identity.
- `venue` is the full venue, because the key acts for the host. When `privacy` is `private`,
  the address is meant for the host and the players; do not republish it publicly.
- Roster totals use the same arithmetic as the app: `openQuantity` is the sum of each
  requirement's open spots (surplus on one role never fills another), and `isRosterReady` is
  true only when the game has requirements and none has an open spot.

## Contract and versioning

- Field names are camelCase. Times are UTC ISO 8601 with a `Z`. Ids are GUIDs.
- Code values (`status`, `type`, `privacy`, `source`) are lowercase snake_case strings, never
  the internal enum numbers.
- Within v1, fields and code values may be added; none is renamed, removed or retyped. Ignore
  fields you do not know, and treat an unknown code as "other".
- A breaking change ships as `/api/integration/v2` with a new schema id, alongside v1.

| Code | Values |
|---|---|
| occurrence `status` | `planned`, `open`, `in_progress`, `completed`, `cancelled`, `archived` |
| participant `status` | `reserved`, `confirmed`, `checked_in`, `active` |
| participant `source` | `player` (self-claim), `host`, `import`, `system` |
| venue `type` | `indoor`, `outdoor`, `virtual` |
| venue `privacy` | `public`, `private` |

## Errors and limits

Errors are RFC 7807 problem details (`application/problem+json`).

| Status | When |
|---|---|
| 400 | A query parameter is invalid (`errors` names it). |
| 401 | No key, more than one key, or a malformed, unknown, revoked or expired key. The response carries `WWW-Authenticate: ApiKey header="X-Api-Key"` and never echoes the key. |
| 403 | The key lacks the scope the endpoint needs. |
| 404 | The game does not exist or the key's owner does not host it. |
| 429 | Over the per-key limit. Wait for `Retry-After` seconds. |

Each key may make 120 requests a minute (`RateLimiting:Integration`, per API instance). Every
response carries `X-Request-Id`; quote it when reporting a problem.

## What v1 does not do yet

- No writes (creating games, claiming or assigning spots).
- No webhooks: poll `GET /occurrences` and compare the roster totals, or re-read a roster.
  `updatedAtUtc` changes when the game itself is edited, not when a player joins or leaves.
- No organisation- or team-level keys: a key is bound to one host's games.
- No ingestion of external rosters. The planned import format is
  [TBRL v0.1](architecture/tbrl-v0.1.md).
