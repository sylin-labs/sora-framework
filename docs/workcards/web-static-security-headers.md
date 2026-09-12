# Static responses inherit Web security headers

**Task:** Correct the Web pipeline order so static/default-file responses receive the existing configured security headers.

**Application intent:** Serve the application's real HTML and assets with the same response protections as its API.

**Public expression:** Reference `Sylin.Koan.Web`, call `AddKoan()`, and supply an ordinary ASP.NET web root. Existing `Koan:Web:EnableSecureHeaders`, `IsProxiedApi`, and `ContentSecurityPolicy` retain their meanings. No extra application code or registration is required.

**Guarantee/correction:** When secure headers are enabled and the host is not a proxied API, default/static files and controllers receive the same existing header normalization. Explicit disabled/proxied settings remain effective. The application does not need a second middleware implementation.

**Complete intent surface:** No actions exist beyond the existing reference, `AddKoan()`, web root, and optional Web configuration. Static files remain optional for API-only hosts.

**Public concepts:** None added. Web options and ASP.NET middleware remain the complete vocabulary.

**Docs read:** `AGENTS.md` and `CLAUDE.md` assign pipeline composition to the Web pillar; `.codex/skills/explore/SKILL.md` requires this workcard before edits, with explicit authorization already supplied; `docs/engineering/README.md` requires executable evidence; `docs/architecture/principles.md` gives cross-cutting semantics one owner; root `README.md` establishes reference-first composition; Web `README.md` and `TECHNICAL.md` document static middleware and current options; the TOC has no separate static-header policy page needing a new entry.

**Code read:** `KoanWebStartupFilter` currently installs default/static files before the secure-header callback, so a successful file response short-circuits it. `KoanWebOptions` owns enable/proxy/CSP configuration. `KoanWebConstants` already owns every header name and value. `WebStartupHostOwnershipSpec` and `WellKnownWebApplicationFactory` establish a real `AddKoan()` TestServer with framework-owned routing and host cleanup. The test assembly serializes hosts.

**Reusing:** Existing options, constants, secure-header callback, Web startup filter, real TestServer pattern, and framework health controller. Explicit constants/options/DTO searches found no missing public part.

**Creating new:**

| Code | Location | Justification |
| --- | --- | --- |
| HTTP regression spec | `tests/Suites/Web/Koan.Web.WellKnown.Tests/StaticSecurityHeadersSpec.cs` | Exercises the existing Web startup owner with a real physical web root and HTTP responses. |
| This workcard | `docs/workcards/web-static-security-headers.md` | Records the bounded correction and executable evidence. |

**Coalescence:** Closest pattern: `KoanWebStartupFilter`. Specificity: Web pillar. Disposition: keep the existing normalization and move static/default-file registration after it. The same header contract and request lifetime apply to both consumers; Core or an app-specific middleware would be the wrong owner. No superseded type or alternate path remains.

**Ergonomics:** Application code, IntelliSense surface, and configuration branches do not grow. The middleware order makes the existing promise true for every served response.

**Constraints satisfied:** No new endpoint, inline route, public type, constant, option, environment gate, persistence path, or package-version edit. Update existing Web README/TECHNICAL text; no new ADR or TOC entry is needed for restoring documented behavior.

**Risks:** Header normalization now also applies to successful static responses by design. Preserve existing opt-outs and app-supplied values. Focused HTTP tests cover enabled, disabled, and proxied configurations. The Tangent auth contribution is independent and its postimages must remain unchanged.

**Validation:** On .NET SDK 10.0.401, the three-row `StaticSecurityHeadersSpec` first reproduced the defect: enabled headers were missing on `/`, while both opt-out rows passed. After the ordering correction, all three rows passed against real `/`, `/index.html`, and `/health/live` responses, asserting DENY, no-referrer, nosniff, configured CSP, and unchanged opt-outs. Run `dotnet test tests/Suites/Web/Koan.Web.WellKnown.Tests/Koan.Web.WellKnown.Tests.csproj --filter FullyQualifiedName~StaticSecurityHeadersSpec`.
