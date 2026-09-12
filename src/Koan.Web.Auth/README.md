# Sylin.Koan.Web.Auth

Koan's external sign-in runtime for ASP.NET Core. Reference a provider connector, supply credentials, and
`AddKoan()` compiles the provider plan, registers maintained OAuth2/OIDC handlers, maps the framework endpoints, and
reports the result at startup.

## Install

Use a connector when one matches your provider; it brings Web Auth transitively:

```powershell
dotnet add package Sylin.Koan.Web.Auth.Connector.Google
```

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

No authentication-specific registration or middleware call is required:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKoan();
var app = builder.Build();
await app.RunAsync();
```

Register `https://your-app/auth/google/callback` with Google. Start sign-in at
`GET /auth/google/challenge?return=/`.

## Meaningful behavior

- `GET /.well-known/auth/providers` returns eligible providers only.
- `GET /auth/{provider}/challenge` invokes the provider's ASP.NET handler. Built-in OAuth2/OIDC flows use PKCE.
- `/auth/{provider}/callback` is consumed by the corresponding ASP.NET authentication handler.
- `GET /me` projects the current cookie principal; `GET|POST /auth/logout` removes the local session.
- Explicitly configured providers outrank automatic local defaults. `PreferredProviderId` selects among eligible
  providers.
- Startup logs and Koan composition facts report provider state, eligibility, default election, reason, and correction
  without exposing credentials.

Web Auth can also run a configuration-only OIDC/OAuth2 provider. No generic connector package is needed; set `Type`,
provider endpoints or `Authority`, `ClientId`, and `ClientSecret` under `Koan:Web:Auth:Providers:{id}`.

## Claims and role refresh

Provider-owned claims and application-owned role decisions stay distinct. Web Auth maps the provider's
roles to `ClaimTypes.Role` and its extra claims one-for-one; it does not define, normalize, or rank a
role vocabulary, so a role means exactly what the application's authorization declares. Refreshing
application-owned role claims belongs to the shared sign-in flow: an `IKoanAuthFlowHandler` may stamp or
strip role claims on the mutable sign-in identity and re-check them during cookie-principal validation. An
optional built-in handler applies an email-keyed allow/revoke file at sign-in
(`Koan:Web:Auth:Lifecycle:RoleListFile`); an empty file path, the default, disables it.

## Guarantees and failure posture

- Connector references declare availability; they do not silently enable an unconfigured real provider.
- Explicit but incomplete provider intent fails startup with the exact missing fields and configuration path.
- Custom protocol connectors participate in the same plan, discovery, facts, and election; an eligible provider with
  no registered ASP.NET handler stops startup with a correction naming the missing scheme.
- Unknown or ineligible `PreferredProviderId` values fail startup instead of silently selecting something else.
- Missing external subject identifiers, identity-link persistence failures, and security-bearing lifecycle-handler
  failures reject the sign-in flow.
- Cookie validation failures reject the principal. Sign-out cleanup alone is best-effort.

## Boundaries

- Includes OAuth2 and OIDC interactive sign-in. Other protocols require a connector providing both `IAuthProtocol`
  configuration validation and a standard ASP.NET handler; declaring a `Type` alone does not add protocol mechanics.
  No SAML handler is included.
- Web Auth signs users into this application; `Sylin.Koan.Web.Auth.Server` is the separate opt-in capability that
  issues OAuth tokens to clients.
- Referencing `Sylin.Koan.Web.Auth` alone does not add a simulated provider. Use
  `Sylin.Koan.Web.Auth.Connector.Test` for local OAuth/OIDC flows.
- Secrets are ordinary configuration values today; use your deployment platform's configuration provider. A
  `SecretRef` indirection is not implemented.

See [TECHNICAL.md](TECHNICAL.md) and the public
[authentication guide](../../docs/guides/authentication-setup.md).
