# AT Protocol connector mechanics

Web Auth owns provider election, return URL policy, sign-in contributors, cookies
and external identity management. This connector implements `IAuthProtocol` and
registers the ordinary named ASP.NET scheme `atproto` plus its metadata controller
through one `AtprotoAuthModule`. No AT branch is added to the generic controller.

The native dependency is CarpaNet.OAuth/CarpaNet 1.1.0-alpha.5, whose package NuSpec
names source commit `a24d54bf6a9ce3bbf7c1961d37ab099abe1d1a65`. The SDK supplies
identity parsing/resolution, PKCE, PAR, token exchange, DPoP key generation/proofs,
nonce storage and token refresh. The adapter adds the checks
and lifecycle required to fit the AT profile to ASP.NET sign-in.

DNS TXT resolution uses maintained DnsClient 1.8.0 over TCP via the SDK resolver
hook, with matching answer names; the SDK's raw UDP parser is not used. Revocation
uses SDK DPoP proof generation and nonce state, includes the required client_id,
checks the provider response and retains local state on failure. This corrects
the pinned SDK's best-effort revoke method, which otherwise hides rejection.
The adapter rejects SDK non-PAR fallback and enforces the configured permission
ceiling on both staged token responses and restored/refreshed sessions. Callback
grants must additionally contain every exact scope in the protected request;
downscoped grants are rejected before either durable or cached session replacement.
An ordinary default challenge reuses an existing broader durable scope set only
after the same DID/PDS/issuer/client and current declared ceiling validate;
explicit scope challenges retain their exact requested set.

1. The standard challenge receives a handle/DID and AuthenticationProperties.
   The remote handler creates its browser correlation cookie and protects those
   properties separately from SDK state.
2. Guarded discovery resolves the DID/PDS/authorization issuer. A local handle map
   is used only under the explicit development gate and requires the reverse DID
   binding. A public handle uses normal SDK resolution.
3. SDK state contains the expected DID, PDS, issuer, requested scope and protected
   browser properties. SDK PKCE/PAR creates the authorization URL.
4. Callback processing validates browser correlation before exchanging a code.
   It requires one state and issuer, exact issuer equality, expected token subject,
   and a fresh subject/PDS/issuer resolution.
5. SDK session writes go to `VerifiedSessionStore` until those checks and exact
   requested-scope coverage pass. A missing requested permission produces a
   sanitized HTTP 400 correction without clearing the previous session. Only
   then does a per-DID gate commit the protected session and replace its cached
   client. This is the same gate used by request/refresh and disconnect, preventing
   an old in-flight refresh from overwriting a newly authorized session.
6. The principal carries protocol DID/optional verified handle claims, links the
   existing external-identity store, and enters Koan's cookie lifecycle. No OAuth
   credential is placed in AuthenticationProperties tokens.

The protected file store atomically consumes one-use state and replaces the
encrypted document. Its process lock makes its single-host guarantee explicit.
Restoration checks subject, PDS, issuer and configured client ID again. PDS origin
comparison permits the SDK's equivalent trailing slash after refresh; callback
issuer comparison remains exact.

`AtprotoHttp` checks scheme and origin on every outgoing request and resolves DNS
inside `SocketsHttpHandler.ConnectCallback`. It rejects private, loopback,
link-local, documentation, multicast, mapped and selected translation addresses,
then connects the specific vetted address while the standard handler retains TLS
hostname verification. Public destinations require HTTPS/default port. Redirects
and ambient proxies are disabled. Responses and total request time are bounded.
The initial conservative address policy is not a general networking framework.
Exact development origins are allowed only by the named environment gate.

The development PLC selector applies only to explicit `DevelopmentHandles` keys
or DID values and is used consistently for challenge, callback, restore and DID
document lookup. Unknown/public identities use `PlcDirectory`; lookup failure
never triggers a fallback to another directory. `DevelopmentConnectHost` affects
socket DNS only for an exact allowed development origin; public destinations keep
their original connection host and address checks. Production configuration
rejects both settings. These hooks let one container use disposable loopback
fixtures while continuing to resolve public identities normally.

The SDK's raw `ATProtoOAuthClient.HttpClient` is not exposed: it bypasses this
transport. Guarded XRPC composes SDK DPoP requests and sends them through the same
connection owner. Namespace syntax cannot supply a separate URL; destination
comes from the verified session. A server action still owns whether it may use a
particular DID's grant. This service is not an HTTP impersonation endpoint.

Evidence boundaries:

- S01 outside this module proved real OAuth, callback replay denial, native
  cross-PDS Spaces transport, and real refresh after a process restart.
- This module's focused tests exercise address guards, configuration corrections,
  development exceptions, protected state consumption, staging, permission ceilings,
  requested-grant completeness and mandatory PAR. Thirty checks pass; the destructive provider lifecycle test
  is skipped unless explicitly enabled. The generic sample additionally passed
  real correlation/issuer/state/replay, cookie sign-in and actual process-restart
  checks. Redacted receipts are in `evidence/`.
- The opt-in local-provider lifecycle proof opens the stopped sample's protected
  session using its normal key ring. It adjusts local expiry metadata to cause a
  real refresh, verifies rotation of both tokens and authenticated `getSession`,
  then proves checked revocation makes the retained refresh token fail with
  `invalid_grant`. Explicit provider authentication/consent and restored refresh
  after reauthorization also passed. It never exports credentials.
- No production certification, multi-node session guarantee, confidential-client
  lifetime, A2A/MCP/WebMCP support, or Spaces repository verification is implied.
- The public repo MST parser in this SDK cannot verify the current experimental
  Spaces commitment/index format. That is a separate protocol data concern.

Follow-up SDK contributions should cover callback issuer/token-subject checks and
binding validation explicitly. This adapter must not remove its guards merely
because a future SDK adds similarly named methods; rerun the negative evidence.

Server-first entry (2026-09-12): a challenge without `identifier` starts at
`https://bsky.social`. This follows https://atproto.com/specs/oauth#identity-authentication:
bind issuer, omit login_hint, then resolve token subject to DID -> PDS -> issuer.
CarpaNet alpha.5 incorrectly adds URL inputs as login hints and retains the entryway
as its token audience. The connector uses its PKCE/DPoP primitives for the initial
mandatory PAR and lets the staged session store resolve/check the subject and set
the actual PDS before SDK client construction. Existing correlation, one-use state,
permission ceilings, callback issuer binding and guarded transport remain in force.
The focused ProtocolGuards run has 36 passing cases, including a real loopback PAR
nonce exchange with no login hint and negative subject/issuer/PDS binding checks.
No completed public-provider callback is claimed by that fixture.
