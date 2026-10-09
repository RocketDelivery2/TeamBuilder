# TeamBuilder web (private QA client)

A thin, mobile-first React + TypeScript + Vite client for the TeamBuilder
API. It renders what the API returns and holds no roster rules of its own:
capacity, readiness and permissions are decided by the server.

Screens: **My games**, **Find a game** (nearby games by distance, date and
time, closest first), **New game** (pickup game form with an optional venue),
**Game** (roster, join/leave, host controls, venue address or the private
venue note, and **Notify me if a spot opens** per role on a full roster),
**Notifications** (newest first, unread highlighted, opening one marks it read
and opens the game) and a **Share** link on each game. The header bell shows
the unread count.

Notifications are in-app only. The bell re-reads the unread count when the
tab regains focus or becomes visible and every 30 seconds while it is visible
(`BELL_POLL_MS` in `src/components/NotificationBell.tsx`); hidden tabs do not
poll. The game page and the notifications page also re-read on focus. There
is no SignalR, Web Push or service worker yet.

For running it against an API and SQL Server, and for the full basketball QA
walk-through, see [docs/private-qa.md](../../docs/private-qa.md).

## Commands

```bash
npm ci          # install from the lockfile
npm run dev     # dev server on http://localhost:5173, proxies /api to http://localhost:5076
npm test        # unit and component tests (Vitest + Testing Library, jsdom)
npm run lint    # type-check
npm run build   # type-check and production build into dist/
npm run qa:token -- --sub <subject>   # mint a local QA token (needs TEAMBUILDER_JWT_SIGNING_KEY)
```

## Configuration

Copy `.env.example` to `.env.local` (git-ignored). Every `VITE_` value is
compiled into the public bundle, so none of them may be a secret.

| Variable | Meaning |
|---|---|
| `VITE_API_BASE_URL` | API origin; empty means same origin (Vite proxy or the nginx `/api/` proxy). |
| `VITE_OIDC_AUTHORITY`, `VITE_OIDC_CLIENT_ID`, `VITE_OIDC_SCOPE` | Authorization Code + PKCE sign-in. OIDC is offered only when authority and client id are both set. |
| `VITE_ALLOW_DEV_TOKEN` | `true` lets a built bundle offer manual bearer-token sign-in, with a warning banner. Default `false`. It is always offered under `npm run dev`. |

## Auth boundary

`src/auth/authAdapter.ts` defines the `AuthAdapter` interface the rest of the
app uses. Two adapters implement it:

- `OidcAuthAdapter` (`oidc-client-ts`): Authorization Code + PKCE, user state
  in `sessionStorage`, redirect to `/auth/callback`.
- `DevTokenAuthAdapter`: a pasted bearer token for local development and
  private QA only, kept in `sessionStorage` for the tab and cleared on sign
  out. A production build never offers it unless `VITE_ALLOW_DEV_TOKEN=true`
  was set explicitly at build time.

## Conflict handling

`src/api/conflicts.ts` maps API problem codes to one reaction each, and
`src/api/claim.ts` performs a join:

| Response | Client behaviour |
|---|---|
| `201` / `200` on join | Joined (`200` is the existing spot returned again). |
| `RequirementFull` | Shows that the game is full; never retried automatically. |
| `AlreadyParticipating` | Treated as joined; the page refreshes. |
| `RosterChanged` on join | Retried at most 2 times (hard cap 3) with a growing, jittered delay, then refresh and explain. |
| `OccurrenceChanged`, `RosterChanged` on host actions | Refresh and ask the host to try again. |
| `AssignmentEnded` | Refresh and show the participant's final status. |
| `OccurrenceClosed` | Disables roster controls. |
| `401` | Sign in again. |
| `403` | Explains the action is not allowed; no retry is suggested. |

## Location

**Find a game** and the venue form read the browser position once
(`navigator.geolocation.getCurrentPosition`) only when the person presses the
location button; there is no tracking or background location. Manual
coordinates are the fallback. The search point lives in page state and the
request URL only: it is never written to `localStorage`, `sessionStorage` or
the profile. Search times are computed in the browser's time zone
(`src/lib/discover.ts`) and sent to the API as UTC; each result is shown in
its venue's time zone. There is no address geocoding.

## Docker

`Dockerfile` builds the bundle with Node and serves it from nginx, proxying
`/api/` to `API_UPSTREAM` (default `http://api:8080`). See
`docker-compose.qa.yml` in the repository root.
