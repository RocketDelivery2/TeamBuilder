# TeamBuilder — OIDC Staging/Production Rollout Plan

This document describes how to connect TeamBuilder to a real OIDC identity
provider (IdP) for staging and production environments. No code changes are
needed; the application already supports the OIDC authority path. Only
environment-specific configuration values need to be supplied.

**Do not commit real secrets, client secrets, or signing keys to source control.**
Use Octopus Deploy variables, Azure App Service application settings, or
Azure Key Vault references for all sensitive values.

---

## Background

TeamBuilder JWT Bearer configuration supports two paths:

| Path | When active | Used for |
|---|---|---|
| **Symmetric key** | `Jwt:SigningKey` is set | Local development with `dotnet user-jwts` |
| **OIDC authority** | `Jwt:SigningKey` is absent and `Jwt:Authority` is set | Staging and production |

For staging and production, set `Jwt:Authority` (and leave `Jwt:SigningKey`
absent) so the application validates tokens against the published OIDC metadata.

---

## Configuration Keys

All keys live under the `Jwt` section of `appsettings.{Environment}.json` or
as environment variables / deployment variables.

| Key | Required for OIDC | Description | Example value |
|---|---|---|---|
| `Jwt:Authority` | Yes | Base URL of the OIDC issuer. Appends `/.well-known/openid-configuration` for key discovery. | `https://login.microsoftonline.com/{tenant-id}/v2.0` |
| `Jwt:Audience` | Yes | The audience (`aud`) claim the token must contain. Must match the IdP app registration. | `api://teambuilder-api` |
| `Jwt:Issuer` | Optional | Explicit issuer override. Leave empty to use OIDC discovery. | _(empty)_ |
| `Jwt:PlayerIdClaim` | Optional | JWT claim name carrying the TeamBuilder player GUID. Default: `sub`. | `sub` or `oid` |
| `Jwt:RequireHttpsMetadata` | Optional | Whether HTTPS is required for OIDC metadata. Must be `true` in deployed environments. | `true` |
| `Jwt:SigningKey` | Must be absent | Symmetric key for local dev only. If present it overrides OIDC. Must be absent in all deployed environments. | _(absent)_ |

---

## Supported Identity Providers

### Option A — Microsoft Entra ID (formerly Azure AD)

1. Open Azure Portal — Entra ID — App registrations and create a new registration.
   - Name: `TeamBuilder API`
   - Supported account types: single-tenant or multitenant as required.
   - Redirect URI: leave blank (API only, no login UI).
2. Note the **Application (client) ID** and **Directory (tenant) ID**.
3. Under **Expose an API**, set the Application ID URI (e.g. `api://teambuilder-api`)
   and add a scope (e.g. `api://teambuilder-api/access_as_player`).
4. Under **Token configuration**, add the `oid` claim (stable GUID, survives password resets).
5. Create a separate client app registration for each frontend / SPA client.

```text
Jwt:Authority     = https://login.microsoftonline.com/{tenant-id}/v2.0
Jwt:Audience      = api://teambuilder-api
Jwt:PlayerIdClaim = oid
```

> Entra ID `oid` is a stable GUID and maps directly to `Player.Id`.
> The `sub` claim in v2 tokens is a pairwise pseudonymous value and is not a GUID.

---

### Option B — Auth0

1. Open Auth0 Dashboard — Applications — APIs and create a new API.
   - Name: `TeamBuilder API`
   - Identifier (audience): `https://api.teambuilder.example.com`
   - Signing algorithm: `RS256`
2. Note the **Domain** (e.g. `your-tenant.us.auth0.com`).
3. Create a client Application and grant it the API scope.
4. Add an Auth0 Action to emit a GUID-compatible player ID claim.

```text
Jwt:Authority     = https://your-tenant.us.auth0.com/
Jwt:Audience      = https://api.teambuilder.example.com
Jwt:PlayerIdClaim = sub
```

> Auth0 `sub` format is `auth0|<id>` which is **not** a GUID.
> Use a custom action to emit a GUID-valued claim and update `Jwt:PlayerIdClaim`.

---

## Environment-Specific Configuration Guidance

### Local development

Local development uses `dotnet user-jwts`. No OIDC provider required.
`Jwt:SigningKey` is stored in `dotnet user-secrets` only — never committed.

### Staging (QA)

`appsettings.QA.json` uses Octopus `#{...}` token placeholders substituted at
deploy time. The following Octopus variables must be defined for QA:

```text
Jwt__Authority            = #{Jwt.Authority}
Jwt__Audience             = #{Jwt.Audience}
Jwt__Issuer               = #{Jwt.Issuer}
Jwt__PlayerIdClaim        = #{Jwt.PlayerIdClaim}
Jwt__RequireHttpsMetadata = true
```

`Jwt__SigningKey` must be absent from all QA configuration.

### Production

Same variable set as QA using a separate Octopus environment and a separate
IdP app registration.

---

## Player ID Claim Mapping

`Jwt:PlayerIdClaim` must name a claim whose value is parseable as a `Guid`.

| IdP | Recommended claim | Notes |
|---|---|---|
| Entra ID | `oid` | Stable GUID; survives password resets and email changes. |
| Auth0 | custom | `sub` is `auth0\|<id>` — not a GUID. Emit a custom GUID claim via Auth0 Actions. |
| Local dev | `sub` | `dotnet user-jwts` emits a GUID-compatible `sub` by default. |

If the claim is missing or not a GUID, `ICurrentUserContext.UserId` returns
`Guid.Empty` and write requests receive `401 Unauthorized`.

---

## QA Execution Checklist

Complete all steps for QA before repeating for Production.

### 1. Choose provider

- [ ] Confirm provider: Microsoft Entra ID, Auth0, or other OIDC-compliant IdP.
- [ ] Confirm the tenant or organization exists.

### 2. Create the QA app registration

- [ ] Register the API application in the IdP for QA.
- [ ] Set the audience identifier (Application ID URI or API identifier).
- [ ] Record Authority URL, Client ID, and Audience in a team vault — do not
      commit to source control.

### 3. Configure audience and issuer

- [ ] Confirm `<authority>/.well-known/openid-configuration` is reachable from
      the QA App Service (no outbound firewall blocking OIDC metadata).
- [ ] Verify `Jwt__Audience` matches the `aud` claim the IdP will issue.
- [ ] Set `Jwt__Issuer` only if the issuer differs from the authority URL.

### 4. Configure the player ID claim

- [ ] Decide which claim carries the player GUID (`sub`, `oid`, or custom).
- [ ] Configure the IdP to emit the claim if needed.
- [ ] Verify the claim value is a valid GUID using [jwt.io](https://jwt.io).
- [ ] Note the claim name for `Jwt__PlayerIdClaim`.

### 5. Set Octopus variables for QA

- [ ] Define in the Octopus QA environment:
  - `Jwt.Authority` — OIDC authority URL
  - `Jwt.Audience` — expected audience
  - `Jwt.Issuer` — issuer (or leave empty for discovery)
  - `Jwt.PlayerIdClaim` — claim name
- [ ] Confirm `Jwt.SigningKey` is **absent** from QA Octopus variables.
- [ ] Confirm `Jwt__SigningKey` is absent from QA Azure App Service settings.

### 6. Deploy QA

- [ ] Trigger a QA deployment via Octopus Deploy.
- [ ] Confirm App Service application settings show the substituted values
      (verify in Azure Portal — do not log or expose values).

### 7. Acquire a real JWT

- [ ] Obtain a bearer token from the QA IdP.
- [ ] Inspect at [jwt.io](https://jwt.io) and confirm:
  - `iss` matches the expected issuer.
  - `aud` contains the configured audience.
  - The player ID claim is present and is a valid GUID.

### 8. Run the Postman smoke test

- [ ] Set the `token` variable in the Postman QA environment.
- [ ] Run the smoke-test collection against the QA base URL.
      See [docs/postman-smoke-test.md](postman-smoke-test.md).
- [ ] Confirm write requests succeed with the token.
- [ ] Confirm read and health endpoints succeed without a token.

### 9. Validate 401 / 403 / 409 behavior

- [ ] **401** — `POST /api/v1/teams` with no token returns `401`.
- [ ] **401** — Write endpoint with expired or tampered token returns `401`.
- [ ] **403** — Player B mutating Player A's resource returns `403`.
- [ ] **409** — Mutation against a null-owner resource returns `409`.
- [ ] **200/204** — Owner performing a valid write returns success.
- [ ] **Health** — `GET /health` and `GET /health/ready` return `200` without
      a token.

---

## General Rollout Summary

For Production, repeat steps 2-9 using a separate app registration and the
Production Octopus environment.

| Step | Description |
|---|---|
| 1 | Choose provider |
| 2 | Create app registration for the environment |
| 3 | Configure audience and issuer |
| 4 | Configure player ID claim |
| 5 | Set Octopus / App Settings variables — no secrets in source control |
| 6 | Deploy |
| 7 | Acquire real JWT and inspect at jwt.io |
| 8 | Run Postman smoke test |
| 9 | Validate 401, 403, and 409 behavior |

---

## What Not to Commit

| What | Where it should live instead |
|---|---|
| `Jwt:Authority` (staging/production values) | Octopus variable or Azure App Settings |
| `Jwt:Audience` (staging/production values) | Octopus variable or Azure App Settings |
| `Jwt:Issuer` (staging/production values) | Octopus variable or Azure App Settings |
| IdP client secrets / credentials | Octopus sensitive variable or Azure Key Vault |
| `Jwt:SigningKey` | `dotnet user-secrets` (local only) |
| Application Insights connection string | Octopus variable (already uses `#{...}` tokens) |

---

## Related Documentation

| Document | Relevance |
|---|---|
| [docs/auth-plan.md](auth-plan.md) | Authentication implementation history and JWT key reference. |
| [docs/deployment-next-steps.md](deployment-next-steps.md) | Azure App Service, Octopus Deploy, and Key Vault hosting plan. |
| [docs/postman-smoke-test.md](postman-smoke-test.md) | Smoke-test guide including Bearer token usage. |
| [docs/api.md](api.md) | API reference — Authentication section. |
