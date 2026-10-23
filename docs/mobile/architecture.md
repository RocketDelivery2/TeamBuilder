# Mobile architecture

The shared application lives in `apps/TeamBuilder.Mobile`. Expo SDK 58
selects the React Native and native-module versions; direct dependencies
are pinned and the npm lockfile records the full dependency tree.

Expo Router owns navigation. Screens call typed API services through
TanStack Query. HTTP transport owns cancellation, a 15-second request
timeout, typed problem responses, and Retry-After throttling. Only GET
requests may replay once after a 401 token refresh. Mutations are never
transport-retried or queued offline.

The API remains authoritative for ownership, capacity, identity mapping,
and venue masking. Browser-only code is not imported. OIDC credentials
are stored with Expo SecureStore; query results and search coordinates
remain in memory and are cleared when the session ends. Read requests
are cancelled on sign-out. AppState controls foreground query refresh.

The native interfaces use accessible React Native controls, safe areas,
keyboard avoidance, and system/light/dark colors. No WebView hosts the UI.
The optional web bundle is a development preview, not the native sign-in
or device acceptance target.

## Change and rollback boundaries

Mobile packages, source, and documentation are separate from the existing
web client. Native push requires an additive backend delivery channel;
browser subscriptions and VAPID credentials must remain separate.

Revert a mobile phase's commit to roll back its source changes. Database
migrations and provider registration changes require their own rollback
review; never drop production data as part of a source rollback.
