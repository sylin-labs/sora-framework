# AT Protocol sign-in

Reference `Sylin.Koan.Web.Auth.Connector.Atproto` and keep `builder.Services.AddKoan()`.
This source contribution adds native AT OAuth as a public client using CarpaNet.OAuth
1.1.0-alpha.5. It is not yet a published-package or production-certification claim.

Keep `PlcDirectory` at the public default when accepting public accounts. A mixed
development network can set `DevelopmentPlcDirectory` for only the handles/DIDs
explicitly listed in `DevelopmentHandles`. For Docker Desktop, set
`DevelopmentConnectHost` to `host.docker.internal` to reach the exact origins in
`DevelopmentAllowedOrigins` through the host. This changes only the socket
destination; issuer URLs, HTTP Host, TLS identity and DPoP audiences stay intact.
Neither setting is accepted outside Development. Failed discovery or PAR returns
a sanitized HTTP 400 retry page, without exposing the provider response.

Configure the public metadata URL:

```json
{
  "Koan": { "Web": { "Auth": {
    "Providers": { "atproto": {
      "ClientId": "https://your-app.example/auth/atproto/client-metadata.json",
      "Scopes": ["atproto"]
    }},
    "Atproto": { "SessionDirectory": "/private/persistent/atproto" }
  }}}
}
```

Serve that HTTPS metadata URL publicly and keep the callback at
`https://your-app.example/auth/atproto/callback`. The connector derives both from
trusted configuration, never the incoming Host header. This first connector uses
the public OAuth client profile, with no client secret or confidential-client key;
provider session lifetime limits still apply.

Send the browser to `/auth/atproto/challenge?return=<path>` for Bluesky account
selection on its own authorization page. Keep
`/auth/atproto/challenge?identifier=<handle-or-DID>&return=<path>` as the alternative
for other providers or a specific account.
Koan validates the return destination and creates its ordinary authentication
cookie after the connector verifies correlation, issuer, account DID and PDS.
Use `AtprotoClaimTypes.Did` for protocol identity even if a local identity module
reconciles `ClaimTypes.NameIdentifier`. `AtprotoClaimTypes.Handle` is optional and
is emitted only after bidirectional handle verification. Neither email nor a
Bluesky profile is requested.

Server-first sign-in binds the authorization server before the account is known.
The returned DID is resolved to its PDS and authorization server before accepting
the session. An identity-only sign-in keeps an existing validated broader local
grant rather than overwriting room access with the new identity-only grant.

## Request additional access deliberately

For this connector, `Providers:atproto:Scopes` declares the client-metadata upper
bound. A first ordinary sign-in requests only `atproto`. When a validated durable
session for the same DID/PDS/issuer/client already has a broader grant within
the current ceiling, ordinary sign-in requests that established grant again.
A trusted server action requests additional exact declared scope strings:

```csharp
var properties = new AuthenticationProperties { RedirectUri = "/approved-return" };
AtprotoChallenge.WithScopes(properties, "atproto", "your-exact-declared-scope");
return Challenge(properties, "atproto");
```

Validate the return destination in an application-owned challenge action. Raw
query-string scopes do not change permissions. The requested set must include
`atproto` and be an exact subset of the declared strings. Declare separate
ordinary and management permission strings; request management only from the
server action authorized to establish that grant. OAuth scopes grant PDS access,
not application administration roles.

The callback requires every requested scope in the returned token grant and
rejects any scope outside the request or current declared ceiling. A provider
that silently omits requested access receives a sanitized HTTP 400 correction;
the previous durable session and cached client are retained. Refresh and
restoration check the current declared ceiling without requiring optional
permissions that were never requested. Explicit requested scopes always remain
exact. This first connector
accepts exact scope strings; it does not interpret permission-set expansion or
experimental Spaces scope defaults. Supply concrete resource scopes in their
provider-canonical form. For the pinned experimental Spaces provider, specify
the authority DID and collections explicitly: `authority=self` and omitted
collections are rewritten during token issuance and therefore fail the exact
grant check. Reauthorization is required when the client ID or permission ceiling
changes.

`AtprotoSessions` in the `.Protocol` namespace sends XRPC through the native
session using `Send(did, nsid, method, parameters, content, ct)`. It fixes the
destination to that DID's verified PDS, bounds responses, applies SDK DPoP and
refresh, and returns a disposable `HttpResponseMessage`. `Disconnect(did)` revokes
the upstream grant. Ordinary Koan logout clears only the local cookie.

For a protocol workflow that uses a different credential, `AtprotoHttp.Send(request, ct)`
reuses the same network guards while the caller owns headers and credential context.
`AtprotoSessions.ResolveDid(did, ct)` returns the SDK DID document after an exact
identifier check. Neither API exposes stored OAuth secrets or the raw HttpClient.

## Custody and development

Persist the session directory and the ASP.NET Data Protection key ring. Protocol
state and sessions are encrypted with that key ring; token/key material is not
stored in the browser cookie. Protect the key ring for the deployment account
(for example DPAPI on Windows or a managed encryptor in a service deployment).
A process-exclusive file lock rejects a second host using the same session
directory. This initial storage implementation is single-process; it does not
claim distributed refresh coordination.

`Koan:Web:Auth:Atproto` options are:

| Key | Default / meaning |
| --- | --- |
| `PlcDirectory` | `https://plc.directory` |
| `SessionDirectory` | `.koan/atproto`, relative to content root |
| `MaximumResponseBytes` | 4194304, maximum buffered protocol/XRPC response |
| `RequestTimeoutSeconds` | 30 |
| `DevelopmentAllowedOrigins` | Empty; exact HTTP(S) origins for a local protocol test network |
| `DevelopmentHandles` | Empty; explicit local DNS fixture map from handle to DID |

Development exceptions require `KoanEnv.Gate.DevelopmentOnly`; no production
override exists. A fixture handle still resolves through the real DID directory
and must appear in that DID document's `alsoKnownAs`. Normal public handles use
SDK DNS/HTTPS resolution. The special `http://localhost` client ID must contain
one IP-loopback `redirect_uri` with the canonical callback path and one `scope`
query value equal to the declared upper bound. See `samples/AtprotoIdentity`.

An inactive reference supplies availability without initiating network traffic or
opening session storage. Incorrect client/issuer/endpoint settings fail with a
corrective configuration message. Failed browser verification returns a generic
HTTP 400 without disclosing provider responses or token material.
