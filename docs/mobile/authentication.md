# Native authentication

Register a **public/native client** with the existing TeamBuilder OIDC
provider. Do not embed a client secret or assume the web SPA registration
supports native redirect URIs. Grant the public client the delegated API
scope already validated by the TeamBuilder API.

Register the exact callback and post-logout callback for each environment:

- QA: `teambuilder-qa://auth/callback`
- Production: `teambuilder://auth/callback`

Provide the authority, public client ID, API scope plus `openid profile`,
and `offline_access` only if the provider supports refresh tokens.
These are public build configuration. SecureStore holds access/refresh
tokens; they are never written to logs, AsyncStorage, or analytics.

AuthSession opens the system browser, supplies PKCE, validates OAuth state,
and exchanges the authorization code. An early expiry margin triggers a
single shared refresh request. If refresh is unavailable or fails, the
session is cleared and the user signs in again. A late login or refresh
cannot restore a session after logout. Stored sessions are bound to the
environment, API origin/prefix, authority, client ID, and scopes.

Logout clears local credentials and query state before attempting provider
revocation and end-session. Provider logout can fail offline; local logout
still completes. Account selection is requested on the next sign-in.

The API validates token issuer/audience and maps issuer plus subject to
PlayerIdentity and then Player.Id. The mobile client calls GET
`/api/v1/players/me`; only 404 sends an authenticated user to onboarding.
POST to the same route creates the linked profile. No GUID-subject
shortcut or mobile integration key is used.

## Required live verification

Two authorized QA accounts must sign in on actual native builds. Verify
their distinct Player.Id values, restoration, refresh/expiry, logout,
account switching, cancelled sign-in, and callback handling. Unit tests
exercise session behavior but do not establish provider registration,
provider interoperability, or real-device success.

Reference: [Expo authentication](https://docs.expo.dev/guides/authentication/).
