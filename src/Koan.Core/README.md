# Sylin.Koan.Core

The always-present Koan substrate for module composition, host lifecycle, logical context, provider catalogs,
readiness, service discovery, runtime facts, and build provenance.

Applications normally reference `Sylin.Koan` or `Sylin.Koan.App`, which bring Core transitively. Reference Core
directly when authoring a Koan module, provider, projection, or host integration.

## Install

```powershell
dotnet add package Sylin.Koan.Core
```

## Meaningful use

A module is an ordinary class. Its identity and version derive from standard package/assembly facts; module authors
do not declare a Koan ID or descriptor.

```csharp
using Koan.Core;
using Microsoft.Extensions.DependencyInjection;

public sealed class BillingModule : KoanModule
{
    public override void Register(IServiceCollection services)
        => services.AddSingleton<InvoiceService>();
}
```

When an application calls `AddKoan()`, the generated host constitution retains one module instance across
registration, ordered startup, provenance, and composition reporting. Referencing Core alone does not activate an
application capability beyond this substrate.

Core also provides:

- `KoanContext` and `IKoanContextCarrier` for immutable logical-flow meaning and explicit durable carriage;
- one memoized provider catalog and priority model used by concern-owned election pipelines;
- generic adapter readiness, initialization, monitoring, and operation gating;
- service-description and discovery contracts shared by runtime providers and external topology tooling;
- `IKoanRuntimeFacts` and `KoanFactJson`, the redacted envelope shared by startup, health, Web, and MCP;
- `AppHost` ownership for terse framework surfaces that must reach the current Koan host; and
- transitive build targets that emit `koan.lock.json`, direct-reference provenance, semantic activation manifests,
  and trimming roots for executable consumers.

## Inspection and correction

Keep the checked-in `koan.lock.json` under review. It records static application/module identity and direct package or
project intent; negotiated runtime decisions remain in startup output, runtime facts, and
`obj/koan.lock.resolved.json`.

Required host-backed operations fail with `KoanHostContextException`, distinguishing an absent host, disposed host,
and missing service while naming the operation and correction. Incomplete runtime facts are `Complete=false` and
must never be interpreted as implicit success.

## Boundaries and failures

- Core is mechanism, not a business capability. It does not select a data store, transport, web projection,
  observability exporter, container engine, or AI provider.
- Put immutable module-owned meaning in `KoanContext`; keep services, mutable state, and disposable resources in DI.
- Context carriage format validation is not authentication, confidentiality, or authorization. Ingress must state
  trust and insufficient trust fails before user work.
- Runtime facts exclude arbitrary configuration values, payloads, stack traces, and raw exceptions, but still expose
  topology identities and require an operational access boundary.
- Application code should prefer constructor injection. `AppHost.GetRequiredService` is for terse framework surfaces
  and advanced hosting seams.
- Core's generic provider/readiness mechanisms do not own concern policy. Data owns schema recovery, Communication
  owns lane guarantees, and each pillar owns its selection and failure semantics.

See [TECHNICAL.md](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Core/TECHNICAL.md) for lifecycle,
context, facts, provider-catalog, and build-target contracts.

## Background-service configuration

AddKoan binds Koan:BackgroundServices to KoanBackgroundServiceOptions. Enabled=false prevents the
orchestrator from executing child services; Services:<service-name>:Enabled controls an individual child.
StartupTimeoutSeconds and FailFastOnStartupFailure use the same bound options. The directly resolved
orchestrator and the hosted-service instance are the same singleton. Previously ignored settings now take effect.
