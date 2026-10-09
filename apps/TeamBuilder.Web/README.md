# TeamBuilder web (private QA client)

A thin, mobile-first React + TypeScript + Vite client for the TeamBuilder
API. It renders what the API returns and holds no roster rules of its own:
capacity, readiness and permissions are decided by the server.

Screens: **My games**, **Find a game** (nearby games by distance, date and
time, closest first), **New game** (pickup game form with an optional venue),
**Game** (roster, join/leave, host controls, venue address or the private
venue note, and **Notify me if a spot opens** per role on a full roster),
**Notifications** (newest first, unread highlighted, opening one marks it read
and opens the game, plus the browser-alert switch) and a **Share** link on each
game. The header bell shows
the unread count.

In-app notifications are always on. The bell re-reads the unread count when
the tab regains focus or becomes visible and every 30 seconds while it is
visible (`BELL_POLL_MS` in `src/components/NotificationBell.tsx`); hidden tabs
do not poll. The game page and the notifications page also re-read on focus.
There is no SignalR.

## Browser alerts (Web Push)

**Current.** When the API has Web Push configured (`GET /api/v1/push/config`
says `enabled`), a full game's page (after **Notify me if a spot opens**) and
the **Notifications** page offer **Notify me when a spot opens**. Only that
click asks the browser for notification permission; nothing prompts on page
load. Allowed: the client registers the service worker's push subscription
with the API (`src/lib/webPush.ts`) and shows **Browser alerts on** with
**Turn off browser alerts**. Denied: it says alerts are blocked in the
browser settings, and in-app notifications keep working. Unsupported browser,
insecure context or server-side push off: the option is hidden or explained,
never an error.

`public/sw.js` is a small plain-JavaScript service worker (no build step, no
caching of the app): it shows the push as a notification (one per game, so a
repeat replaces it) and, on click, focuses an open TeamBuilder tab and
navigates it, or opens a new one, at the exact game URL with `via=push`. It
never claims anything. The game page then reports the open, re-reads the
roster, and if the spot was already taken or the game was cancelled says so
above the current roster. When the browser rotates the subscription the worker
asks the open page to re-register it, retiring the old endpoint.
`public/manifest.webmanifest` and the icons make the app installable; Safari on
iOS only allows Web Push for an app added to the home screen.

Push needs a secure context: HTTPS, or `http://localhost`. Tests evaluate
`sw.js` against a fake `self` (`src/sw.test.ts`).

**Future:** native mobile push (APNS/FCM apps), SMS and email.

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
`/api/` to `API_UPSTREAM` (default `http://api:8080`); `sw.js` and the manifest
are served `no-cache` so a fixed service worker reaches browsers at once. See
`docker-compose.qa.yml` in the repository root.
