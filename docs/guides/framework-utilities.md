---
type: GUIDE
domain: core
title: "Framework Utilities Guide"
audience: [developers, architects, ai-agents]
status: current
last_updated: 2026-09-12
framework_version: v1.0.0
validation:
  date_last_tested: 2026-07-17
  status: verified
  scope: module declaration verified by downstream generated/trim-shaped packaging proof; other sections retain prior evidence
related_guides:
  - entity-capabilities-howto.md
  - ../reference/data/index.md
  - ../reference/web/index.md
  - performance.md
---

# Framework Utilities Guide

A catalog of what Koan already provides. Read it before writing a helper: the odds are good that the
thing you are about to write exists, and that the existing one is wired into startup reporting,
capability negotiation, or provenance in ways a local copy will not be.

## Table of Contents

- [Orchestration & Discovery](#orchestration--discovery)
- [Configuration & Options](#configuration--options)
- [Web API Utilities](#web-api-utilities)
- [Data Access Helpers](#data-access-helpers)
- [Background Jobs](#background-jobs)
- [Common Patterns](#common-patterns)
- [Provenance & Boot Reporting](#provenance--boot-reporting)

---

## Orchestration & Discovery

### ConnectionStringParser

`src/Koan.Core/Orchestration/ConnectionStringParser.cs`

One parse and one build, both told which provider's dialect to use. Recognised provider types are
`postgres`/`postgresql`, `sqlserver`/`mssql`, `sqlite`, `redis`, and `mongodb`/`mongo`; anything else
falls back to generic key/value parsing rather than failing.

```csharp
public static ConnectionStringComponents Parse(string connectionString, string providerType);
public static string Build(ConnectionStringComponents components, string providerType);
public static (string Host, int Port) ExtractEndpoint(string connectionString, string providerType);
```

`ConnectionStringComponents` is a record of `Host`, `Port`, `Database`, `Username`, `Password`, and a
`Parameters` dictionary for whatever else the string carried. An empty or whitespace connection string
parses to `ConnectionStringComponents.Empty` rather than throwing.

```csharp
var components = ConnectionStringParser.Parse(
    "Host=localhost;Port=5432;Database=mydb;Username=admin;Password=secret", "postgres");

var (host, port) = ConnectionStringParser.ExtractEndpoint(connectionString, "postgres");
```

Use it in discovery adapters building a health probe, in connector factories reading configuration,
and in test fixtures composing a connection string -- anywhere provider-specific string handling would
otherwise be written twice.

---

### ServiceDiscoveryAdapterBase

**Location**: `src/Koan.Core/Orchestration/ServiceDiscoveryAdapterBase.cs`
**Pattern**: Template Method base class
**ADR**: [ARCH-0068](../decisions/ARCH-0068-refactoring-strategy-static-vs-di.md) (P1.02)

**Purpose**: Base class for service discovery adapters with container/local/Aspire detection logic.

#### Abstract Members to Implement

```csharp
protected abstract string ServiceName { get; }
protected abstract string[] Aliases { get; }
protected abstract Type GetFactoryType();
protected abstract Task<bool> ValidateServiceHealth(string serviceUrl, DiscoveryContext context, CancellationToken cancellationToken);
```

#### Provided Infrastructure

```csharp
// Container detection (Docker/Podman)
protected bool IsContainerEnvironment { get; }
protected string? DetectContainerService(string serviceName);

// Aspire detection
protected bool IsAspireEnvironment { get; }
protected string? DetectAspireService(string serviceName);

// Local fallback detection
protected string? DetectLocalService(int defaultPort);

// Configuration-based discovery
protected string? GetConfiguredConnectionString();

// Service attribute reading
protected KoanServiceAttribute? GetServiceAttribute();
```

#### Usage Example

```csharp
internal sealed class PostgresDiscoveryAdapter : ServiceDiscoveryAdapterBase
{
    public override string ServiceName => "postgres";
    public override string[] Aliases => new[] { "postgresql", "npgsql" };

    public PostgresDiscoveryAdapter(IConfiguration configuration, ILogger<PostgresDiscoveryAdapter> logger)
        : base(configuration, logger) { }

    protected override Type GetFactoryType() => typeof(PostgresAdapterFactory);

    protected override async Task<bool> ValidateServiceHealth(string serviceUrl, DiscoveryContext context, CancellationToken cancellationToken)
    {
        var components = ConnectionStringParser.Parse(serviceUrl, "postgres") with
        {
            Database = context.Parameters.GetValueOrDefault("database", "postgres")
        };

        var connectionString = ConnectionStringParser.Build(components, "postgres");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return true;
    }
}
```

#### When to Use
- Creating new discovery adapters for data stores
- Implementing autonomous service discovery
- Supporting container, Aspire, and local development environments

---

## Bootstrap & Modules

### KoanModule

**Location:** `Koan.Core` (`KoanModule`; host `Koan.Core.Hosting.Modules.KoanModuleHost`)

The boot-time module primitive (ARCH-0086/ARCH-0115). Framework and capability-package authors use one
ordinary module for registration, typed structural contribution, startup, provenance, and safe composition
evidence. The generator derives its stable identity from the standard NuGet `PackageId` (falling back to
`AssemblyName`), emits the construction metadata, and the host retains one instance for the complete lifecycle.
An implementation assembly contains at most one concrete `KoanModule`.

#### Members
- `string Id` — derived and host-bound; module authors do not declare or override it.
- `string? Version` (virtual) — defaults to the declaring assembly version.
- `void Register(IServiceCollection services)` (virtual) — register DI services. Replaces `Initialize`.
- `Task Start(IServiceProvider sp, CancellationToken ct)` (virtual) — one-time startup work, DI available,
  ordered against other modules by `[Before]`/`[After]`, run by `KoanModuleHost`. Folds the "register a
  bootstrap `IHostedService` for startup" idiom into one verb.
- `void Report(ProvenanceModuleWriter module, IConfiguration cfg, IHostEnvironment env)` (virtual) — publish
  provenance. Named `Report` (not `Describe`) to disambiguate from per-provider capabilities
  (`IDescribesCapabilities.Describe`, ARCH-0084).
- `void ReportComposition(KoanCompositionBuilder composition, IServiceProvider services)` (virtual) —
  optionally project already-resolved safe decisions from this active retained module. Do not perform
  provider election, structural contribution, or application work here.

#### Usage Example

```csharp
public sealed class MyPillarModule : KoanModule
{
    public override void Register(IServiceCollection services)
        => services.AddSingleton<IMyService, MyService>();

    public override Task Start(IServiceProvider sp, CancellationToken ct)
    {
        // one-time startup work, DI available, ordered by [Before]/[After]
        return Task.CompletedTask;
    }

    public override void ReportComposition(
        KoanCompositionBuilder composition,
        IServiceProvider services)
    {
        var plan = services.GetRequiredService<MyPillarPlan>();
        composition.AddObservation(
            "koan.my-pillar.plan.selected",
            "my-pillar:plan",
            $"Koan selected '{plan.Posture}' for MyPillar.",
            plan.Reason,
            Id);
    }
}
```

#### When to Use
New framework or capability-package modules. Recurring periodic/pokable work stays on the
`IKoanBackgroundService` family — `Start` models one-time ordered startup only. Code that registers
services belongs in `Register`, not `Start`. `ReportComposition` is a fail-soft projection of canonical
plans/receipts; never create a separate discoverable reporter or place configuration values, credentials,
ambient context values, or business payloads in it. Application developers normally need only `AddKoan()`.

Cross-module contracts belong in an isolated `*.Core`, `*.Abstractions`, or `*.Contracts` assembly with
no `KoanModule`. Reference that contracts assembly when only its API is needed; reference the functional
assembly when the capability should participate. No Koan-specific project-reference metadata is required.

### [KoanDiscoverable] + KoanRegistry.GetDiscoveredImplementors

**Location:** `Koan.Core` (`KoanDiscoverableAttribute`; `Koan.Core.Hosting.Registry.KoanRegistry`)

Mark an **interface** with `[KoanDiscoverable]` and every concrete implementer is auto-registered into the
central `KoanRegistry` — at build time by the source generator and at runtime by `RegistryManifestLoader` —
keyed by the interface `Type`. Query it with `KoanRegistry.GetDiscoveredImplementors(typeof(T))`. This
replaces bespoke `AppDomain.CurrentDomain.GetAssemblies()` reflection scans, which miss lazily-loaded Koan
assemblies and bypass the single discovery authority (ARCH-0086 §4).

#### Usage Example

```csharp
[KoanDiscoverable]
public interface IMyPlugin { /* ... */ }

// elsewhere (e.g. inside a module/registrar), wire the discovered implementers:
foreach (var type in KoanRegistry.GetDiscoveredImplementors(typeof(IMyPlugin)))
    services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IMyPlugin), type));
```

#### When to Use
Any "many implementers of one contract, discovered without explicit registration" surface — instead of
hand-rolling an `AppDomain` assembly scan. Used by `IKoanAuthEventContributor` / `IKoanAuthFlowHandler`.

---

## Configuration & Options

### Configuration.ReadWithSource\<T\>

**Location**: `src/Koan.Core/Configuration.cs`
**Pattern**: Static helper method
**ADR**: [ARCH-0068](../decisions/ARCH-0068-refactoring-strategy-static-vs-di.md)

**Purpose**: Read a configuration value **with source attribution** — returns not just the value but
where it came from (appsettings, environment variable, LaunchKit, etc.). This is the preferred
method inside `KoanModule.Report(...)` to report settings with their origin.

#### Return type: `ConfigurationValue<T>`

```csharp
public readonly record struct ConfigurationValue<T>(
    T Value,                // The resolved value
    BootSettingSource Source,  // Where the value came from
    string? ResolvedKey,    // The config key that matched
    bool UsedDefault        // true when no config was found and default was returned
);
```

#### `BootSettingSource` enum

| Value | Meaning |
|-------|---------|
| `Unknown` | Source could not be determined |
| `Auto` | Resolved by the framework automatically |
| `AppSettings` | From `appsettings.json` / `appsettings.{env}.json` |
| `Environment` | From an environment variable |
| `LaunchKit` | From LaunchKit service provisioning |
| `Custom` | Explicitly set in code |

#### Available Methods

```csharp
// Read with source attribution (preferred in Describe())
public static ConfigurationValue<T> ReadWithSource<T>(
    IConfiguration? cfg,
    string key,
    T defaultValue)

// Convenience overload — checks multiple keys in order (first match wins)
public static ConfigurationValue<T> ReadWithSource<T>(
    IConfiguration? cfg,
    T defaultValue,
    params string[] keys)

// Read without source (use when you only need the value)
public static T Read<T>(IConfiguration? cfg, string key, T defaultValue)
public static T Read<T>(IConfiguration? cfg, T defaultValue, params string[] keys)
```

#### Usage Example

```csharp
public void Describe(ProvenanceModuleWriter module, IConfiguration cfg, IHostEnvironment env)
{
    module.Describe(ModuleVersion, "Postgres connector");

    // Read with source tracking — reports the value AND where it came from
    var host = Configuration.ReadWithSource(cfg, "localhost", "Koan:Data:Postgres:Host", "POSTGRES_HOST");
    var db   = Configuration.ReadWithSource(cfg, "default",   "Koan:Data:Postgres:Database");

    module.SetSetting("host",     b => b.Value(host.Value).Source(host.Source.ToString()));
    module.SetSetting("database", b => b.Value(db.Value).Source(db.Source.ToString()));

    if (db.UsedDefault)
        module.SetStatus("degraded", "Database name not configured — using default");
}
```

#### When to Use
- Inside `KoanModule.Report(...)` to show where settings came from in the boot report
- Connector modules reporting their resolved configuration
- Any diagnostic context where traceability of config values matters
- Use plain `Configuration.Read<T>()` when you only need the resolved value

---

### OptionsExtensions

`src/Koan.Core/Modules/OptionsExtensions.cs`

Binds an options type to a configuration section and validates it, so a module registers a family in
one line. Validation is not a separate opt-in: every overload applies data-annotation validation and,
by default, validates at host start rather than on first resolution.

```csharp
public static OptionsBuilder<TOptions> AddKoanOptions<TOptions>(
    this IServiceCollection services, string? configPath = null, bool validateOnStart = true);

public static OptionsBuilder<TOptions> AddKoanOptions<TOptions, TConfigurator>(
    this IServiceCollection services, string? configPath = null, bool validateOnStart = true,
    ServiceLifetime configuratorLifetime = ServiceLifetime.Singleton);

public static OptionsBuilder<TOptions> AddKoanOptions<TOptions>(
    this IServiceCollection services, IConfiguration cfg, string sectionPath,
    Action<TOptions>? postConfigure = null, bool validateOnStart = true);
```

The one-argument form is the common path. Binding is deferred through `IConfigureOptions<T>`, so the
call does not require `IConfiguration` to be present in DI; where it is absent, the type's defaults
apply instead of failing.

```csharp
public sealed class BillingModule : KoanModule
{
    public override void Register(IServiceCollection services)
        => services.AddKoanOptions<BillingOptions>(BillingOptions.SectionPath);
}
```

Give the options type a `SectionPath` constant, as `AuthorizeOptions`, `OriginOptions`, and
`PaginationSafetyBounds` do, so the section name lives with the type it configures rather than at
each call site. Reach for the `TConfigurator` overload when a family needs computed defaults, and for
the `IConfiguration` overload when a post-configure step has to normalize what was bound.

---

### EntityQueryParser

`src/Koan.Web/Queries/EntityQueryParser.cs`

Turns an HTTP query collection into the `QueryOptions` the endpoint pipeline consumes: paging, sort
specs resolved against the entity, shape, view, and extras. One call reads the whole query string --
there is no per-concern parse to assemble yourself.

```csharp
public static QueryOptions Parse<TEntity>(
    IQueryCollection query, EntityEndpointOptions defaults, bool lenient = false);

public static QueryOptions Parse(
    Type entityType, IQueryCollection query, EntityEndpointOptions defaults, bool lenient = false);
```

Sort fields resolve against `TEntity`, and an unresolvable field throws `InvalidSortFieldException`,
which `EntityController` converts to `400`. Pass `lenient: true` where an unknown field should be
dropped instead.

`EntityController.BuildOptions()` is this call, so overriding that method is the supported way to
adjust the result before the query runs.

---

### PatchNormalizer

`src/Koan.Web/PatchOps/PatchNormalizer.cs`

Normalizes each accepted PATCH media type into one canonical `PatchPayload<TKey>`, so the rest of the
write path sees a single operation list regardless of what the client sent
([DATA-0116](../decisions/DATA-0116-canonical-patch-operations.md)).

```csharp
public static PatchPayload<TKey> NormalizeJsonPatch<TEntity, TKey>(...);   // RFC 6902
public static PatchPayload<TKey> NormalizeMergePatch<TKey>(...);           // RFC 7386
public static PatchPayload<TKey> NormalizePartialJson<TKey>(...);          // partial application/json
```

The [PATCH guide](patch-capabilities-howto.md) owns format selection, null and array policy, and the
provider pushdown story.

---

### SampleApplicationExtensions

`src/Koan.Web/Hosting/SampleApplicationExtensions.cs`

Setup that samples repeat, kept in one place so a sample's `Program.cs` stays about its own subject.

```csharp
public static WebApplicationBuilder ConfigureSampleLogging(this WebApplicationBuilder builder);
public static WebApplication ConfigureSampleLifecycle(...);
public static void LaunchBrowser(ILogger logger, string url);
```

These exist for the samples in this repository. An application uses the ordinary `AddKoan()` bootstrap
and its own logging and lifecycle choices.

---

### Entity Static Methods Pattern

**Location**: Throughout `Entity<T>` and `Entity<T, TKey>` classes
**Pattern**: Static factory methods on entity classes
**Guidance**: [Data capability](../reference/data/index.md)

#### Common Patterns

```csharp
// Retrieve by ID
var todo = await Todo.Get(id);
var todos = await Todo.All();
var results = await Todo.Query(x => x.IsComplete == false);

// Create
var newTodo = new Todo { Title = "Buy milk" };
await newTodo.Save();

// Update
var todo = await Todo.Get(id);
todo.Title = "Buy organic milk";
await todo.Save();

// Remove
await todo.Remove();
```

#### When to Use
- Application-level entity operations
- Avoiding manual repository injection
- Following Koan's "Reference = Intent" pattern
- Rapid prototyping and sample code

### Conditional compare-and-set (optimistic concurrency)

**Location:** `Koan.Data.Abstractions` (`IConditionalWriteRepository<TEntity,TKey>`, `DataCaps.Write.ConditionalReplace`)

Atomically replace a row **iff the stored row still matches a guard** — a compare-and-set / optimistic-concurrency
write. The guard is an ordinary LINQ predicate, lowered through the same filter translator as `Query`, so there is
no new SQL surface. Declared by SQLite, Postgres, SqlServer, and Mongo (relational conditional `UPDATE … WHERE Id …
AND <guard>`; Mongo single-document `ReplaceOne` — atomic, no transaction) and forwarded by `RepositoryFacade`.

```csharp
var repo = Data<Order, string>.As<IConditionalWriteRepository<Order, string>>();
if (repo is not null && Data<Order, string>.Capabilities.Has(DataCaps.Write.ConditionalReplace))
{
    order.Status = OrderStatus.Shipped;
    bool applied = await repo.ConditionalReplaceAsync(order, o => o.Status == OrderStatus.Paid);
    // applied == false → someone changed it first (lost the race); re-read and retry.
}
```

This is the primitive behind the jobs contention-free claim (JOBS-0005 §20.3). Probe the capability (or null-check
the cast) and fall back where an adapter doesn't declare it.

### PartitionNameValidator

**Location:** `Koan.Data.Core` (`PartitionNameValidator`; enforced in `EntityContext.With`)

Validates partition names so distinct partitions cannot collide after identifier sanitization. Adapters turn
a partition into a storage identifier via `PartitionTokenPolicy`, which maps every disallowed character to the
same `_` — a lossy mapping that would collapse `tenant/7`, `tenant 7`, and `tenant_7` onto one store.
`EntityContext.With(partition:)` rejects exactly those names up front (`ArgumentException`) so the mapping stays
injective.

**Rule:** a partition name is valid iff it is a **GUID**, or every character (after trimming) is a letter,
digit, or one of `-` `.` `_`. Whitespace-only is treated as "no partition" (not an error).

```csharp
using (EntityContext.Partition("tenant-7")) { /* ok */ }
using (EntityContext.Partition("019a5aff-79cb-7815-8dae-3700a698f840")) { /* ok — GUID */ }
using (EntityContext.Partition("tenant/7")) { /* throws ArgumentException — would collide with tenant_7 */ }
```

#### When to Use
- You don't call it directly — it runs automatically on every `EntityContext.With(partition:)` /
  `EntityContext.Partition(...)`. Catch `ArgumentException` if you route user-supplied partition values and want
  to surface a friendly error; otherwise re-encode names to the allowed set before use. See DATA-0077 §4.

---

## Background Jobs

**Location**: `src/Koan.Jobs` (internal wake carriage is supplied by `Koan.Communication`)
**Pattern**: Entity-first pillar, auto-discovered (`[KoanDiscoverable]` / `KoanModule`)
**ADR**: [JOBS-0005](../decisions/JOBS-0005-job-orchestrator-rebuild.md) · **Authoring guide**: [Background Jobs How-To](jobs-howto.md)

**Purpose**: Durable, edge + level-triggered background work with a single orchestrator concern and a
ledger-as-truth model. A job is a normal `Entity<T>` carrying its own behavior — no queues, workers, or
repositories to wire. The same job code runs unchanged across tiers; the infrastructure you reference
(a durable data adapter, multiple nodes, a Communication connector) decides durability and scale, never correctness.

#### Entry points

```csharp
// Define: behavior co-located with the entity
public sealed class SendEmail : Entity<SendEmail>, IKoanJob<SendEmail>
{
    public string To { get; set; } = "";
    public static async Task Execute(SendEmail job, JobContext ctx, CancellationToken ct) { /* … */ }
}

// Submit / trigger / query via the .Job (instance) and .Jobs (static) accessors
await email.Job.Submit();                 // edge trigger
await SendEmail.Jobs.Trigger("reconcile"); // type-level singleton
await mailbatch.Submit();                  // batch (IEnumerable<T>)
```

| Surface | What it does |
|---|---|
| `IKoanJob<TSelf>` + `static Execute(...)` | the job contract; auto-discovered |
| `.Job` / `.Jobs` accessors | `Submit` / `Trigger` / `Cancel` / `Where` / `Status` (C# 14 extension members) |
| `[JobAction(action, Timeout/MaxAttempts/OnFailure/Lane/MaxConcurrency/Schedule/Deadline/MaxReschedules)]` | per-action policy |
| `[JobChain(a,b,c)]` | linear pipeline (auto-advance, one ledger entry per stage) |
| `[JobIdempotent(keys)]` | collapse concurrent / duplicate submits |
| `[JobGate(member)]` | shared resource gate for cooperative backoff; `member` is a property **or** an async resolver method `Task<string?>(IServiceProvider, CancellationToken)` for runtime-derived keys (§18) |
| `[JobPersistence(Auto\|InMemory\|DataStore)]` | per-type durability intent; `DataStore` rejects when no durable provider can honor it |
| `[ParallelSafe]` | opt out of per-entity serialization (default: jobs for one entity run one at a time) |
| `JobContext` verbs | `Reschedule(after\|until)` (defer, no retry consumed), `Backoff(after, key)` (cross-node gate), `ContinueWith` / `StopChain`, `Progress` |

#### Work-item write safety (ADR §17)

Two defaults make *an entity a consistency unit*, so handlers don't lose writes:

- **Mutate the entity passed to `Execute`** — the orchestrator auto-saves *that* reference, but **only if it changed**. An untouched reference is never written; a handler that reloads-and-saves its own copy is never clobbered (it left the passed one clean). Don't reload a second copy and save it yourself.
- **One job per entity at a time** — a work-item id is its ordering key (Kafka-partition / SQS-FIFO model): jobs for the same `(WorkType, WorkId)` are serialized by default; different entities parallelize fully. Opt out per type with `[ParallelSafe]` when the actions are provably independent.

#### Capability ladder

`in-memory → durable → distributed → +Communication wake` — constant at-least-once + idempotent contract across all of them.

- **In-memory** (no durable data adapter): fast and explicitly ephemeral.
- **Durable** (SQLite/Postgres/Mongo/SQL Server or another durable Data provider): a Data-backed ledger over `Entity<JobRecord>`;
  ambient submission is deferred until the coordinator's commit sequence (not an atomic outbox), and **retention** is automatic —
  the sweep purges Completed/Cancelled past `ArchiveAfter` (7d) and Failed/Dead past `FailedAfter` (30d), with an
  optional per-work-type count cap (`RetainPerWorkType`). On a TTL-capable store (Mongo) a native TTL index on the
  per-outcome `ExpireAt` (`[Index(Ttl)]` / `DataCaps.Retention.TtlIndex`) expires terminal rows continuously between
  sweeps; the sweep stays the universal backstop (§20.4). Ledger reads are pushed down (indexed claim/dashboard queries).
  A later failure in the same ambient sequence can leave the saved Entity without its job; invariants that require
  durable publication need one atomic aggregate or application-owned idempotent recovery.
- **Distributed** (several nodes on one store): competing consumers use the adapter's conditional compare-and-set
  capability automatically. SQLite/Postgres/SqlServer/Mongo admit only one claimant per ready ledger row; adapters
  without that capability retain the honest optimistic at-least-once fallback. Resource gates are honored cross-node.
  (JOBS-0005 §20.3)
- **+Communication connector**: cross-node wake — a submit emits one bounded hint to
  the elected worker group; no Jobs-specific transport package or application bus API
  instead of waiting out the poll interval. Latency upgrade only; the ledger stays the truth.

> Scheduling is initiator-driven: a `Schedule` re-submits a fresh job on its cadence (interval / cron via Cronos /
> `@boot` / `@continuous`) against the per-type singleton — never a parked job. See the how-to for the full model.

> Bulk / high throughput: model the **window** as the work-item, not the row — a cursor-conveyor re-queues itself via
> `ctx.ContinueWith` to drain a large source through a handful of ledger rows (not one per item). The sweep warns
> (`JobPerRowWarnThreshold`) when a work-type's active set looks like job-per-row. See the how-to §8.1.

> Observability: active counts come from the indexed ledger; opt into `JobsOptions.MetricsEnabled` for a
> node-sharded throughput rollup that **survives retention** — read it with
> `JobMetrics.Summary(workType, from, to)`. See the how-to §10. (JOBS-0005 §20.2)

---

## Common Patterns

### Guard Clauses

`src/Koan.Core/Utilities/Guard/`

Fluent, zero-allocation parameter validation that captures the parameter name for you:

```csharp
var validTitle = title.Must().NotBe.Blank();
var validPriority = priority.Must().Be.InRange(1, 5);
```

The [guard utilities reference](../reference/core/guard-utilities.md) is the complete surface --
every `Be` and `NotBe` member, the `RangeType` options, and where guards stop and batch validation
begins. It is the one place that contract is written down.

---

### Provenance & Boot Reporting

**Location**: `src/Koan.Core/Provenance/ProvenanceModuleWriter.cs` and
`src/Koan.Core/Hosting/Bootstrap/ProvenanceModuleExtensions.cs`
**Pattern**: Fluent writer + extension methods
**Used in**: `KoanModule.Report(ProvenanceModuleWriter module, ...)`

`ProvenanceModuleWriter` is the object passed to `Report()` for every active `KoanModule`. Use it
to contribute structured metadata to the framework boot report.

#### Full API

```csharp
// Fluent core methods (on ProvenanceModuleWriter directly)
module.Describe(string? version, string? description)      // Set version + description
module.SetStatus(string status, string? detail = null)     // "ok" | "degraded" | "error"
module.ClearStatus()                                        // Reset to default
module.SetSetting(string key, Action<ProvenanceSettingBuilder> configure)  // Structured setting
module.RemoveSetting(string key)
module.SetNote(string key, Action<ProvenanceNoteBuilder> configure)        // Structured note
module.RemoveNote(string key)
module.SetTool(string name, Action<ProvenanceToolBuilder> configure)       // Registered tool/endpoint
module.RemoveTool(string name)

// Extension methods (ProvenanceModuleExtensions)
module.AddNote(string message)                             // Quick plain-text note
module.AddTool(string name, string route,
    string? description = null, string? capability = null) // Quick tool registration
```

#### Usage Pattern

```csharp
public void Describe(ProvenanceModuleWriter module, IConfiguration cfg, IHostEnvironment env)
{
    // 1. Identify the module
    module.Describe(ModuleVersion, "My application services");

    // 2. Add plain notes (quick and simple)
    module.AddNote($"Environment: {env.EnvironmentName}");
    module.AddNote("Services: TodoService, EmailService");

    // 3. Add structured settings (show value + source)
    var connStr = Configuration.ReadWithSource(cfg, "", "Koan:Data:Default:ConnectionString");
    module.SetSetting("connection", b => b.Value("[redacted]").Source(connStr.Source.ToString()));

    // 4. Signal degraded state if optional config is absent
    if (!cfg.GetSection("Email:Smtp").Exists())
        module.SetStatus("degraded", "Email not configured — notifications disabled");

    // 5. Register tools/endpoints for ZenGarden discovery
    module.AddTool("health", "/health", "Health check endpoint");
}
```

#### When to Use
- In every `KoanModule.Report(...)` implementation that has configuration to explain
- Connector modules reporting resolved configuration
- Any module that wants to appear in the Koan boot report or ZenGarden topology

---

## Anti-Patterns to Avoid

### ❌ Don't Duplicate These Utilities

Before creating new helper methods, check if these already exist:

1. **Connection String Parsing** → Use `ConnectionStringParser`
2. **Options Configuration** → Use `OptionsExtensions`
3. **Query Parsing** → Use `EntityQueryParser`
4. **Patch Normalization** → Use `PatchNormalizer`
5. **Guard Clauses** → Use `Must`, `Be`, `NotBe` guards
6. **Discovery Logic** → Inherit from `ServiceDiscoveryAdapterBase`
7. **Config reading with source** → Use `Configuration.ReadWithSource<T>()`
8. **Boot report writing** → Use `ProvenanceModuleWriter` methods (not custom `Publish()` helpers)

### ❌ Don't Inject Services for Pure Functions

If a method:
- Has no side effects
- Doesn't need runtime configuration
- Performs pure data transformation
- Is stateless

→ Make it a **static helper** instead of an injected service.

**Example:**
```csharp
// ❌ BAD - Unnecessary service
public interface IConnectionStringParser
{
    string BuildPostgresConnectionString(string host, int port, string database);
}

// ✅ GOOD - Static utility
public static class ConnectionStringParser
{
    public static string BuildPostgresConnectionString(string host, int port, string database) { }
}
```

---

## Decision Framework

**When should I create a new utility vs. using DI?**

See [ARCH-0068: Refactoring Strategy](../decisions/ARCH-0068-refactoring-strategy-static-vs-di.md) for the complete decision framework.

**Quick Reference:**

| Use Static Utility When... | Use DI Service When... |
|----------------------------|------------------------|
| Pure functions (no side effects) | Needs configuration at runtime |
| Zero allocation on hot paths | Has mutable state |
| Used across many assemblies | Requires lifecycle management |
| Testable through inputs only | Needs mock/stub in tests |
| Examples: parsing, validation | Examples: repositories, HTTP clients |

---

## Contributing

**Adding New Utilities:**

1. Check this guide to avoid duplication
2. Follow the decision framework in ARCH-0068
3. Document the utility in this guide
4. Add contextual README if in a new directory
5. Update CLAUDE.md if it affects AI assistant guidance
6. Add unit tests in appropriate test suite

When a new utility would establish durable framework law, record that law in an ADR. Do not use an
implementation ledger as current API guidance.

The reasoning behind static-versus-injected placement is recorded in
[ARCH-0068](../decisions/ARCH-0068-refactoring-strategy-static-vs-di.md).
