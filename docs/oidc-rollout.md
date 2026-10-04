# OIDC Provider Configuration Guidance

TeamBuilder supports JWT validation through an OIDC authority. This guide
describes repository-supported settings and deployment checks; it does not
verify that an IdP, tenant, audience, QA environment, or Production deployment
has been configured.

Do not commit signing keys, client credentials, or other secrets. Use the
deployment environment's approved secret/configuration store.

## Runtime configuration

The API binds these keys from `Jwt`:

| Key | Purpose |
|---|---|
| `Jwt:Authority` | OIDC authority used to retrieve discovery metadata and signing keys when no symmetric signing key is configured. |
| `Jwt:Audience` | Expected access-token audience. |
| `Jwt:Issuer` | Expected issuer on the symmetric-key validation path. With OIDC authority validation, the issuer is validated using the authority's metadata. |
| `Jwt:RequireHttpsMetadata` | Controls HTTPS metadata requirements; keep enabled for deployed OIDC environments. |
| `Jwt:SigningKey` | If non-empty, selects symmetric-key validation instead of OIDC authority discovery. Keep local development signing keys in user-secrets. |
| `Jwt:ExternalIdentity:SubjectClaim` | Claim to use as external subject; defaults to `sub`. Configure `oid` for Entra if that is the stable identifier issued in the API access token. |
| `Jwt:ExternalIdentity:TenantIdClaim` | Optional tenant metadata claim; defaults to `tid`. |
| `Jwt:ExternalIdentity:Provider` | Descriptive provider metadata stored on new identity links; defaults to `oidc`. |

Environment-variable equivalents use double underscores, for example
`Jwt__Authority`, `Jwt__Audience`,
`Jwt__ExternalIdentity__SubjectClaim`,
`Jwt__ExternalIdentity__TenantIdClaim`, and
`Jwt__ExternalIdentity__Provider`.

The default authentication scheme is `ExternalIdentity`. JWT signature,
issuer, audience, and lifetime validation use the configured validation
settings. A validated token resolves to a `PlayerIdentity` by the exact token
issuer and configured subject claim, then to the internal TeamBuilder
`Player.Id`. The subject is opaque and need not be a GUID; do not make it equal
to `Player.Id` or normalize either identity-key value.

## Microsoft Entra ID

For an Entra integration, choose the appropriate tenant model and configure
the authority and expected audience for the API registration. When the access
token contains the Entra object ID claim and that is the selected stable
external identifier, set:

```text
Jwt__ExternalIdentity__SubjectClaim = oid
Jwt__ExternalIdentity__TenantIdClaim = tid
Jwt__ExternalIdentity__Provider = entra
```

Use tenant-appropriate `Jwt__Authority` and validate the actual token issuer
and audience from the selected registration. These are configuration
placeholders, not deployed values. Entra's `oid` remains the subject component
of an external identity key; TeamBuilder creates and stores a separate
internal `Player.Id`.

## Other OIDC providers

Use the claim configured by the provider as a stable issuer-scoped subject.
The default is `sub`. Provider-specific subject formats are opaque: values
such as `auth0|...` are valid strings and are not parsed as GUIDs. Configure
the provider label for metadata only; it does not change identity matching.

## Deployment checklist

For each target environment:

1. Confirm the selected provider, authority, audience, and issuer using the
   provider's actual access-token contract.
2. Configure `Jwt:Authority`, `Jwt:Audience`, and
   `Jwt:RequireHttpsMetadata=true` for OIDC validation, and ensure
   `Jwt:SigningKey` is absent so authority validation is selected.
3. Configure `Jwt:ExternalIdentity:SubjectClaim` to the intended stable claim.
   For Entra this may be `oid` when issued in the API token; configure `tid`
   and the `entra` provider label only as metadata requirements dictate.
4. Check that a valid token has the expected exact `iss` and subject claim,
   and that issuer/subject values match the persisted `PlayerIdentity` values.
5. Onboard a test caller through `POST /api/v1/players/me`, then verify
   `GET /api/v1/players/me` returns the linked profile.
6. Verify a valid but unlinked identity receives `404` from
   `GET /api/v1/players/me` and `403` from player-backed resource routes.
7. Verify missing/invalid credentials receive `401` on protected endpoints.

Do not infer successful QA or Production setup from this repository's
configuration placeholders. Confirm the actual environment settings with its
operators before rollout.

## Related documentation

- [Identity and authorization model](auth-plan.md)
- [API reference](api.md)
- [Deployment guide](deployment.md)
- [Postman smoke-test guide](postman-smoke-test.md)
