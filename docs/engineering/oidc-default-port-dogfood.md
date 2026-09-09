---
type: ANALYSIS
domain: framework
title: "Default-port local OIDC issuer compatibility"
audience: [maintainers, ai-agents]
status: archived
last_updated: 2026-09-09
framework_version: v1.0.0
validation:
  status: reviewed
  scope: historical dogfeeding evidence classification; original execution claims retained
---

# Local OIDC issuer consistency

**Task:** Complete real sign-in through Gposingway's existing local Docker port mapping.
**Application intent:** Reference the Test connector and use the standard OIDC challenge/callback.
**Public expression:** Existing package reference and `AddKoan()`, with no extra host or issuer setting.
**Guarantee/correction:** The local provider advertises and issues exactly the same issuer when a default
port is explicit on one request and implicit on another. Signature, nonce, audience and exact issuer
validation remain enforced. Real external provider issuers are untouched.
**Complete intent surface:** No new configuration or public types.
**Public concepts:** Existing relative local authority and separate browser/internal transport origins.

**Docs read:** Root README/principles, Web Auth README/TECHNICAL, Test connector README/TECHNICAL and
release workbook. They assign protocol orchestration to Web Auth and token issuance to the Test connector.
**Code read:** RequestHostOidcConfigurationManager, AuthSchemeSeeder, AuthorizeController, OidcDiscoveryController,
AuthSwapFixture and AuthEngineSwapSpec. Public origins are interpolated from Request.Host on separate hops.
**Reusing:** Standard Uri authority canonicalization, existing Kestrel auth fixture, maintained OIDC validation.
**Creating new:** Default-port regression in the existing real callback journey. No new runtime mechanism.
**Coalescence:** Repair origin construction at the two existing owners; do not relax token validation.
**Ergonomics:** Default HTTP/HTTPS ports require no application workaround.
**Constraints satisfied:** No production connection, new app service, persistent test stack or public API.
**Risks:** Only local relative issuers can be canonicalized. External OIDC issuers must keep their exact text.

The copied-data app starts and OAuth2/collections/likes pass. The OIDC callback fails because the browser
authorization request issues `http://localhost/.testoauth`, while a redirect's explicit `:80` makes the
callback expect `http://localhost:80/.testoauth`. Characterize this before implementation.

The added full callback regression fails before correction. Standard Uri authority construction at the
existing manager, discovery and authorization boundaries fixes it without changing validation. All six real
OAuth/OIDC integration cases and all 46 Web Auth unit cases pass, including HTTP/HTTPS default ports,
nondefault ports and IPv6. The browser host still remains separate from internal token/discovery transport.
