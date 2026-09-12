# AT Protocol connector exploration — 9 September 2026

Follow-up: Docker development and public sign-in. The runtime failure for a real
handle resolved its public DID through the fixture PLC (404) and escaped the
challenge as 500. Keep resolution in AtprotoSessions, routing in AtprotoHttp and
corrective failures in the handler. Typed DevelopmentPlcDirectory selects only
explicit fixture identities; DevelopmentConnectHost applies only to exact allowed
origins, preserving protocol URLs. Tests cover public/fixture separation, strict
origin matching and production rejection. The application retains AddKoan and
its existing domain/data behavior. User-authorized Docker migration uses fresh
Linux keys with genuine reauthorization; DPAPI backups remain unchanged.

Status: exploration complete; public contract approved by the coordinating agent before production edits. The accepted Tangent EPIC-001 explicitly authorizes this generic connector contribution. This card owns no Tangent application policy.

**Task:** Add generic AT Protocol browser authentication to Koan through the standard ASP.NET authentication lifecycle and the existing Web Auth provider plan.

**Application intent:** A person signs in with their existing AT identity, and returns as the same verified DID after an application restart; an application can later use the separately authorized PDS session without receiving tokens in its browser cookie.

**Public expression:** Reference the proposed `Sylin.Koan.Web.Auth.Connector.Atproto` package and keep the ordinary host:

```csharp
builder.Services.AddKoan();
```

Configure `Koan:Web:Auth:Providers:atproto:ClientId` to the public HTTPS URL of `/auth/atproto/client-metadata.json`. `Scopes` defaults to `["atproto"]`. The challenge is the existing `/auth/atproto/challenge?identifier=<handle-or-DID>&return=<destination>`. The connector uses `/auth/atproto/callback`; an incompatible `CallbackPath` is rejected. Operators persist the session directory and the host's ASP.NET Data Protection key ring. The default PLC directory is the public protocol directory. A dedicated development configuration may use the profile's special localhost client ID and exact allowed local origins, only through `KoanEnv.Gate.DevelopmentOnly`.

**Guarantee/correction:** A successful authentication ticket binds the initiating browser's correlation cookie, one-use protected authorization state, expected account DID, discovered PDS, callback issuer, and token subject. Standard Koan cookie sign-in, return-URL validation, user claims, and external-identity linkage remain the owning lifecycle. Discovery cannot connect to nonglobal addresses except explicitly allowed development origins. Unsupported or incomplete configuration fails with the precise setting to correct; missing correlation, issuer mismatch, DID mismatch, invalid state, unusable refresh, or unsafe destination never creates a signed-in principal.

**Complete intent surface:** Package reference; existing `AddKoan()`; explicit provider ClientId; optional provider Scopes; a handle/DID submitted to the existing challenge endpoint; publicly reachable client metadata and fixed callback; durable session directory and Data Protection keys. Optional development-network settings are test topology, not production fallback. Additional resource permissions are application authorization decisions and are never added to minimal sign-in automatically. No manual provider registration or custom middleware ordering is required of the application.

The approved scope refinement keeps provider `Scopes` as the declared client-metadata upper bound. A first ordinary sign-in requests only `atproto`; when a validated durable session for the same DID/PDS/issuer/client already has a broader grant within that ceiling, ordinary sign-in requests that established grant again rather than silently downscoping it. Trusted server code uses `AtprotoChallenge.WithScopes(AuthenticationProperties, params string[])` to request an exact declared subset. Raw query scopes never grant access. This AT-specific meaning is documented rather than silently importing generic OAuth scope behavior.

**Public concepts:**

- Existing provider ClientId identifies the application's published metadata; existing Scopes express the access being requested.
- Identifier is the account a person chose; verified DID is the identity key.
- Connector options configure PLC discovery, custody directory, and an explicitly local development topology.
- A guarded native session client permits authorized application-server XRPC requests and explicit upstream disconnect. It exposes no raw OAuth secret or unguarded SDK HTTP client. Local logout remains local logout.

**Docs read:**

- `AGENTS.md` routes framework changes through contributor law and mandatory exploration.
- `CLAUDE.md` establishes reference-as-intent composition, adapter ownership, plain async verbs, environment gates, and current-source authority.
- `docs/MEMORY.md` requires actual provider evidence and durable workcards, and warns against asserting compatibility from source alone.
- `docs/engineering/README.md` defines corrective operational documentation.
- `docs/architecture/principles.md` places protocol mechanics in the adapter and semantic activation in Web Auth.
- `README.md` establishes the ordinary `AddKoan()` application expression.
- `docs/toc.yml` identifies the current identity and authentication guidance routes.
- `docs/guides/auth-howto.md` describes provider configuration and the existing authentication action surface.
- `docs/engineering/adding-a-connector.md` supplies connector project/module/documentation conventions; current package-local versioning in CLAUDE overrides its older release-train prose.
- `docs/reference/capability-map.md` gives exact current Web Auth and connector package names; Atproto is a newly proposed family sibling, not an existing published package claim.
- [AT OAuth profile](https://atproto.com/specs/oauth) defines metadata, localhost development, account binding, PKCE/PAR/DPoP and the public/confidential client distinction. This first connector is explicitly a public client; a web server may use that profile.
- [AT DID specification](https://atproto.com/specs/did) defines identity and PDS resolution; handles never become the persistent identity key.

**Code read:**

- `src/Connectors/Web/Auth/Google/Initialization/GoogleAuthModule.cs`: contributes defaults, allowing Web Auth to own eligibility and election; it supplies no protocol implementation.
- `src/Connectors/Web/Auth/Test/Initialization/TestAuthModule.cs`: registers controller discovery, typed options and provenance through a real functional module.
- `src/Koan.Web.Auth/Providers/AuthProviderPlan.cs`: compiles effective provider options and election once; the concurrent generic protocol seam retains this owner.
- `src/Koan.Web.Auth/Hosting/AuthSchemeSeeder.cs`: realizes standard ASP.NET schemes and links external identities. A registered AT scheme must use the same public provider ID.
- `src/Koan.Web.Auth/Controllers/AuthController.cs`: validates return destinations then invokes an ASP.NET challenge; logout remains cookie-only.
- `src/Koan.Web.Auth.Abstractions/Options/ProviderOptions.cs`: owns the canonical merge, existing ClientId/Scopes/CallbackPath, and avoids a second generic provider model.
- `src/Koan.Web.Auth.Abstractions/Providers/AuthProviderDefinition.cs` and `IAuthProviderCatalog.cs`: expose availability and redacted effective scope/eligibility; no credentials are added to the public catalog.
- `src/Koan.Web.Auth.Abstractions/Domain/IExternalIdentityStore.cs` and `ExternalIdentity.cs`: own external identity linkage using a hashed provider key.
- `src/Koan.Web.Auth/Hosting/RequestSchemeAdaptiveCookieBuilder.cs`: explains browser cookie storability; this internal OAuth/OIDC helper is not copied. AT uses GET callbacks, SameSite=Lax and explicit production Secure policy.
- `src/Koan.Core/KoanEnv.cs` and existing auth development-gate uses: establish the named environment decision surface.
- `tests/Koan.Web.Auth.Tests/Koan.Web.Auth.Tests.csproj`: supplies current framework-reference/xunit v3 project shape.
- Pinned CarpaNet OAuth source at `a24d54bf6a9ce3bbf7c1961d37ab099abe1d1a65`: real tested PKCE/PAR/DPoP/refresh and storage interfaces; callback issuer and subject consistency checks need the adapter validation layer.

**Reusing:**

- Already exists: one provider plan, generic `IAuthProtocol` validation seam, provider defaults, immutable effective scopes, standard AuthenticationProperties and RemoteAuthenticationHandler, Koan cookie scheme, return-URL policy, external identity store, controller/module discovery, typed options, environment gate, Data Protection, SDK identity resolver, SDK OAuth/state/session interfaces, SDK DPoP proof and token-refresh machinery.
- Already exists: real S01 provider/browser-API evidence across two PDSes, token rotation after actual process restart, replay rejection, and narrow grant enforcement. This proves SDK mechanics, not the forthcoming browser-cookie handler.
- Needs to be created: protocol options/validation, a correlated remote handler, public metadata controller, protected single-process protocol storage, guarded discovery/XRPC transport, generic sample and focused tests.
- Explicit constants/options/shared-type searches found the existing AuthConstants/ProviderOptions/ExternalIdentity and test connector constants; no current AT-specific equivalents.
- Searches for SSRF/ConnectCallback/private-address protection found only unrelated loopback helpers and ZenGarden address checks. There is no reusable Web Auth DNS-pinning transport. A connector-local implementation is the narrow owner until another protocol demonstrates identical requirements.

**Creating new:**

| New code | Exact location | Justification |
| --- | --- | --- |
| Package project | `src/Connectors/Web/Auth/Atproto/Koan.Web.Auth.Connector.Atproto.csproj` | A real protocol adapter with activation and I/O mechanics; same family as existing sign-in connectors |
| Module | `Initialization/AtprotoAuthModule.cs` under that project | Own registrations/options/controller discovery and redacted provenance once |
| Stable names | `Infrastructure/Constants.cs` | Canonical scheme, protocol, routes, claim/property names and config section keys |
| Typed network/session options | `Options/AtprotoOptions.cs` | Operator topology and custody choices; common ClientId/Scopes remain ProviderOptions |
| ASP.NET handler options | `Options/AtprotoAuthenticationOptions.cs` | Standard RemoteAuthenticationOptions, callback/sign-in/state-format/correlation setup |
| Protocol validator | `Hosting/AtprotoProtocol.cs` | Implements the inert generic validation contract and rejects unsupported overlay fields |
| Browser handler | `Hosting/AtprotoAuthenticationHandler.cs` | Framework remote-handler entry points own correlation and ticket production; SDK owns OAuth mechanics |
| Guarded network owner | `Protocol/AtprotoHttp.cs` | Validates scheme/origin, resolves and pins vetted DNS addresses, disables redirects and bounds responses |
| Protocol session orchestration | `Protocol/AtprotoSessions.cs` | One native SDK/session owner for discovery, identity binding, refresh, guarded XRPC and disconnect |
| Maintained DNS adapter | `Protocol/AtprotoDns.cs` | DnsClient 1.8.0 owns DNS wire validation over TCP; replaces the SDK's unchecked raw UDP TXT parser |
| Protected store | `Storage/ProtectedAtprotoStore.cs` | SDK state/session interfaces with encrypted payloads, atomic consumption and process exclusivity |
| Callback staging store | `Storage/VerifiedSessionStore.cs` | Prevents SDK callback persistence before browser/subject/issuer validation |
| Stable protocol claims | `Infrastructure/AtprotoClaimTypes.cs` | Protocol DID survives local identity reconciliation; handle is optional and bidirectionally verified |
| Trusted permission helper | `Hosting/AtprotoChallenge.cs` | Server-owned additional authorization without query-driven scope escalation |
| Metadata controller | `Controllers/AtprotoMetadataController.cs` | Attribute-routed public metadata, derived from validated fixed client identity and effective scopes |
| README and technical contract | `README.md`, `TECHNICAL.md` | Public configuration/correction plus implementation/security/lifecycle boundaries |
| Generic sample | `samples/AtprotoIdentity/*` | Ordinary AddKoan host, handle/DID form and authenticated who-am-I; no room/participant/Spaces model |
| Focused integration tests | `tests/Koan.Web.Auth.Atproto.Tests/*` | Actual host composition, correlation/issuer/subject denial, durable state/refresh, SSRF/environment guard tests |

All paths in the table after the project row are relative to that connector unless fully qualified. One top-level class per source file; classes are grouped in responsibility folders. No generic Web Auth files are owned by this slice.

**Coalescence:** Closest pattern is `GoogleAuthModule` for activation plus `AuthSchemeSeeder` for the standard ASP.NET lifecycle. Keep Web Auth as plan/election/return-URL/cookie owner. Add only AT protocol mechanics in this adapter. The concurrent `IAuthProtocol` seam absorbs the fixed OIDC/OAuth2 validation restriction; no AT branch enters the generic controller. A wider networking framework is premature; application-owned PKCE/DPoP or per-application login handlers would duplicate the same security lifecycle. The disposable Tangent probe remains evidence and is not a production implementation path. No legacy production AT path exists to delete.

**Ergonomics:** An operator adds one connector, one client metadata URL and an account input, then uses existing Koan auth routes and claims. Resource authorization uses existing Scopes. The session service has business-neutral, plain async verbs and no separate service process. Security-relevant development settings are explicit; public behavior is not guessed from the HTTP request Host header. Discovery machinery, key material and metadata fields do not leak into the sign-in UI.

**Constraints satisfied:**

- No new inline HTTP endpoints; the metadata route is a controller. Callback interception is the existing ASP.NET RemoteAuthenticationHandler pattern documented by WEB-0071 and AuthController.
- No empty placeholder types; every new type has a distinct lifecycle or semantic owner.
- Stable literals are centralized; network limits and custody paths are typed options.
- Protocol state is mechanical SDK custody, not an application domain entity or a competing repository abstraction.
- No unbounded entity reads; no public credential/session enumeration endpoint.
- Development origin exceptions require `KoanEnv.Gate.DevelopmentOnly` and exact configured origins, with guard tests proving every exception.
- Reusable module README/TECHNICAL and generic sample/test evidence are part of the slice. Parent integration owns generated inventory/TOC publication and contribution packaging.

**Risks:**

- CarpaNet is an experimental alpha. Successful real exchanges do not certify arbitrary PDS implementations; callback/subject and transport guards need negative tests.
- Initial server client is the supported public OAuth profile, so it does not claim confidential-client revocation/JWKS or longer refresh lifetimes.
- Protected local files support a single process; a second process must fail ownership rather than race token rotation. Multi-instance session storage is outside this first connector.
- Linux/container operators must deliberately persist/protect the host key ring. Encrypted session payloads do not secure a compromised application process.
- Rebinding protection must occur at connection establishment, including IPv4-mapped IPv6 and non-global special-use ranges; preflight DNS checking alone is insufficient.
- SDK raw ATProtoOAuthClient creates its own HTTP client. Returning it publicly would bypass the guarded transport, so the integration must expose guarded requests instead.
- Full Spaces repository verification is outside generic identity and remains a separate bounded in-process extension; no verifier claim is made here.

Approved concrete coalescence refinement: the application now needs non-OAuth credential exchange on independently resolved participant PDSes. `AtprotoHttp.Send(HttpRequestMessage, ct)` is public, while its raw HttpClient remains internal. `AtprotoSessions.ResolveDid(did, ct)` returns the SDK document only after the exact identifier check. This reuses the existing guarded transport without placing Spaces policy, credentials, or key selection in the generic connector.

Review-driven correctness work retains one native path: require PAR rather than accept the SDK's direct-authorization fallback; check current permission ceilings before session restoration and after token refresh; serialize callback session replacement with refresh/disconnect; and issue checked revocation with required client_id and SDK DPoP/nonce handling. These fixes are application integration/SDK contribution candidates, not new OAuth cryptography.
