# Private QA: pickup basketball

This guide runs the TeamBuilder API, SQL Server and the web client
(`apps/TeamBuilder.Web`) for private dogfooding of one flow: a host creates a
Wednesday 8 PM pickup basketball game, shares the link, other players join,
someone leaves and the open spot is refilled, and a nearby player finds the
game with **Find a game**.

It is a QA path, not a production deployment. Nothing here changes how the
API authenticates: every write still needs a valid bearer token for a linked
player.

## Prerequisites

| Path | Needs |
|---|---|
| Docker stack (recommended) | Docker with Compose v2, Node.js 20+ (only to mint QA tokens) |
| Run each piece locally | .NET 10 SDK, Node.js 20+, Docker (for SQL Server) or an existing SQL Server |

No secret is stored in this repository. You choose the SQL password and the
JWT signing key yourself, keep them in a git-ignored `.env` file or your
shell, and never paste them into an issue, chat or commit.

## Option A: everything in Docker

1. Create `.env` in the repository root (it is git-ignored) with two values
   you generate locally:

   ```bash
   cat > .env <<'ENV'
   TEAMBUILDER_QA_SQL_PASSWORD=<a strong password that meets SQL Server rules>
   TEAMBUILDER_JWT_SIGNING_KEY=<random string of at least 32 bytes>
   ENV
   ```

   For example, `openssl rand -base64 48` produces a suitable signing key.

2. Start the stack:

   ```bash
   docker compose -f docker-compose.qa.yml up --build
   ```

   - SQL Server listens only inside the Compose network.
   - The API is on `http://localhost:5080` and applies EF Core migrations on
     startup (`Database:ApplyMigrationsOnStartup=true`, which is off unless
     set).
   - The web client is on `http://localhost:8080` and proxies `/api/` to the
     API, so the browser makes same-origin calls.

3. Check the API: `curl http://localhost:5080/health` returns `Healthy`.

The QA web image is built with `VITE_ALLOW_DEV_TOKEN=true`, so its sign-in
page offers developer-token sign-in and every page shows a warning banner.
A default build of the image (`docker build apps/TeamBuilder.Web`) does not
offer it.

## Option B: run each piece locally

**SQL Server.**

```bash
docker run -d --name teambuilder-sql -e ACCEPT_EULA=Y \
  -e "MSSQL_SA_PASSWORD=$TEAMBUILDER_QA_SQL_PASSWORD" \
  -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
```

**API.** Store local settings with user-secrets (kept outside the repo), then
apply migrations and run:

```bash
cd src/TeamBuilder.Api
dotnet user-secrets set "ConnectionStrings:TeamBuilderSql" \
  "Server=localhost,1433;Database=TeamBuilderQA;User Id=sa;Password=$TEAMBUILDER_QA_SQL_PASSWORD;Encrypt=True;TrustServerCertificate=True"
dotnet user-secrets set "Jwt:SigningKey" "$TEAMBUILDER_JWT_SIGNING_KEY"
dotnet user-secrets set "Jwt:Issuer" "teambuilder-local-qa"
cd ../..
dotnet ef database update --project src/TeamBuilder.Infrastructure --startup-project src/TeamBuilder.Api
dotnet run --project src/TeamBuilder.Api --launch-profile http
```

The API listens on `http://localhost:5076`. Alternatively, use
`dotnet user-jwts` as described in [the API reference](api.md) and paste
those tokens instead of minting your own.

**Web client.**

```bash
cd apps/TeamBuilder.Web
npm ci
npm run dev
```

Open `http://localhost:5173`. The Vite dev server proxies `/api` to
`http://localhost:5076` (override with `TEAMBUILDER_API_PROXY`).
Developer-token sign-in is always available under `npm run dev`.

## Signing in for QA

**Developer tokens (local and private QA only).** Mint a short-lived token for
each test person with the same signing key the API uses. The key is read from
your environment and the token is printed once:

```bash
cd apps/TeamBuilder.Web
TEAMBUILDER_JWT_SIGNING_KEY=... npm run qa:token -- --sub qa-host
TEAMBUILDER_JWT_SIGNING_KEY=... npm run qa:token -- --sub qa-player-2
```

Paste a token on the sign-in page. The web client keeps it in
`sessionStorage` for that tab only. It is never written to `localStorage`, a
cookie, the build output or the repository, and **Sign out** clears it. Use a
separate browser profile or private window per test person.

The first sign-in for a subject asks for a username and display name; that
calls `POST /api/v1/players/me` and links the token's issuer and subject to a
new player.

**Real sign-in (OIDC).** The client supports Authorization Code with PKCE
through `oidc-client-ts`. It is enabled when `VITE_OIDC_AUTHORITY` and
`VITE_OIDC_CLIENT_ID` are set at build time; see [outside configuration
still needed](#outside-identity-provider-configuration-still-needed).

## The basketball flow

1. **Host creates the game.** Sign in as `qa-host`, choose **New game**. The
   form defaults to the Basketball preset (10 players), next Wednesday at
   8:00 PM local time, two hours long. Enter a location and leave **I'm
   playing too** checked. **Create game** sends one `POST /api/v1/events` with
   `rosterRequirements: [{ "roleCode": "participant", "requiredCount": 10 }]`
   and `hostParticipates: true`; the game, its requirement and the host's
   spot are created together. The game page shows **1/10 filled · 9 open**.

   Unchecking **I'm playing too** creates an organizer-only game (**0/10**).
   It still appears under **My games** with an **Organizing** label because
   the list uses `includeHosted=true`.

2. **Share the link.** The game page has a **Share this game** field with
   the game's URL (`/games/<id>`) and a **Copy link** button. The page is readable without signing in.

3. **A second player joins.** Open the link in another browser profile, sign
   in as `qa-player-2`, onboard, and press **Join game**. The roster shows
   **2/10**. Joining again is harmless: the API answers `200`
   with the existing spot.

4. **Fill it.** Repeat with more subjects until the roster reads **10/10**
   and the badge turns **READY**. An eleventh player sees "The roster is
   full" and the client does not retry.

5. **Leave and refill.** A player presses **Leave game**: the roster drops to
   **9/10**, READY clears and one spot reopens. Another player joins and
   the game is READY again. The player who left can rejoin later as a new
   spot.

6. **Host controls.** The host sees each participant with **Check in**,
   **No show** and **Remove** where they apply, plus **Start game**,
   **Finish game** and **Transfer host** (pick a participant or type a
   username). After the game is finished the page disables roster controls.

## Finding games nearby

Discovery is searched by coordinates. Nothing is geocoded, so a game is found
only when its host added a venue with coordinates.

1. **Host adds a venue.** On **New game**, check **Add the venue so nearby
   players can find this game**. Enter a venue name, choose Outdoor or
   Indoor, and pick who sees the address:
   - **Everyone**: a public court, park or gym.
   - **Only players in the game**: a driveway, backyard or private club. Keep
     the street address out of the name, which everyone sees.

   Press **I'm at the venue: use my location** or type the latitude and
   longitude (for QA, Chicago's Union Park is `41.8849`, `-87.6661`).
   **Create game** first sends `POST /api/v1/venues`, then the game with its
   `venueId`. If the game step fails, retrying reuses the same venue.

2. **A player searches.** Sign in as another subject and open **Find a game**
   (also on **My games**). The defaults are Basketball, today, any time and
   **15 mi**. Press **Use my location** (the browser
   asks once; refusing is fine) or open **Enter coordinates instead** and
   type a point near the venue, for example `41.8781`, `-87.6298`. Press
   **Search**.

   Results are sorted closest first. Each card shows distance, the start in
   the game's own time zone, the roster (for example **9/10 · 1 spot open**
   or **READY 10/10**), and **You're hosting** or **You're playing** where it applies.
   **Load more** fetches the next page.

3. **Change the search.** Switch **Distance** to **25 mi** or **50 mi** to see
   games further out. Choose **Evening (5–11 PM)** or **Around…** with a time
   for "Wednesday around 8 PM" (a two-hour window). Check **Open spots
   only** to hide full games.

4. **Check private address masking.** For a game at an "Only players in the
   game" venue, the searching player sees the venue name, city and an
   approximate distance, and the game page says the address is shared with
   players in the game. After **Join game** the page shows the street address.
   After **Leave game** it is hidden again. The host always sees it.

What to expect:

- The search point is sent with each request and is not saved to the
  player's profile or any table, and the API's request log omits the query
  string.
- Discovery allows 60 searches per minute per client address
  (`RateLimiting:Discovery`). In the Docker stack every browser reaches the API
  through the web proxy, so all QA testers share that budget. Raise
  `RateLimiting__Discovery__PermitLimit` on the API service if a group QA
  session hits **Too many searches**.
- Games without a venue (only the free-text location), virtual venues, and
  finished or cancelled games never appear.
- Searches cover at most 30 days ahead and 100 miles. Results come from one
  SQL Server, not from a worldwide search service.

## Outside identity provider configuration still needed

Real sign-in needs settings that live outside this repository. None of them
are secrets that belong in the web build; the SPA is a public client and
never holds a client secret.

| Where | Setting |
|---|---|
| Identity provider | Register a single-page application (public client) with Authorization Code + PKCE and no client secret. |
| Identity provider | Redirect URI `https://<web origin>/auth/callback`; post-logout redirect `https://<web origin>/`. |
| Identity provider | An API resource or scope whose access tokens carry the audience the API expects (`teambuilder-api` unless configured otherwise). |
| Identity provider | Access tokens must carry a stable subject claim (`sub` by default, or `oid` for Microsoft Entra with `Jwt:ExternalIdentity:SubjectClaim=oid`). |
| Web build args | `VITE_OIDC_AUTHORITY`, `VITE_OIDC_CLIENT_ID`, and `VITE_OIDC_SCOPE` including the API scope (for example `openid profile api://teambuilder/access`). Leave `VITE_ALLOW_DEV_TOKEN` unset. |
| API configuration | `Jwt:Authority` set to the issuer's authority, `Jwt:Audience` matching the token audience, no `Jwt:SigningKey`, and `AllowedOrigins` containing the web origin if the web client is served from another origin. |

Linking is by exact issuer plus subject (see [the authentication
plan](auth-plan.md)), so a person who signed in with a developer token and
later with the real provider becomes a different player.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| "Please sign in to continue" (`401`) | The token's issuer, audience or signing key does not match the API settings, or it expired. Mint a new one. |
| The page asks to onboard again | A different subject or issuer was used; each pair is a separate player. |
| **Find a game** shows nothing | No game with a venue within the radius and window. Widen the distance, clear **Open spots only**, or check that the game has a venue with coordinates (a free-text location is not searchable). |
| **Use my location** fails | The browser blocked location, or the page is not served over HTTPS or `localhost`. Enter coordinates instead. |
| "Too many searches" (`429`) | The discovery rate limit; wait a minute, or raise the limit for a group QA session. |
| "The roster changed while saving" | Several people changed the roster at once. On a join the client already retried twice with a short random delay, then refreshed; press **Join game** again if a spot is still open. |
| API exits on startup in Docker | SQL Server was not ready or the password does not meet complexity rules; check `docker compose -f docker-compose.qa.yml logs sql`. |
