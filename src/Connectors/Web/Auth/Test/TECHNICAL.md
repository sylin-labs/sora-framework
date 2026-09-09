# Sylin.Koan.Web.Auth.Connector.Test — technical contract

## Activation

The module registers two immutable automatic provider definitions when `TestProviderOptions.IsActive` is true:

- `test-oidc`: OIDC, priority `26`, relative authority `/.testoauth`;
- `test`: OAuth2, priority `25`, local authorize/token/userinfo endpoints.

Development activates them by default. Outside Development, set
`Koan:Web:Auth:TestProvider:Enabled=true` only in controlled test environments. Web Auth still owns eligibility,
default election, scheme seeding, discovery, and evidence.

## Routes and protocol behavior

Controllers use stable absolute attribute routes under `/.testoauth`; there is no startup filter or conventional route
mapper. The OIDC simulator publishes an ES256 JWKS and signed ID token. OAuth2/OIDC code flow supports PKCE S256,
state/nonce through the consuming ASP.NET handler, redirect-URI checks, and one-time code redemption.

For the relative OIDC authority, Web Auth derives two views automatically. The live challenge request defines the
public issuer and browser authorization endpoint; the running Kestrel server address defines discovery, token,
userinfo, and JWKS back-channel endpoints. The authorization code records the public issuer, so an ID token minted by
an internal token request still has the exact issuer advertised to the browser-facing handler. This supports Docker
port publishing and reverse proxies without an application-owned back-channel option or DNS workaround.
Discovery and authorization canonicalize the local URI authority, including default ports, before constructing
the issuer. The consuming handler keeps exact issuer, audience, signature and nonce validation enabled.

Userinfo emits `sub`, `id`, username, email, roles, permissions, and custom claims. Web Auth maps roles to
`ClaimTypes.Role`, permissions to `Koan.permission`, and extra claims one-for-one.

## Options

Configuration section: `Koan:Web:Auth:TestProvider`.

- `Enabled`, `ClientId`, `ClientSecret`, `AllowedRedirectUris`;
- persona caps and defaults;
- optional JWT access-token settings;
- optional client-credentials clients and allowed scopes.

### Personas

Personas travel by cookie, not by the login form's visible fields: the shipped `login.html` writes
`document.cookie = '_tp_user=<name>|<email>'` and the provider reads that cookie at the callback.
Flows that avoid browser automation should set the `_tp_user` cookie directly before calling the
authorize endpoint; the form fields on the sample page are presentation only.

## Failure and security posture

Inactive controllers return 404. Invalid clients, redirect URIs, grants, PKCE verifiers, codes, and bearer tokens are
rejected. Empty redirect allow-lists permit only `/auth/{provider}/callback` shapes for local convenience.

This is not hardened for hostile callers, durable credentials, production availability, external federation, or
multi-process token state. Its in-memory stores reset with the process.
