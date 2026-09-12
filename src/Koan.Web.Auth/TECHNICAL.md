# Sylin.Koan.Web.Auth — technical contract

## Ownership

Web Auth owns one host-level authentication plan. Connector modules register immutable `AuthProviderDefinition`
values; application configuration overlays those defaults; `AuthProviderPlan` compiles availability, eligibility,
priority, correction, and default election once. The same plan drives scheme seeding, controllers, startup reporting,
composition facts, and the credential-free `IAuthProviderCatalog` projection.

Cross-module contracts live in inert `Sylin.Koan.Web.Auth.Abstractions`. Optional modules must reference that assembly
directly instead of depending on functional Web Auth merely to consume a contract.

## Provider rules

- A connector definition is inactive until its provider has explicit configuration, unless the definition declares an
  automatic local provider.
- Explicit configuration outranks automatic providers; then priority and stable provider ID break ties.
- `PreferredProviderId` is required intent: unknown, disabled, unavailable, or incomplete targets stop startup.
- Configuration-only provider IDs are supported because OAuth2/OIDC mechanics belong to Web Auth itself.
- Required OIDC values: `Authority`, `ClientId`, `ClientSecret`.
- Required OAuth2 values: `AuthorizationEndpoint`, `TokenEndpoint`, `UserInfoEndpoint`, `ClientId`, `ClientSecret`.
- Other protocols require one connector-owned `IAuthProtocol` validator plus the named ASP.NET handler. Unknown
  protocols, duplicate validator identities, and attempted replacement of reserved `oidc`/`oauth2` mechanics stop
  startup. Protocol identities are trimmed and compared without case sensitivity.

Provider callbacks use `/auth/{id}/callback`. Relative endpoints and authorities are reserved for the self-hosted local
test provider; deployment providers should use absolute HTTPS endpoints.

The local relative OIDC authority is projected as one logical provider with separate transport addresses: the live
request supplies its public issuer/authorization origin, while `IServerAddressesFeature` supplies its internal
back-channel origin. If neither a bound address nor a loopback public origin is available, challenge fails with a
correction naming the required Kestrel binding instead of attempting an unreachable public hostname.
Local issuer origins use standard URI authority canonicalization so explicit default ports agree with
implicit ones across browser redirects. Canonicalization applies to locally projected issuers only;
external issuers are validated exactly as configured.

## Runtime behavior

`AuthModule.Start` resolves the immutable plan and ensures one ASP.NET scheme per eligible provider. OIDC uses
`OpenIdConnectHandler`; OAuth2 uses `OAuthHandler<OAuthOptions>`. PKCE, state, correlation, OIDC nonce, issuer,
audience, and signature validation remain owned by maintained ASP.NET handlers.

A custom connector registers its `IAuthProtocol`, `AuthProviderDefinition`, and ordinary ASP.NET named scheme in its
module registration phase. Web Auth passes the effective, merged `ProviderOptions` into the protocol validator once
for each active provider. The validator returns an empty list for complete configuration, or secret-free corrective
messages with full configuration paths. It must reject unsupported scope, callback, or other overrides that its
handler cannot honor; it must not perform network I/O or mutate configuration. Inactive, disabled, and unavailable
providers do not undergo protocol validation.

The existing seeder preserves a registered scheme and rejects any eligible route that still lacks one. Merely
declaring a custom `Type` does not implement configuration-only provider IDs: the connector must register each
corresponding scheme. Normalized protocol and merged provider options remain owned by the compiled plan. There is no
second custom protocol startup lifecycle and no per-request protocol discovery.

The unmodified challenge controller applies the existing return-URL policy before invoking the named handler.
Protocol-specific initiation input is available through the handler's request; the connector owns validation and
must bind any security-relevant value into its protected transaction state rather than trusting callback query
parameters. Custom handlers own their protocol's remote validation, subject mapping, external linkage, and any token
storage. They sign in through `AuthenticationExtensions.CookieScheme` to retain Koan's cookie lifecycle and rejection
semantics. A registered scheme proves composition only, not that these security requirements are implemented.

The framework cookie scheme is `Koan.cookie`. Challenge and access-denied responses pass through the discovered
`IKoanAuthFlowHandler` pipeline. JSON/API requests receive the built-in JSON challenge behavior; interactive requests
redirect to the elected provider when one exists.

## Lifecycle failure semantics

- bootstrap, sign-in, challenge, and access-denied exceptions propagate;
- validation exceptions reject the principal and are logged;
- an explicit sign-in rejection prevents cookie issuance;
- external identity linkage completes before sign-in and fails closed;
- sign-out handler failures are logged and local sign-out continues.

Handlers are scoped, auto-discovered implementations of `IKoanAuthFlowHandler`, ordered by `Priority` and then full
type name. Framework-contributed and application handlers share one pipeline. Provider-owned claims are mapped once
from the provider's assertions; refreshing application-owned role claims is a flow-handler decision (stamping the
mutable sign-in identity and re-validating the cookie principal), never a Web Auth normalization. These
cookie events do not validate bearer tokens. The built-in
role-list handler applies an email-keyed allow/revoke JSON file at sign-in when
`Koan:Web:Auth:Lifecycle:RoleListFile:FilePath` is configured. At priority 50 it applies revocations after
lower-priority handlers; a later handler can add roles again. Choose ordering deliberately for
application-owned role authority. Implement only the events the module owns; no DI registration is required.

## Inspectability

Startup logs include total providers, eligible providers, default ID, and election reason. Composition facts expose one
observation per provider plus the default election receipt. `IAuthProviderCatalog` deliberately excludes credentials.
The public discovery route returns eligible providers only.

## Unsupported and deferred

- no SAML handler;
- no automatic secret-vault reference resolution;
- no built-in provider-token persistence or refresh-token API for calling third-party APIs;
- no claim that a provider console, callback URI, consent policy, or tenant allow-list can be inferred by Koan.

The embedded authorization server has its own package and contract.
