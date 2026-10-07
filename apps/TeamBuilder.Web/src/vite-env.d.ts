/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** API origin; empty means same origin (dev proxy or the nginx /api proxy). */
  readonly VITE_API_BASE_URL?: string;
  /** OIDC authority (issuer URL) for Authorization Code + PKCE. */
  readonly VITE_OIDC_AUTHORITY?: string;
  /** Public SPA client id registered at the identity provider. */
  readonly VITE_OIDC_CLIENT_ID?: string;
  /** Scopes requested, including the API scope that yields an access token for the API audience. */
  readonly VITE_OIDC_SCOPE?: string;
  /** Explicit opt-in for the manual bearer-token mode in a non-dev build (private QA only). */
  readonly VITE_ALLOW_DEV_TOKEN?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
