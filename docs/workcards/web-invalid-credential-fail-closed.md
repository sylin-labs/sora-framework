---
type: DEV
domain: web
title: "Rejected credentials cannot inherit anonymous Entity authority"
audience: [framework-authors, maintainers, contributors]
status: resolved
last_updated: 2026-09-13
framework_version: v1.0.0
validation:
  date_last_tested: 2026-09-13
  status: tested
  scope: selected-handler failure on public collection and keyed Entity reads; credential-free anonymous control
---

# Rejected credentials cannot inherit anonymous Entity authority

**Task:** Preserve the difference between no credential and a credential rejected by the selected ASP.NET authentication handler, and fail the shared Entity endpoint gate closed for the latter.

**Application intent:** An Entity read declared for `Access.Anyone` is public to a caller who supplies no credential; a caller who presents an invalid or revoked credential receives 401 and never reaches Entity data access as anonymous.

**Public expression:** `[Access(read: Access.Anyone, write: Access.Authenticated, remove: Access.Authenticated)]` on the Entity plus its existing `EntityController<TEntity>` and selected ASP.NET authentication scheme. No application middleware, header guard, option, or registration is added.

**Guarantee/correction:** `AuthenticateResult.Fail` from the selected default authentication handler is captured after `UseAuthentication`, translated into internal `EntityRequestContext` state, and challenged by the shared `EntityEndpointService` before hooks or persistence. `AuthenticateResult.NoResult` remains a legitimate anonymous request. Collection and by-id reads have the same result.

**Complete intent surface:** No user action exists beyond the current package reference, `AddKoan()`, access declaration, and ordinary authentication configuration.

**Public concepts:** None. The correction reuses ASP.NET Core's authentication result feature, Koan's request context, and the existing `AuthorizeDecision.Challenge` result.

**Docs read:** `docs/engineering/README.md` assigns certification to executable workbooks; `docs/architecture/principles.md` assigns Web policy to one pillar chokepoint; root `README.md` establishes `EntityController<T>` as the application grammar; `docs/toc.yml` identifies the current authentication guide; `docs/guides/authentication-setup.md` owns authentication and Entity access guidance; `SEC-0004` establishes `EntityEndpointService` as the shared cross-surface authorization owner.

**Code read:** `KoanWebStartupFilter` owns authentication middleware ordering; `EntityRequestContextBuilder` and `EntityRequestContext` translate transport state into the shared invocation; `EntityEndpointService.Gate` runs before collection and keyed reads; `EntityController` already translates a challenge to 401; the Web Extensions TestServer fixture is the closest real-host regression seam.

**Reusing:** `IAuthenticateResultFeature`, the selected ASP.NET default scheme, `EntityRequestContext`, `AuthorizeDecision.Challenge`, and the existing pre-read gate. Explicit constant, options, and contract searches found no missing configuration or public DTO.

**Creating new:**

| Code | Location | Justification |
| --- | --- | --- |
| Selected authentication-result capture middleware | `src/Koan.Web/Middleware/EntityAuthenticationResultMiddleware.cs` | Records failure immediately after the framework-owned `UseAuthentication` stage. |
| Internal request-context rejection state | `src/Koan.Web/Endpoints/EntityRequestContext.cs` and builder | Carries the transport fact to the protocol-neutral shared endpoint. |
| HTTP regression coverage | Existing Web Extensions fixture and projection spec | Proves invalid Bearer rejection and credential-free anonymous access for collection and keyed reads. |
| Current guidance | `docs/guides/authentication-setup.md` | Makes the restored security guarantee discoverable without adding a parallel guide. |

**Coalescence:** The closest pattern is MCP's explicit edge authentication, which rejects a failed bearer before shared dispatch. Keep that resource-specific path; absorb the general selected-handler result at the Koan.Web authentication boundary and enforce it at the existing shared Entity gate. Core is too wide because the signal is ASP.NET-specific; a controller/header guard is too narrow because it would fork REST from the shared authorization law. No application workaround becomes framework surface.

**Ergonomics:** Application code and configuration do not change. Human readers and coding agents continue to infer public access from `[Access]`, with the framework internally preserving the ordinary security distinction between absent and rejected credentials.

**Constraints satisfied:** No inline endpoint, route, option, configuration key, magic credential string, environment branch, persistence change, or public authoring type. Rejection precedes data access. The existing guide is updated; no new TOC entry or ADR decision is required.

**Risks:** ASP.NET Core's authentication middleware exposes successful default authentication but does not retain failed/no-result outcomes in `IAuthenticateResultFeature`. The follow-on middleware therefore asks the selected default scheme for its per-request cached result. Explicit non-default resource schemes remain edge-owned and must reject before dispatch, as MCP already does.

**Validation:** The exact keyed-read reproducer first failed 200-vs-401, then passed after the correction. `EntityProjectionE2ESpec` passed 8/8, including collection and credential-free controls; the full Web Extensions suite passed 118/118, WellKnown passed 142/142, and Web Auth passed 57/57. The Release build/docs/lockfile/skills/AOT ratchet passed every leg with tests skipped because the focused suites were run separately.
