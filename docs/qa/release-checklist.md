# Private QA release checklist

For a QA release to 10–20 testers. Each line needs evidence (a link, a screenshot, a command's
output or a CI run). Leave a line unchecked until it was actually done: device results are
never assumed from automated runs. Runbook: [private-qa-deployment.md](private-qa-deployment.md).

Release: `__________`  API image: `__________`  Web artifact: `__________`  Date: `__________`

## Before deploying

- [ ] Release workflow green for this commit: migration bundle validated on SQL Server (clean,
      idempotent, prior schema), web release build, container release smoke, Azure reference
      compiles. Link: ______
- [ ] QA uses its own database, OIDC SPA client and API audience, VAPID key pair, host name and
      secret store (none shared with Production).
- [ ] No secret in the repository, image or web build (`qa.env` / Key Vault only).
- [ ] Backup of the QA database taken (or point-in-time restore available) before migrating.

## Deploy

- [ ] Migration bundle applied to QA; `database status` exits 0 (no pending migrations).
- [ ] First deployment only: database stamped `QA`; `database show-environment` says `Stamped for QA.`
- [ ] API rolled out; `/healthz/ready` returns 200 with `database`, `environment`,
      `configuration` all `Healthy`; `version` and `commit` match the release.
- [ ] Restart or redeploy of the API leaves the data intact (count of players or games unchanged).
- [ ] Rollback path known: previous API image tag `__________` and previous web artifact `__________`.

## Automated smoke against QA

- [ ] `node scripts/qa/release-smoke.mjs` with four QA test-account tokens: every automated step
      PASS. Output attached.

## Exit evidence (people and devices)

- [ ] Real OIDC sign-in on at least **two** accounts (different people or test users), each lands
      on onboarding or their games, and `/players/me` shows the right player.
- [ ] **Android Chrome**: real Web Push received for a vacancy with the browser in the
      background; tapping it opened the game. Device/OS/Chrome version: ______
- [ ] **iPhone, installed Home Screen PWA** (iOS 16.4+): real Web Push received; tapping it opened
      the game. Device/iOS version: ______
- [ ] A basketball game at **10/10** capacity reached READY.
- [ ] Last-slot contention with several testers pressing **Join game** at once: exactly one winner,
      the others see the game is full, roster stays 10/10.
- [ ] Leave → push → refill: a player leaves, a subscriber is alerted, opens the game and claims
      the spot; the game is READY again.
- [ ] Private venue: the street address is masked for a non-member, visible after joining, and
      masked again after leaving.
- [ ] Logs reviewed for one session: no bearer tokens, OIDC subjects, push endpoints or keys,
      search coordinates or private street addresses.
- [ ] No search coordinates persisted (discovery stores nothing about the searcher; spot-check
      the database and the web/proxy access logs).
- [ ] Migration bundle succeeds from a clean database (CI) and from the previous release's
      schema (CI, and on the QA database for this release).
- [ ] API rollback to the previous compatible image tried, or documented why it was not.

Sign-off: ______________________
