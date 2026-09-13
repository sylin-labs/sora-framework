---
type: GUIDE
domain: web
title: "Authentication Setup with Koan"
audience: [developers, security-engineers, ai-agents]
status: current
last_updated: 2026-09-10
framework_version: v1.0.0
validation:
  date_last_tested: 2026-09-09
  status: verified
  scope: provider-plan and scheme unit tests, local OAuth/OIDC HTTP tests, synthetic custom-protocol HTTP composition tests
related_guides:
  - auth-howto.md
  - authorization-howto.md
  - oauth-server-howto.md
---

# Authentication Setup with Koan

Koan treats authentication as a composed capability: the package reference states intent, configuration supplies
deployment-specific values, and `AddKoan()` owns the mechanics. Applications do not register schemes, middleware,
callbacks, or provider services themselves.

## Shortest meaningful local path

Add the local provider:

```powershell
dotnet add package Sylin.Koan.Web.Auth.Connector.Test
```

Keep the application bootstrap ordinary:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKoan();

var app = builder.Build();
await app.RunAsync();
```

In Development, visit:

```text
/auth/test-oidc/challenge?return=/
```

Choose a subject, roles, permissions, and custom claims. The simulator completes a real OIDC code flow through the
same ASP.NET handler, application cookie, identity-link store, and lifecycle pipeline used by deployment providers.
`GET /me` shows the current projection. `GET /.well-known/auth/providers` lists the eligible providers.

The Test connector is not built into `Sylin.Koan.Web.Auth`; it activates only when referenced. It is a local protocol
simulator and must not be enabled in production.

## Add a deployment provider

For Google, reference the connector:

```powershell
dotnet add package Sylin.Koan.Web.Auth.Connector.Google
```

Supply only the deployment values the connector cannot know:

```json
{
  "Koan": {
    "Web": {
      "Auth": {
        "Providers": {
          "google": {
            "ClientId": "{GOOGLE_CLIENT_ID}",
            "ClientSecret": "{GOOGLE_CLIENT_SECRET}"
          }
        }
      }
    }
  }
}
```

Register `https://your-app/auth/google/callback` in Google. Start sign-in with
`GET /auth/google/challenge?return=/`.

Microsoft and Discord follow the same shape using
`Sylin.Koan.Web.Auth.Connector.Microsoft` and `Sylin.Koan.Web.Auth.Connector.Discord`. Their IDs are `microsoft` and
`discord`; their callbacks are `/auth/microsoft/callback` and `/auth/discord/callback`.

Referencing a real connector only makes the capability available. It remains inactive until complete explicit
configuration exists, so adding a package cannot unexpectedly replace a working local login. Once configured, explicit
intent outranks automatic local providers.

## Configuration-only OIDC or OAuth2

The protocol engines live in Web Auth, so a generic connector package is unnecessary. Reference
`Sylin.Koan.Web.Auth` and configure a provider ID directly:

```json
{
  "Koan": {
    "Web": {
      "Auth": {
        "Providers": {
          "corporate": {
            "Type": "oidc",
            "DisplayName": "Corporate SSO",
            "Authority": "https://identity.example.com",
            "ClientId": "{CLIENT_ID}",
            "ClientSecret": "{CLIENT_SECRET}",
            "Scopes": [ "openid", "profile", "email" ]
          }
        }
      }
    }
  }
}
```

OIDC requires `Authority`, `ClientId`, and `ClientSecret`. OAuth2 requires `AuthorizationEndpoint`, `TokenEndpoint`,
`UserInfoEndpoint`, `ClientId`, and `ClientSecret`. SAML is not supported.

## Protocol-specific connectors

A connector for another protocol uses the same discovery, challenge route, provider election, runtime facts, and
application cookie. Reference the connector and follow its configuration contract; changing `Type` to an unsupported
protocol does not supply a handler. Startup names the missing connector or named scheme when protocol intent cannot
be realized. OAuth2 and OIDC remain built into Web Auth.

Connector authors register an `IAuthProtocol` validator alongside their `AuthProviderDefinition` and normal ASP.NET
authentication scheme inside the connector module. The validator receives the merged `ProviderOptions`; it must
honor those values or return corrections with full setting paths. It cannot silently ignore a configured callback or
scope. The framework keeps eligibility, election, and cookie policy; the connector owns remote protocol validation,
identity linkage, and any credentials or tokens needed for that protocol. See the
[Web Auth technical contract](../../src/Koan.Web.Auth/TECHNICAL.md) for this contribution boundary.

## Multiple providers and default election

Every eligible provider appears in discovery and can be challenged by ID. Default election is deterministic:

1. an eligible `PreferredProviderId`, when specified;
2. explicit application configuration before automatic local defaults;
3. provider priority;
4. stable provider ID.

```json
{
  "Koan": {
    "Web": {
      "Auth": {
        "PreferredProviderId": "microsoft"
      }
    }
  }
}
```

A preferred provider is required intent. Unknown, disabled, unavailable, or incomplete choices stop startup with a
correction instead of silently selecting something else.

## Protect application behavior

Use stock ASP.NET authorization on custom controllers:

```csharp
[ApiController]
[Route("api/reports")]
public sealed class ReportsController : ControllerBase
{
    [Authorize(Roles = "admin")]
    [HttpPost("rebuild")]
    public IActionResult Rebuild() => NoContent();
}
```

Use Koan's gate/constrain/project model for entity surfaces:

```csharp
[Access(read: "authenticated", write: "is:editor", remove: "is:admin")]
public sealed class Article : Entity<Article>
{
    public string Title { get; set; } = "";
}

public sealed class ArticlesController : EntityController<Article>;
```

`Access.Anyone` means no credential is required; it does not make a rejected credential anonymous. When the selected
ASP.NET authentication handler returns a failed result for a supplied credential, Koan Entity endpoints return 401
before reading or mutating data. A request for the same public read with no credential remains anonymous and allowed.

External roles map to `ClaimTypes.Role`; permissions from the local simulator map to `Koan.permission`. Koan maps
these claims but does not define or normalize a role vocabulary: a role means what the application's authorization
declares, and refreshing application-owned roles happens in an `IKoanAuthFlowHandler` at sign-in and
cookie-principal validation. Bearer tokens use their separate validation scheme. Row-level ownership
and agent grants are authorization concerns, introduced by
[Identity and isolation](../reference/identity/index.md).

## Built-in application endpoints

- `GET /.well-known/auth/providers` — eligible provider discovery;
- `GET /auth/{provider}/challenge?return=/path` — begin sign-in;
- `/auth/{provider}/callback` — handler-owned callback registered with the provider;
- `GET /me` — current-user projection, or 401;
- `GET|POST /auth/logout?return=/path` — local sign-out.

Return URLs are checked against Web Auth's return-url policy before redirect. Do not create a second callback controller
or manually parse provider tokens.

## Startup failures and inspectability

The provider plan is compiled once per host. Startup logs show total providers, eligible count, elected default, and
reason. Koan composition facts add one credential-free observation per provider plus the election receipt; public
discovery returns eligible providers only.

Explicit incomplete providers fail startup and name the missing fields and exact configuration path. Missing external
subject identifiers, identity-link persistence failures, and authentication lifecycle failures reject sign-in. Cookie
validation failures reject the principal. Only sign-out cleanup handlers are best-effort.

Secrets are standard .NET configuration values. Supply them through your environment or configuration provider; Koan
does not currently resolve a `SecretRef` abstraction or rotate provider credentials.

## Extending the lifecycle from a module

A module author references `Sylin.Koan.Web.Auth.Abstractions` and implements only the events it owns:

```csharp
public sealed class SuspendedUserGuard : IKoanAuthFlowHandler
{
    public Task OnSignIn(AuthSignInContext context, CancellationToken ct)
    {
        if (context.Identity.HasClaim("account_status", "suspended"))
            context.Reject("account is suspended");
        return Task.CompletedTask;
    }
}
```

The implementation is discovered automatically and runs as a scoped service. Lower `Priority` values run first. A
module should not reference functional Web Auth only to consume these contracts.

## Issuing tokens is a separate capability

External providers sign a user into this application. To issue OAuth tokens to clients, reference the opt-in
`Sylin.Koan.Web.Auth.Server` package. It activates its `/oauth/...` authorization-server surface under `AddKoan()`;
see the [OAuth server guide](oauth-server-howto.md). It is not an external login provider.

## Current boundaries

- OAuth2 and OIDC interactive code flows are supported; SAML is not.
- Provider-console registration, redirect URLs, consent, tenant allow-lists, and secret rotation are deployment duties.
- Third-party access/refresh tokens are not exposed as a general application token store.
- The local Test provider is single-process and in-memory; restarting the application clears its protocol state.
