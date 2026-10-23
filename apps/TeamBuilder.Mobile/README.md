# TeamBuilder mobile

Native React Native / Expo client for Android and iOS. The application uses
the existing user API and real OIDC Authorization Code + PKCE. It has no
integration API key or developer-token sign-in.

## Run

Use Node 24.3 or later, then from this directory:

```powershell
npm.cmd ci
Copy-Item -LiteralPath .env.example -Destination .env
# Fill the public QA values in .env; never put a client secret in this file.
npm.cmd run android
```

Android Studio's SDK and a compatible JDK must be installed. On macOS with
Xcode, use `npm run ios`. Native authentication needs a development/native
build with the registered redirect scheme; Expo Go is not the sign-in target.

QA: `teambuilder-qa://auth/callback`
Production: `teambuilder://auth/callback`

The configured API base may include the hosting proxy prefix. For example,
a base of `https://qa.example.invalid/api` plus the API's
`/api/v1/players/me` produces `/api/api/v1/players/me` at a proxy that strips
its first `/api` segment. Confirm the actual deployment contract.

## Validation

```powershell
npm.cmd run typecheck
npm.cmd run lint
npm.cmd test
npm.cmd run config:check
npm.cmd run bundle:check
npm.cmd audit
```

Bundling is not a signed native build or a physical-device acceptance test.
See [authentication](../../docs/mobile/authentication.md),
[architecture](../../docs/mobile/architecture.md), and the
[dependency findings](../../docs/mobile/dependency-findings.md).
