# TeamBuilder Identity and Authorization

This document describes the current implementation. It does not establish
which identity provider or settings are deployed in any environment; verify
provider-side configuration with the environment operators.

## Identity model

TeamBuilder validates an external JWT and derives an identity key from:

```text
exact token issuer (iss)
    + configured external subject claim
    -> PlayerIdentity
    -> internal TeamBuilder Player.Id
```

`ExternalIdentity` is the default authenticate, challenge, and scheme. There
is one JWT bearer validation path for authenticated endpoints. The external
subject is opaque: it can be non-GUID and is not a TeamBuilder player ID.
Issuer and subject are compared exactly, using ordinal string semantics after
the database candidate lookup. The application does not trim, case-fold,
normalize, or otherwise rewrite either value.

`Player.Id` is generated and owned by TeamBuilder. Resource ownership is
checked using this internal ID resolved through `PlayerIdentity`, never by
parsing a JWT claim as the player ID.

## JWT configuration

All keys are under `Jwt`. Supply deployment values through the environment's
configuration provider or secret store as appropriate; do not commit signing
keys or credentials.

| Key | Purpose | Default / notes |
|---|---|---|
| `Jwt:SigningKey` | Selects symmetric HMAC JWT validation when non-empty. Intended for local development and tests. | Not set in the base appsettings file. |
| `Jwt:Authority` | OIDC authority used for metadata and signing-key discovery when `SigningKey` is absent. | Empty in base appsettings. |
| `Jwt:Audience` | Expected token audience. | `teambuilder-api` in base appsettings. |
| `Jwt:Issuer` | Expected issuer for symmetric-key validation. | No base default. With OIDC authority validation, issuer comes from authority metadata. |
| `Jwt:RequireHttpsMetadata` | Requires HTTPS when retrieving OIDC metadata. | `true` in base appsettings. |
| `Jwt:ExternalIdentity:SubjectClaim` | Claim name used as the external subject. | `sub`; use `oid` for Microsoft Entra when configured for that token claim. |
| `Jwt:ExternalIdentity:TenantIdClaim` | Optional tenant metadata stored on new identity links. | `tid`. It is not part of the lookup key. |
| `Jwt:ExternalIdentity:Provider` | Provider label stored on new identity links. | `oidc`. It is metadata, not part of the lookup key. |

`Jwt:SigningKey`, `Jwt:Authority`, `Jwt:Issuer`, `Jwt:Audience`, and
`Jwt:RequireHttpsMetadata` preserve the common JWT validation configuration.
When `SigningKey` is supplied, the symmetric path is selected; otherwise a
configured `Authority` enables OIDC metadata validation. `Program.cs` binds
the `Jwt:ExternalIdentity` section to the options above.

For Entra, configure `SubjectClaim` as `oid` when the access token contains
that claim and use a tenant-appropriate authority/issuer configuration. The
issuer provides tenant context; `tid` may additionally be stored as metadata.
The `oid` value still identifies a `PlayerIdentity`; it does not become
`Player.Id`.

## Onboarding and player endpoints

- `POST /api/v1/players/me` is the authenticated canonical onboarding route.
  It creates a TeamBuilder player with a generated internal ID and links the
  exact current issuer and configured subject.
- `GET /api/v1/players/me` returns the linked caller's full profile, including
  Email. An authenticated but unlinked identity receives `404`.
- `POST /api/v1/players` has been removed; anonymous player creation is not
  supported.
- `GET /api/v1/players`, `GET /api/v1/players/{id}`, and
  `GET /api/v1/players/username/{username}` remain public discovery routes and
  return `PublicPlayerDto`, which omits Email.
- `PUT` and `DELETE /api/v1/players/{id}` require authentication and are
  self-only through `PlayerIdentity` resolution.

## Resource authorization

Authentication alone does not grant access to every resource. A valid JWT
without a linked player receives `403` on routes that need a player-backed
authorization decision.

| Resource | Current authorization |
|---|---|
| Teams | Writes resolve the caller through `PlayerIdentity`; team update/delete are owner-only. Voluntary leave is self-only; the route cannot be used by an owner to remove another member. |
| Join requests | Creation is by the linked applicant. A request detail is readable by its applicant or the relevant team owner; team lists are owner-only; player lists are self-only; processing is team-owner-only. |
| Events | Writes resolve the host through `PlayerIdentity`; update/delete are host-only. Creating an event associated with a team requires ownership of that team, and inactive/disbanded teams are rejected. |
| Roster imports | Reads, processing, and deletion are restricted to the original linked importer. This protects uploaded data, including `RawData`. |
| Public discovery | Team and event discovery, health checks, and public player profiles remain anonymous. Join-request and roster-import reads are not public. |

Invalid, expired, or missing tokens on protected routes receive `401`.
Authenticated but unlinked or unauthorized callers receive `403`, except
`GET /players/me`, which returns `404` when the caller has not onboarded.

## Local development

`dotnet user-jwts` can issue a local development JWT. The subject is external
and opaque; it does not need to be a GUID. Create/link a player once through
`POST /api/v1/players/me`, then use that same token identity for player-backed
requests.

```powershell
cd src/TeamBuilder.Api
dotnet user-jwts create --audience teambuilder-api --claim sub=local-user-123
```

Keep local signing keys in user-secrets. Never put signing keys, database
credentials, or provider secrets in source control.

## Provider rollout status

The application supports symmetric-key validation for development/tests and
OIDC authority validation. This repository does not verify that QA or
Production has a provider, authority, audience, tenant, or token configuration
deployed. See [OIDC rollout guidance](oidc-rollout.md) for configuration
planning and verification steps.
