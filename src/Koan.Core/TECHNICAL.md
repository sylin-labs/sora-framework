---
uid: reference.modules.Koan.core
title: Koan.Core - Technical Reference
description: Core utilities, primitives, and conventions used across Koan modules.
packages: [Sylin.Koan.Core]
source: src/Koan.Core/
---

## Contract

- Inputs/Outputs: foundational types, result helpers, guards, common abstractions, and the generic
  logical-flow context/carriage contract used by higher pillars.
- Options: follow ADR ARCH-0040 for constants/options.
- Error modes: required terse host-backed surfaces use `KoanHostContextException`; avoid magic values.
- Runtime ownership: the generic-host binder owns the process-default `AppHost` provider from host
  start through stop. Explicit flow scopes override that default without mutating its lease.

## Key types

- Core primitives surfaced by other modules (data, web, messaging, ai).
- `Koan.Core.Json.KoanJsonContractResolver`: editor-hidden common Json.NET array construction for
  concrete additive collections. Existing native constructors, IList and ICollection contracts take
  precedence. The fallback uses only public parameterless construction and exact `Add(T)`; seeded
  constructor values require public `Clear()` or fail. Data and Web inherit this logic while retaining
  their own persistence and operation policy. It does not change global JsonConvert defaults.
- `KoanContext`: one exact-type-keyed immutable context snapshot for the current logical execution
  flow. `Push<T>` and `Suppress<T>` restore the prior snapshot when their scopes are disposed.
- `IKoanContextCarrier`: a module-owned serializer/restorer for one opaque, versioned context axis.
- `KoanContextCarrierRegistry`: the host-owned, deterministic registry that captures registered axes,
  validates all identities and trust requirements before restore, suppresses absent axes, and unwinds
  partial scopes in reverse order. Its value-free `Descriptors` projection exposes only each
  `CarrierDescriptor(AxisKey, MinimumIngressTrust)` in ordinal axis order.
- `ContextIngressTrust`: provenance supplied by an ingress (`Unverified`, `Authenticated`, or
  `HostTrusted`); it does not imply confidentiality, authorization, delivery, or payload correctness.
- `KoanContextCarrierException`: safe machine-readable carriage failures containing bounded axis
  identities and trust posture, never carried values or implementation exceptions.
- `KoanContextFingerprint`: a domain-separated, length-delimited SHA-256 identity over a canonical
  carrier bag and optional logical identity parts. It is for dedupe/keys, not confidentiality or trust.
- `AppHost`: resolves the current flow-scoped provider, then the running host's leased default.
- `AppHost.PushScope(IServiceProvider)`: selects a provider for one async flow and restores the prior
  flow value when disposed.
- `AppHost.Attach(IServiceProvider)`: low-level hosting integration lease. Disposing it clears the
  process default only if it still owns that binding; it never revives a predecessor.
- `AppHost.GetRequiredService<T>(operation)`: resolves a required service from the selected host and
  distinguishes missing host, disposed host, and missing service through `KoanHostContextException`.
- `AppHost.Identity`: resolves the immutable identity snapshot registered by that same provider;
  hostless callers receive the frozen `KoanEnv` application identity.
- `KoanLog.For<T>()`: creates a category-only reusable scope. Each emission resolves
  `ILoggerFactory` from the current `AppHost` provider, so host leases and flow scopes also govern
  logging without a second ambient owner.
- `IKoanRuntimeFacts`: read-only access to the current host's schema-versioned runtime fact envelope.
- `KoanFactKind.Guarantee`: schema-2 meaning for a value-free guarantee projected from a canonical
  concern plan or realization receipt. Startup selects by kind; Web and MCP serialize the same envelope.
- `KoanApplicationReferenceManifest`: immutable host-owned direct `PackageReference`/
  `ProjectReference` provenance. `IsPresent=false` means unknown provenance; it must never be replaced
  by inference from loaded assemblies.
- `KoanFactJson`: the canonical deterministic JSON projection used by Web and MCP.
- `KoanModule.ReportComposition`: optional fail-soft evidence projection invoked only on constitution-active
  retained module instances. It reads canonical plans/receipts after host construction; it is not a
  provider-election or structural-contribution lifecycle.
- `ServiceDiscoveryAdapterBase`: the concern-owned discovery template. Adapters supply environment,
  runtime-topology, normalization, and health hooks but cannot replace activation or precedence.
- `KoanServiceAttribute`, `ServiceKind`, and `DeploymentKind` in `Koan.Core.Services`: the
  always-present runtime service description read by discovery and, by metadata name, external
  topology tooling. Core has no dependency on a Koan topology package.
- `IAdapterReadiness`, `IAsyncAdapterInitializer`, `AdaptersReadinessOptions`, and
  `AdapterLifecycleModule`: one shared backend lifecycle. The module starts and monitors adapters;
  concern-specific recovery remains with the concern (for example, schema provisioning in Data Core).
- `ServiceDiscoveryReporting`: reuses the normal discovery adapter to render startup provenance; it
  does not implement another endpoint-election policy.
- `DiscoveryCandidatePriority`: the canonical explicit → environment alias → automatic → host-gateway →
  loopback ordering used by every discovery adapter and optional contributor.
- `buildTransitive/Sylin.Koan.Core.targets`: emits static composition, the embedded resolved-module
  and direct-reference manifests, and trimming roots for applicable executable builds even when Core
  arrives through a bundle.

## Usage guidance

- Prefer these utilities over bespoke helpers; keep concerns separated.
- Put immutable, module-owned meaning in `KoanContext`; keep services and disposable runtime objects
  in the host's DI container. Higher modules should expose business-named facades such as `Tenant`,
  not require application code to manipulate generic context directly.
- Register `IKoanContextCarrier` implementations through the module's normal composition path. The
  caller restoring a durable bag must state its `ContextIngressTrust`; format validation alone is not
  authentication.
- Let `AddKoan()` and the generic host manage the default provider. Use `PushScope` for concurrent
  integration hosts, jobs, or other explicit execution contexts.
- Custom hosting integrations that call `Attach` must keep its lease for exactly the provider's active
  lifetime and dispose the lease no later than the provider. Prefer `StartKoan()` for synchronous
  console startup; it owns a standard Generic Host internally.
- Resolve host-specific application identity through `AppHost.Identity` or an explicitly supplied
  provider. Do not retain configuration-derived identity in another process static.
- Do not cache services obtained from `AppHost.Current` in process-static fields. Immutable reflection
  metadata may be process-static; services and configuration remain host-owned.
- Reserve `GetRequiredService<T>(operation)` for terse framework APIs and advanced hosting seams.
  Application business code should use constructor injection. Optional probes should retain explicit
  `Try*`, nullable, or availability behavior instead of throwing this exception.
- Static `KoanLogScope` fields are safe because they retain only category text. A hostless flow or a
  selected provider without `ILoggerFactory` emits nothing and never falls back to another host.
- Read fact `Code`/`ReasonCode`/`State` for automation. Do not parse startup prose or treat
  `Complete=false` as healthy.
- Treat `Guarantee` as explanation, not health or provider-fleet certification. Read its explicit
  bounds and never infer topology, confidentiality, durability, or exactly-once semantics.
- Framework/capability packages that need runtime evidence override `ReportComposition` on their
  descriptor-backed module. Do not create a reporter service, discoverable contributor, or second module
  object; never include configuration values, ambient dimensions, credentials, or business payloads.
- Layer optional engines through the concern-owned contributor seam. Compatibility metadata is inert;
  the engine's Reference = Intent registration activates contribution; adapters never probe assemblies.
- Keep the checked-in `koan.lock.json` under review. It contains static app/module identity and direct
  application reference provenance;
  negotiated elections and runtime facts belong to `obj/koan.lock.resolved.json`.
- Lockfile `app.name` is the executable assembly identity and is excluded from `modules`; friendly
  application identity remains an operator/runtime-facts concern.
- Foundation packages keep their own ordinary identities (for example `Example.Approvals.Foundation`).
  Generated reference manifests accept the build writer's package/project name grammar without a
  `Sylin.Koan` prefix requirement. Only reachable dependency edges activate available modules;
  unrelated modules remain inactive. Malformed records still fail with corrective rebuild guidance.

## Observability & Security

- Runtime facts exclude arbitrary payloads, raw exception messages, stack traces, and configuration
  values. They still expose topology identifiers and should use an operational access boundary.
- Context values and opaque carrier payloads must not enter runtime facts, startup prose, logs, or
  exceptions. `KoanContextCarrierRegistry.Descriptors` is the current safe inspection boundary. A
  later runtime-facts/startup projection may reuse it without widening the data disclosed.

## References

- ARCH-0040 config and constants: `/docs/decisions/ARCH-0040-config-and-constants-naming.md`
- Engineering guardrails: `/docs/engineering/README.md`
- Runtime facts: `/docs/engineering/runtime-facts.md`
- ARCH-0111: `/docs/decisions/ARCH-0111-unified-runtime-facts.md`
- ARCH-0113: `/docs/decisions/ARCH-0113-entity-capability-communication.md`
- ARCH-0114: `/docs/decisions/ARCH-0114-layered-capability-activation.md`

## Background-service configuration

AddKoan binds Koan:BackgroundServices to KoanBackgroundServiceOptions. Enabled=false prevents the
orchestrator from executing child services; Services:<service-name>:Enabled controls an individual child.
StartupTimeoutSeconds and FailFastOnStartupFailure use the same bound options. The directly resolved
orchestrator and the hosted-service instance are the same singleton. Previously ignored settings now take effect.
