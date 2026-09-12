# Tangent AT authentication protocol seam

Status: implemented and locally validated; reduced contract approved by coordinator; not published.

Prepared 9 September 2026 against `e07a84cc3f71a0867f1122b03b723cc80727e772` in the isolated Tangent worktree. This card owns only the generic Web Auth seam. The AT connector, its native client/session implementation, and Tangent policy belong to subsequent work.

**Task:** Admit a connector-owned authentication protocol into Koan's existing provider plan and standard ASP.NET authentication scheme path without rewriting OAuth2/OIDC or creating another authentication runtime.

**Application intent:** A person can reference an AT sign-in connector, enter their account identifier, and receive the application's ordinary authenticated session while existing OAuth2/OIDC sign-in continues to work.

**Public expression:** The application still uses its existing Koan application/Web Auth packages and a real connector package, configures that connector's documented deployment values, and calls `AddKoan()` once:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKoan();
var app = builder.Build();
await app.RunAsync();
```

The application does not register a scheme, authentication middleware, callback route, or protocol validator. The new cross-module contract is for connector authors, not an application ceremony:

```csharp
// Implemented in this worktree; not yet a published API.
public interface IAuthProtocol
{
    string Protocol { get; }
    IReadOnlyList<string> Validate(string providerId, ProviderOptions options);
}
```

`Validate` is synchronous, composition-time configuration validation with no network I/O. An empty result means the effective configuration is complete; every nonempty entry is a secret-free corrective diagnostic identifying the relevant setting, including its full configuration path when it lives outside the ordinary provider overlay. It does not choose activation, priority, default provider, identity, or authorization. A connector may inject its own typed options into its validator. There is no `IServiceProvider` parameter or scheme-creation method on this contract.

The connector module registers its immutable provider definition, singleton validator, and standard ASP.NET named handler/options through ordinary DI and `AddAuthentication().AddScheme<TOptions,THandler>(providerId, ...)`. That is connector-owned registration; it never appears in application `Program.cs`. The connector's handler uses `Koan.cookie` as its sign-in scheme and the existing `/auth/{provider}/callback` convention. The generic seam fixture supplies those connector-shaped registrations locally in a real `AddKoan()` host, without an automatically discovered test module that would change the existing OAuth/OIDC fixtures' provider set. Its handler is deliberately synthetic and carries no AT dependency.

Existing `Koan:Web:Auth:Providers:<id>` configuration still overlays provider presentation/intent. Connector-specific options remain with that connector. This card introduces no published connector package ID and does not claim that generic AT deployment settings have already been implemented. The next connector workcard must name its exact package, configuration, persistent key/session storage, metadata/callback URLs, and network prerequisites before its production implementation.

**Guarantee/correction:** A known, configured custom protocol can participate in the same immutable availability/eligibility/default plan, discovery, facts, challenge route, and cookie lifecycle as the existing providers. An enabled explicit unknown protocol fails during plan compilation with a supported-protocol/connector correction. Incomplete contributed configuration fails before challenge. An eligible route lacking an actual ASP.NET scheme fails startup instead of advertising a nonexistent handler. Existing explicit/default-provider failure behavior stays intact. This does not certify the connector's protocol exchange or persistent sessions.

**Complete intent surface:** No new application action exists beyond referencing/configuring the actual connector and the existing `AddKoan()` bootstrap. A protocol that needs an account identifier reads its connector-owned query parameter directly from the initiating request in its standard authentication handler. The connector validates syntax, length, resolution and protocol meaning and binds the validated subject into its protected protocol state. It is never an identity claim merely because it appeared in the query. The generic controller, existing `return` allow-list, `prompt` handling and OAuth2/OIDC behavior remain unchanged. Connectors register named schemes before `AuthModule.Start`; adding an unrecognized Type to configuration alone cannot manufacture a custom protocol or handler. The effective, merged `ProviderOptions` remains the plan's authority; connectors must consume its scopes/callback settings or reject unsupported intent explicitly, never silently ignore overlays.

**Public concepts:**

- `IAuthProtocol`: one connector-author contract expressing configuration eligibility for a new protocol; kept in inert Auth.Abstractions because another module consumes it.
- Identifier query names and validation remain connector-owned; no new generic challenge property or controller forwarding concept is introduced.
- Ordinary ASP.NET `AuthenticationScheme`, options and handler: existing standard concepts reused for realization; no Koan substitute is introduced.
- No new application registry, factory, result hierarchy, builder, service locator, middleware, or credential abstraction.

**Docs read:**

- [AGENTS.md](../../AGENTS.md), [CLAUDE.md](../../CLAUDE.md), and [explore skill](../../.codex/skills/explore/SKILL.md) establish the mandatory ownership, constant, module, exploration, and evidence requirements; all apply.
- [Engineering index](../engineering/README.md) establishes instruction-first operational documentation and named failure/recovery guidance; relevant to the resulting auth guidance.
- [Architecture principles](../architecture/principles.md) places immutable eligibility at the pillar and mechanics at adapters, preferring standard .NET; directly determines this seam.
- [Documentation navigation](../toc.yml) already reaches authentication setup and identity reference; update the existing linked pages instead of creating a parallel user guide.
- [Root README](../../README.md) establishes package-reference intent, singular `AddKoan()`, and the distinction between availability and selected behavior; the application expression must preserve it.
- [Web Auth technical contract](../../src/Koan.Web.Auth/TECHNICAL.md) identifies the one provider plan, maintained handler path, cookie lifecycle and unsupported token-persistence boundary; directly relevant.
- [Authentication setup](../guides/authentication-setup.md) supplies current user configuration and configuration-only OAuth2/OIDC behavior; those paths are regressions to preserve.

**Code read:**

- [AuthProviderPlan](../../src/Koan.Web.Auth/Providers/AuthProviderPlan.cs) compiles activation, overlay, eligibility and default election; its hardcoded protocol validation/challenge eligibility is the current admission gate.
- [AuthSchemeSeeder](../../src/Koan.Web.Auth/Hosting/AuthSchemeSeeder.cs) realizes OAuth2/OIDC and already respects an existing named ASP.NET scheme; extend its terminal assurance, not its protocol engine collection.
- [AuthController](../../src/Koan.Web.Auth/Controllers/AuthController.cs) validates the return URL before issuing a named challenge; its handler receives the original initiating request, so no identifier forwarding edit is needed.
- [ServiceCollectionExtensions](../../src/Koan.Web.Auth/Extensions/ServiceCollectionExtensions.cs) owns the cookie and discovered auth lifecycle handlers; no cookie event replacement is required.
- [AuthModule](../../src/Koan.Web.Auth/Initialization/AuthModule.cs) resolves the plan and seeds schemes at startup before reporting the host's authentication story; preserve that lifecycle.
- [AuthProviderDefinition](../../src/Koan.Web.Auth.Abstractions/Providers/AuthProviderDefinition.cs), [ProviderOptions](../../src/Koan.Web.Auth.Abstractions/Options/ProviderOptions.cs), and [IAuthProviderCatalog](../../src/Koan.Web.Auth.Abstractions/Providers/IAuthProviderCatalog.cs) already supply provider metadata, overlay and credential-free output; reuse their roles.
- [AuthConstants](../../src/Koan.Web.Auth/Infrastructure/AuthConstants.cs) owns existing controller routes; no new route is needed.
- [AuthCompositionFacts](../../src/Koan.Web.Auth/Composition/AuthCompositionFacts.cs) reads the same plan; successful custom eligibility needs no second facts registry.
- [TestAuthModule](../../src/Connectors/Web/Auth/Test/Initialization/TestAuthModule.cs) contributes immutable provider definitions while Web Auth owns eligibility/reporting; its module pattern is the closest connector example.
- [ProviderCatalog](../../src/Koan.Core/Providers/ProviderCatalog.cs) supplies normalized, duplicate-rejecting, host-owned lookup; reuse it for protocol contributions without adding Core policy.
- [AuthProviderPlanTests](../../tests/Koan.Web.Auth.Tests/AuthProviderPlanTests.cs), [AuthEngineSwapSpec](../../tests/Suites/Auth/Koan.Web.Auth.Integration.Tests/AuthEngineSwapSpec.cs), and [AuthSwapFixture](../../tests/Suites/Auth/Koan.Web.Auth.Integration.Tests/AuthSwapFixture.cs) supply focused plan tests and real loopback OAuth2/OIDC flows; extend rather than replace them.

**Reusing:**

The required explicit searches were run over `src/` and `samples/`, with displayed results narrowed to authentication:

```text
rg "\bconst\b" src/ samples/ -g "*.cs"
rg "\bConstants\b" src/ samples/ -g "*.cs"
rg "\bOptions\b" src/ samples/ -g "*.cs"
rg "record|class .*Dto|class .*Request|class .*Response" src/ samples/ -g "*.cs"
```

- **Already exists:** `AuthProviderProtocols.Oidc/OAuth2`, provider definition/options/catalog, `AuthConstants.Routes.Challenge/Callback`, `AuthenticationExtensions.CookieScheme`, current return-URL policy and provider facts.
- **Already exists:** normal ASP.NET scheme registration and handler/options contracts; the seeder's pre-existing-scheme skip permits connector-owned mechanics.
- **Already exists:** `ChallengeOptions` controls JSON/API challenge classification, not protocol-specific identifier grammar; do not overload it with AT options.
- **Already exists:** cross-module contracts belong in `Sylin.Koan.Web.Auth.Abstractions`, which already references `Microsoft.AspNetCore.App` and Core.
- **Needs to be created:** a custom-protocol configuration-validator contract; no current equivalent was found. Connector-specific identifier constants belong to the AT follow-up.
- **Needs to be created:** post-seeding verification that each eligible route has an actual named handler, plus focused custom-protocol tests.

**Creating new:**

| New or changed code | Exact location | Justification |
| --- | --- | --- |
| `IAuthProtocol` interface | `src/Koan.Web.Auth.Abstractions/Providers/IAuthProtocol.cs` | Inert cross-module protocol validation contract; no runtime module or ASP.NET substitute. |
| Compile contributed protocol lookup; use validator for custom protocols; preserve built-in validators | `src/Koan.Web.Auth/Providers/AuthProviderPlan.cs` | One host plan remains the sole eligibility/election owner. Reject duplicate protocol contributions and contributions attempting to replace reserved OAuth2/OIDC mechanics. |
| Verify each eligible named scheme exists after realization | `src/Koan.Web.Auth/Hosting/AuthSchemeSeeder.cs` | Assurance belongs where the plan becomes handlers; missing mechanics must not silently pass startup. |
| Contributed-protocol plan cases | `tests/Koan.Web.Auth.Tests/AuthProviderPlanTests.cs` | Exercise normalized lookup, missing/duplicate protocol, invalid configuration and host isolation at the existing decision owner. |
| Scheme assurance tests | `tests/Koan.Web.Auth.Tests/AuthSchemeSeederTests.cs` | Exercise eligible-missing-handler failure and preservation of a pre-registered standard scheme. |
| Connector-shaped custom protocol/handler fixture and challenge cases | `tests/Suites/Auth/Koan.Web.Auth.Integration.Tests/CustomProtocol/` with one top-level type per file | Exercise real `AddKoan()` composition, unmodified controller challenge, original-request identifier access, cookie lifecycle and plan/facts alignment without an AT SDK dependency. |
| Contributor contract and registration boundary docs | `src/Koan.Web.Auth.Abstractions/README.md`, `src/Koan.Web.Auth.Abstractions/TECHNICAL.md`, `src/Koan.Web.Auth/README.md`, `src/Koan.Web.Auth/TECHNICAL.md`, `docs/guides/authentication-setup.md` | Keep the existing linked application and connector-author story accurate; navigation already reaches the relevant guide. |

The existing `AuthProviderProtocols` built-in constants remain; do not add `Atproto` there in the generic seam. Its stable protocol ID belongs to the future connector. This workcard records the additive design decision; existing dated ADRs remain unchanged. Add an ADR/navigation entry only if review requires a lasting framework-wide policy change beyond this owned extension.

**Coalescence:**

Closest pattern: `AuthSchemeSeeder` plus `TestAuthModule`. The current decision owner is `AuthProviderPlan`; its consumers are startup realization, discovery/controller, cookie challenge selection and facts. State lifetime is one host composition, with request operations consuming the retained result. Existing OAuth2/OIDC option/handler construction is already centralized and must stay there.

Chosen specificity: **Web Auth pillar eligibility with connector-owned protocol mechanics**. Disposition: **keep** the plan, built-in validation, seeder, cookie and controller; **extend** protocol admission; **reuse** standard ASP.NET registration. Core is too wide because protocol requirements are authentication semantics. The application is too narrow because every AT-enabled app would otherwise repeat handler/diagnostic wiring. The connector is too narrow to elect itself as the site's default outside the shared plan.

Compared alternatives:

| Shape | Benefit | Cost and decision |
| --- | --- | --- |
| Eligibility-only contribution plus ordinary registered ASP.NET scheme | Small cross-module contract; standard named options/handlers; seeder already supports existing schemes | Requires explicit eligible-route-to-scheme verification. **Chosen.** A contributor declaring a protocol is insufficient without its actual handler. |
| One protocol interface with both validation and `Seed(IServiceProvider, ...)` | Can realize arbitrary configuration-only provider IDs after the plan is compiled | Introduces a parallel custom lifecycle and service-provider plumbing, duplicates ASP.NET registration, and invites moving existing OAuth2/OIDC into an unnecessary new framework. **Rejected for this slice.** Reconsider only if a real connector requires dynamic post-plan scheme creation that normal module/named-options registration cannot express. |
| Treat AT as generic OAuth2 with dummy endpoints/secret | No new protocol contract | Misstates eligibility and routes exchanges through mechanics whose behavior differs from the selected native client. **Rejected.** |
| Application registers its handler and bypasses provider catalog | Small local patch | Breaks reference intent, default challenge, availability/facts, and repeated-app simplicity. **Rejected.** |

No old production path needs deletion: this is additive. Do not preserve a temporary app-side provider bypass, fake OAuth endpoint configuration, or custom cookie path if one appears during connector experimentation.

**Ergonomics:** A person still chooses an account provider and, when that provider needs it, an identifier; the normal callback returns to the allow-listed destination. A Koan application author keeps one reference/configuration decision and one bootstrap. A connector author sees one protocol validation contract beside existing provider metadata and familiar ASP.NET registration. IntelliSense discovery stays in the existing `Providers` namespace; no additional builder or operation hierarchy is introduced. Coding agents can trace definition -> plan -> named scheme -> handler -> Koan cookie, and startup rejects the otherwise easy-to-miss gap between a declared provider and a real scheme.

**Constraints satisfied:**

- [x] No inline HTTP endpoints; the existing attribute-routed controller remains unchanged.
- [x] No empty placeholder or commented-out production scaffolding; only this workcard is written at exploration time.
- [x] New stable keys use project-owned constants where introduced; AT identifier keys and tunables remain in the connector.
- [x] No new raw environment checks; existing named Koan environment posture remains intact.
- [x] No data repository or store is added; Entity-first persistence is unaffected.
- [x] No large data path is added; bounded host configuration enumerations are not Entity streaming.
- [x] Cross-module contract lives in inert Auth.Abstractions; Web Auth alone owns eligibility/election.
- [x] Structural compilation occurs once per host; no request-time module discovery or global state.
- [x] Existing public documentation will describe the actual admitted path and its corrective failures; dated ADRs are not silently rewritten.
- [x] No production code changes occur before this plan is reviewed by the coordinator. The accepted epic already authorizes implementation; this is coordination review, not another user permission request.

**Risks:**

- Normal ASP.NET registration must happen before `AuthModule.Start`. The generic seam must prove that this works through a real `AddKoan()` host, not just a hand-built service provider.
- A custom protocol can have connector-owned settings outside the provider overlay. Validation diagnostics must retain the correct setting path instead of blindly prefixing every error with `Providers:<id>`.
- Configuration-only custom provider IDs are not automatically implemented. They work only when the real connector supplies validation and registers the corresponding named handler; otherwise startup correctively fails. Existing configuration-only OAuth2/OIDC remains supported.
- The connector-owned identifier query is untrusted input. Its handler must validate it before discovery/network use; this seam neither resolves it nor promotes it into a subject. Do not log the supplied identifier by default.
- A registered scheme proves wiring, not protocol correctness. Real AT state/issuer/replay/DID validation, refresh and encrypted session/key persistence remain the connector's separate acceptance gate.
- The existing cookie pipeline derives provider identity from `/auth/{id}/callback`; a connector choosing another callback convention could lose that lifecycle attribution. Preserve the convention for the first connector.

Focused proof plan before declaring the seam done:

1. Run the existing `tests/Koan.Web.Auth.Tests` suite, adding known/unknown/custom-incomplete/duplicate/reserved-protocol and separate-host cases.
2. Prove custom eligibility plus missing named handler fails host startup with provider ID, protocol and registration correction.
3. Through a real loopback `AddKoan()` host, prove a pre-registered custom scheme survives seeding, can read its identifier from the original challenge request, and observes the existing allow-listed return handling and cookie lifecycle rejection.
4. Run the existing `tests/Suites/Auth/Koan.Web.Auth.Integration.Tests` suite unchanged for actual OAuth2/OIDC discovery, challenge/callback, claims/linking and plan/facts parity.
5. Assert custom discovery/facts come from the same plan and reveal no options secrets. Build the two affected Auth projects and focused suites; do not run a full release certification or publish packages for this slice.

## Implementation evidence — 9 September 2026

Implemented only the inert validator contract and the existing Web Auth plan/seeder extension. No auth controller,
cookie events, identity stores, package versions, solution composition, or original sibling checkout were changed by
this seam work. The seeder now consumes the already-normalized protocol from the plan, including whitespace/case
normalization for existing OAuth2/OIDC providers.

Commands run from the isolated Koan root with `C:/Tools/DotNet/dotnet.exe` (SDK 10.0.401, .NET 10.0.12):

```powershell
& 'C:/Tools/DotNet/dotnet.exe' test tests/Koan.Web.Auth.Tests/Koan.Web.Auth.Tests.csproj --no-restore -v minimal
& 'C:/Tools/DotNet/dotnet.exe' test tests/Suites/Auth/Koan.Web.Auth.Integration.Tests/Koan.Web.Auth.Integration.Tests.csproj -v minimal
git diff --check
```

- Unit suite: **57 passed, 0 failed, 0 skipped**. Includes effective option overlay, exactly-once validation,
  inactive-provider handling, connector correction paths, unknown/duplicate/reserved protocols, separate-host
  isolation, eligible-without-handler failure at the startup seeder, retained custom scheme, and maintained builtin
  handler identity across repeated seeding.
- HTTP integration suite: **9 passed, 0 failed, 0 skipped**. The existing six OAuth2/OIDC tests remain intact and pass
  real local challenge/callback, discovery/JWKS, validated ID token, claims/linkage, and provider/facts checks. Three
  new cases run a real loopback `AddKoan()` host with connector-shaped contributions, proving custom discovery/facts
  and election, standard scheme preservation, existing return-URL policy, ordinary Koan cookie issuance, and sign-in
  rejection before cookie issuance. A callback query replacement cannot override the initiating request value bound
  into protected fixture state.
- Both affected production Auth assemblies compiled as test dependencies; successful runs emitted no build warnings
  or errors. `git diff --check` passed; Git only reported Windows line-ending conversion notices.

The initial `--no-restore` attempt found the new checkout's missing assets; a normal restore supplied them. The first
test compile exposed a namespace ambiguity in newly added test code, corrected to fully qualify `Options.Create`.
The successful runs above followed those corrections.

The custom HTTP handler is intentionally a synthetic local fixture. It proves the generic contribution boundary and
cookie policy, not AT discovery, issuer validation, network safety, replay protection, credential storage, or refresh.
Those belong to the actual connector's separate tests and native protocol acceptance evidence. No full release
certification, package publication, or remote deployment was performed.

## Callback permission correction — 10 September 2026

**Task:** Reject callback grants that silently omit an explicitly requested permission before replacing a working session.
**Application intent:** When a participant connects the access needed for an action, report whether that access was actually granted.
**Public expression:** Keep the existing connector reference, `AddKoan()`, declared `Providers:atproto:Scopes` ceiling, and trusted `AtprotoChallenge.WithScopes(properties, ...)` action. No additional application code or configuration is required.
**Guarantee/correction:** Every exact scope protected in the pending callback binding must occur in the returned token grant. Missing access produces a sanitized corrective HTTP 400 page and retains the previous durable/cached session. Restores and refreshes still check only the declared ceiling; they do not demand every optional client permission.
**Complete intent surface:** The application still declares and requests concrete provider-canonical strings; the user completes the provider's consent flow. No additional user action is introduced.
**Public concepts:** None. A new internal exception distinguishes missing requested access from untrusted provider failures without exposing provider bodies, credentials, or scope values.
**Docs read:** `docs/engineering/README.md` establishes executable, corrective instructions; `docs/architecture/principles.md` places protocol mechanics in the adapter; root `README.md` preserves reference-and-bootstrap intent; `docs/toc.yml` identifies the existing auth guide; connector `README.md` and `TECHNICAL.md` own exact-grant and session guarantees.
**Code read:** `AtprotoSessions` owns callback validation and current scope ceilings; `VerifiedSessionStore` stages SDK writes and commits under the per-DID gate; `AtprotoAuthenticationHandler` owns browser failure sanitization; `AtprotoAuthModule` composes the ordinary ASP.NET scheme; `ProtocolGuards` tests scope policy and durable staging.
**Reusing:** Existing scope constants, allowed-scope catalog, protected Binding, staged store, per-DID gate, and handler failure page pattern. Explicit constants/options/DTO searches found no missing-scope failure type or requested-grant validator to reuse.
**Creating new:**

| New code | Location | Justification |
| --- | --- | --- |
| `RequireRequestedScopes` | `Protocol/AtprotoSessions.cs` | The callback owner compares the protected request with the returned grant before session replacement. |
| `AtprotoMissingScopesException` | `Protocol/AtprotoMissingScopesException.cs` | Internal typed, sanitized failure; neither application authorization nor generic auth owns AT grant mechanics. |
| Missing-access failure page | `Hosting/AtprotoAuthenticationHandler.cs` | Reuses the handler's existing corrective HTTP 400 page boundary. |
| Scope and session-retention regressions | `tests/Koan.Web.Auth.Atproto.Tests/ProtocolGuards.cs` | Proves silent downscope rejection and keeps ordinary sign-in/restore optionality intact. |

**Coalescence:** Closest pattern is `AtprotoSessions.RequireScopes`. Keep it as the ceiling-only check for restoration/refresh and absorb the callback's second ceiling check into the requested-grant validator. This adapter is the single owner; moving it into generic Auth would invent AT policy there, and moving it into Tangent would duplicate security checks for every application. No new request-time discovery or persistent state is added.
**Ergonomics:** Existing C# intent remains unchanged. Internal method names make the ceiling-versus-request distinction readable in one callback path. Browser correction says access was not granted and prior sign-in remains, without presenting raw protocol fields.
**Constraints satisfied:** No new endpoint, module, environment switch, public API, data abstraction, unbounded data path, or package version. Existing module docs are updated in place; no ADR or TOC entry is needed for this correction. The accepted implementation and continuation authorize this scope.
**Risks:** Exact scope matching deliberately rejects provider normalization or expansion; callers must continue requesting canonical scope strings. A rejected newly issued grant is not installed locally; no automatic revocation is attempted because provider revocation could affect a pre-existing authorization.
