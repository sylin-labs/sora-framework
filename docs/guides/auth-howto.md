---
type: GUIDE
domain: web
title: "Authentication & Identity How-To"
audience: [developers, architects, security-engineers]
status: current
last_updated: 2026-09-10
framework_version: v1.0.0
validation:
  date_last_tested: 2026-07-18
  status: verified
  scope: Auth fabric covered by Koan.Security.Trust.IntegrationTests (HTTP e2e)
related_guides:
  - authentication-setup.md
  - authorization-howto.md
  - ../reference/web/index.md
---

# Koan Authentication & Identity: From Public to the Fleet

This guide walks you through Koan's identity story—from a public-by-default app, to logging in as any profile you like in dev, to roles, real logins, service-to-service tokens, and what changes in production. Think of it as a conversation with a colleague who's wired this up a few times: we'll start with the simplest thing that works and add one idea at a time.

A note on scope. This guide is about **identity**—*who is making this request?* Its sibling, the [Authorization How-To](authorization-howto.md), is about **authorization**—*what may they do?* We'll hand off cleanly when we get there. For the supported OAuth2/OIDC provider configuration, see [Authentication Setup](authentication-setup.md).

---

## 0. The thirty-second version: public by default, log in when you want a profile

Reference = intent. Add the local OAuth/OIDC simulator; it brings Web Auth transitively:

```xml
<PackageReference Include="Sylin.Koan.Web.Auth.Connector.Test" />
```

Boot the runtime as usual—no auth configuration:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKoan();
var app = builder.Build();
await app.RunAsync();
```

Run it in Development and `GET /me`: the response is **401** because the fresh application is anonymous. That's the
point: nothing is auto-signed-in.

To become someone, start the automatic local OIDC provider:

```text
GET /auth/test-oidc/challenge?return=/
```

The browser lands on the connector's login page, where you set a **subject, roles, permissions, and claims**. Submit
and the maintained OIDC callback establishes a real signed session; `GET /me` now returns its projection.
`GET /.well-known/auth/providers` is the machine-readable eligible-provider list.

> **Mentor note.** A framework that auto-signs you in as an admin is convenient for a toy and wrong for a real app.
> The default is anonymous, and logging in walks the actual session path. The local connector is automatic only in
> Development and is not a production identity system.

---

## 1. Who am I? `Identity.Current`

`Koan.Security.Trust.Identity.Current` is the ambient identity for the current request—available in a controller, a service, or a background job, with no `HttpContext` plumbing:

```csharp
if (!Identity.Current.IsAuthenticated)
    return; // anonymous

var who   = Identity.Current.Id;        // the subject (e.g. "dev", "alice", a service name)
var roles = Identity.Current.Roles;     // the roles carried on the credential
if (Identity.Current.Is("admin")) { /* ... */ }
```

Whether the caller arrived with a browser cookie, a service bearer token, or a `?_as=` persona, they all land in the same place. You read identity one way.

---

## 2. Protecting an endpoint

Identity earns its keep the moment you guard something. Use the standard ASP.NET attribute:

```csharp
[Authorize]
[HttpGet("/orders")]
public IActionResult MyOrders() => Ok(/* ... */);
```

By default a request is anonymous, so `[Authorize]` denies it until you log in (§0) or step into a persona (§3) — the same behaviour you'll see in production, with no synthetic admin papering over it. What "denied" looks like over HTTP is content-negotiated:

> **Mentor note.** What does "challenged" look like over HTTP? Koan's cookie scheme is **content-negotiated**.
> A browser navigation is redirected to your login page (302), but an **API request** — one sending
> `Accept: application/json`, an `X-Requested-With: XMLHttpRequest` header, or hitting a path under `/api` —
> gets a clean **401** (or **403** when authenticated-but-unauthorized) instead of a redirect, so a SPA `fetch`
> has something it can act on. Tune the heuristic (extra API path prefixes, etc.) under `Koan:Web:Auth:Challenge`.

---

## 3. Stepping into a persona — the `?_as=` quick override

The login page (§0) is how a *person* signs in. For **scripted / automated** testing there's a faster path: `?_as=` puts you into a **transient** persona for a single request — no page, no cookie.

| Request | You are… |
|---|---|
| `GET /me` | **anonymous** (the default) |
| `GET /me?_as=alice` | `alice` (role `admin` by default) |
| `GET /me?_as=alice&_roles=editor,reviewer` | `alice` with roles `editor`, `reviewer` |
| `GET /me?_as=anonymous` | anonymous (explicit) |

> **Mentor note.** This is how you exercise authorization across users without standing up accounts. Want to prove an editor can't approve their own draft? Drive the request `?_as=author&_roles=author` and assert the 403. It's Development-only and transient — perfect for an integration test that sweeps several profiles. (For a *real* clickable session, use the login page in §0.)

---

## 4. Roles — the coarse layer

Role checks are the first rung of authorization, and they read naturally:

```csharp
[Authorize(Roles = "admin")]
[HttpGet("/admin/reports")]
public IActionResult Reports() => Ok(/* ... */);
```

Prove both directions with personas:

- `GET /admin/reports?_as=alice&_roles=admin` → admin → **200**
- `GET /admin/reports?_as=bob&_roles=viewer` → authenticated, but not admin → **denied**

Roles are deliberately coarse—they belong *in the credential* (the cookie or token). The credential's roles come from
the identity provider's assertions or from the application's own `IKoanAuthFlowHandler` stamping them at sign-in;
Koan maps them to `ClaimTypes.Role` and does not define or normalize a role vocabulary. When you need decisions that
depend on the specific resource, the specific action, or external policy, that's §7.

---

## 5. Real logins — OAuth providers (and the dev TestProvider)

Your dev login (§0) comes from the referenced Test connector. Real *users* sign in through a deployment provider —
in Koan that is a connector reference plus configuration, not application bootstrap code.

**OAuth (Google, Microsoft, GitHub, …):**

```xml
<PackageReference Include="Sylin.Koan.Web.Auth.Connector.Google" />
```

```json
{
  "Koan": { "Web": { "Auth": { "Providers": {
    "google": { "ClientId": "{GOOGLE_CLIENT_ID}", "ClientSecret": "{GOOGLE_CLIENT_SECRET}" }
  } } } }
}
```

Complete configuration makes Google eligible; `/.well-known/auth/providers` lists it and
`/auth/google/challenge` starts the maintained flow. Configuration without a matching first-party connector is also
supported for generic OAuth2/OIDC. SAML is not supported. See [Authentication Setup](authentication-setup.md).

The **TestProvider** you logged in with at §0 is a referenced, self-hosted protocol simulator. It is available
zero-config in Development; to expose it in a controlled non-production test environment, opt in explicitly:

```json
{ "Koan": { "Web": { "Auth": { "TestProvider": { "Enabled": true } } } } }
```

> **Mentor note.** Two dev paths, two jobs. The TestProvider walks the real challenge → callback → cookie flow. The
> `?_as=` override is transient and no-click. `?_as=` is structurally Development-only; the Test connector is inactive
> outside Development unless explicitly enabled, and should never be enabled in production.

---

## 6. Workload bearer tokens

Browsers carry cookies; services carry **bearer tokens**. Koan validates inbound bearer tokens through a dedicated scheme, `Koan.bearer`. Opt an endpoint into it explicitly—so a service route requires a real token, regardless of any browser session:

```csharp
[Authorize(AuthenticationSchemes = KoanBearerDefaults.AuthenticationScheme)]
[HttpGet("/internal/sync")]
public IActionResult Sync() => Ok(/* ... */);
```

A caller presents `Authorization: Bearer <token>`; a missing, malformed, tampered, expired, foreign-key, or
wrong-algorithm token is **401**. Trust generates one random ES256 keypair per process, registers `Koan.bearer`
automatically under `AddKoan()`, and needs no signing secret. To mint a token inside that trust boundary, inject the
one issuer contract:

```csharp
public sealed class SyncTrigger(IIssuer issuer)
{
    public string TokenForBilling() => issuer.Issue(
        new TrustClaims { Subject = "billing-svc", Roles = ["service"] },
        audience: "koan://billing");
}
```

The default key is safe but ephemeral: tokens stop validating after a process restart. For public-client issuance,
restart continuity, rotating keys, and JWKS publication, reference `Koan.Web.Auth.Server`; it replaces the key store
with its persisted implementation outside Development. Trust does not currently discover remote JWKS or enroll a
fleet, so do not treat the direct package as zero-configuration cross-service identity.

The bearer scheme authenticates signature, issuer, algorithm, and lifetime. A resource-specific audience is an
authorization decision at that resource; MCP compares `aud` with its own canonical resource URI automatically.

---

## 7. Fine-grained authorization — the handoff

Roles answer "are you an admin?" Real systems eventually need "may *this* subject perform *this* action on *this* resource?"—soft-delete this record, approve that draft, query an external policy engine.

That's a seam of its own: `IAuthorize` and its capability-graded provider ladder. Rather than repeat it here, follow the sibling guide:

→ **[Authorization How-To](authorization-howto.md)**

The one-line bridge: **identity** (this guide) establishes *who you are*; **authorization** (that guide) decides *what you may do*. They meet at `Identity.Current`.

---

## 8. Going to production — fail-closed by construction

Two things change the moment the environment isn't Development, and you don't have to remember to do either:

1. **The dev conveniences are inactive.** No `?_as=` personas and no automatic TestProvider login. Real users authenticate through configured providers; services through `Koan.bearer`. Do not explicitly enable the Test connector in production.
2. **Token continuity is explicit.** Direct Trust uses a random per-process key and carries no forgeable default
   secret. If the deployment promises OAuth tokens that survive restarts or work across replicas, use Auth Server's
   durable Data/Data Protection-backed key store; Auth Server fails startup outside Development when it cannot provide
   that continuity unless the operator explicitly acknowledges the ephemeral posture.

> **Mentor note.** The design goal is a fail-closed default: dev conveniences are loud in Development and inactive
> elsewhere. Explicitly overriding those gates remains an operator decision and should be reviewed as such.

---

## 9. Testing your auth, end-to-end

Auth bugs love the seams *between* components—exactly where unit tests can't see. Koan ships an HTTP-level pattern for this: a fixture boots your app over an in-memory server, and you drive real requests, asserting status codes. You can mint bearer tokens via the app's *own* issuer (so the scheme accepts them) and step into personas with `?_as=`:

```csharp
[Fact]
public async Task Bearer_endpoint_rejects_a_missing_token()
{
    var res = await _fixture.CreateClient().GetAsync("/internal/sync");
    res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
}

[Fact]
public async Task Admin_route_allows_the_admin_dev_identity()
{
    var res = await _fixture.CreateClient().GetAsync("/admin/reports");
    res.StatusCode.Should().Be(HttpStatusCode.OK);
}
```

A complete runnable example lives in
`tests/Suites/Security/Koan.Security.Trust.IntegrationTests`; it exercises the composed development
identity and authorization boundary.

---

## Where to go next

- **What may they do?** → [Authorization How-To](authorization-howto.md)
- **Provider OAuth2/OIDC reference** → [Authentication Setup](authentication-setup.md)
- **Building the API around it** → [Web reference](../reference/web/index.md)
