---
type: GUIDE
domain: jobs
title: "Background Jobs How-To"
audience: [developers, architects]
status: current
last_updated: 2026-09-12
framework_version: v1.0.0
validation:
  date_last_tested: 2026-09-12
  status: verified
  scope: transaction-bound submission wording checked against the deferred coordinator and durable-prefix regression
related_guides:
  - ../reference/data/index.md
  - ../reference/web/index.md
  - performance.md
---

# Koan Jobs: Your Complete Guide

This guide walks you through Koan's background jobs, from your first `myJob.Job.Submit()` to durable, distributed pipelines that survive restarts and ride out rate limits. Think of it as a conversation with a colleague who's run a lot of background work in production—we'll start with a one-line job and build up to chains, schedules, and cooperative backoff.

Each section follows a gentle rhythm: **Concept** (what is this?), **Recipe** (how do I set it up?), **Sample** (show me the code), and **When to use it**. By the end you'll know how to model any background workload as a plain entity and let the orchestrator handle the rest.

**The one idea to hold onto:** a job is just an **entity that knows how to do work**. You write the entity and a single `Execute` method; Koan does the queuing, claiming, retrying, scheduling, and cancelling. You never see a queue, a worker, or a coordinator.

**Related Guides:**
- Modeling the entity itself? → [Data](../reference/data/index.md)
- Kicking jobs off from an API? → [Web reference](../reference/web/index.md)
- Throughput and tuning? → [Performance](performance.md)

---

## 0. Prerequisites

Jobs is a supported extension. The package reference is the activation intent:

```xml
<PackageReference Include="Sylin.Koan.Jobs" />
```

Repository samples use the equivalent project reference from a source checkout; application code is
identical either way.

That's all the wiring there is. **Reference = Intent**: adding Jobs and implementing the job interface is enough—`AddKoan()` activates your jobs and starts the orchestrator automatically.

```csharp
using Koan.Core;   // AddKoan() lives here - without this using, the call does not resolve

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKoan();   // discovers your jobs, starts the worker
var app = builder.Build();
await app.RunAsync();
```

With no data adapter referenced, jobs run in-memory (fast, ephemeral). Add a data adapter (SQLite, Postgres, Mongo, SQL Server) and the same jobs become **durable**—they survive restarts. You change nothing in your job code; see [§10](#10-durability-pick-your-tier).

**SQLite needs zero configuration**: referencing `Sylin.Koan.Data.Connector.Sqlite` is the whole step — `AddKoan()` anchors a per-app database to your content root and elects it automatically (`jobs:ledger → durable-data` in facts). Postgres/Mongo/SQL Server read their connection strings from their connector's documented keys.

---

## 1. Your first job

**Concept.** A job is an `Entity<T>` that implements `IKoanJob<T>` and provides one static `Execute` method. The entity carries the data; `Execute` does the work.

**Recipe.** Declare the entity, implement `Execute`, call `.Job.Submit()`.

**Sample.**

```csharp
using Koan.Data.Core.Model;
using Koan.Jobs;

public sealed class ThumbnailJob : Entity<ThumbnailJob>, IKoanJob<ThumbnailJob>
{
    public string SourceUrl { get; set; } = "";
    public string? ThumbUrl { get; set; }

    public static async Task Execute(ThumbnailJob job, JobContext ctx, CancellationToken ct)
    {
        job.ThumbUrl = await Thumbs.Make(job.SourceUrl, ct);   // do the work, mutate the entity
    }
}
```

Kick it off from anywhere—a controller, a service, a startup task:

```csharp
await new ThumbnailJob { SourceUrl = url }.Job.Submit();
```

The job runs in the background. Koan persists the entity, claims the job on a worker, runs `Execute`, and—if you changed it—saves the entity it handed you. When `Execute` returns, the job is **Completed**.

> **Mutate the entity you're given.** Koan loads the work-item fresh, passes it to `Execute`, and persists *that* reference if you changed it (untouched → nothing is written). So do your work on the `job` parameter—don't reload a second copy and save it yourself, or your write and Koan's will race. Need an escape hatch? A handler that genuinely owns its own persistence simply leaves the passed entity untouched; Koan then writes nothing.

**Check on it later** by the entity's id:

```csharp
JobStatus? status = await thumb.Job.Status();          // this instance
JobStatus? status = await ThumbnailJob.Jobs.Status(id); // by id, from anywhere
```

**When to use it.** Anything you don't want to block a request on: image processing, sending mail, calling a slow API, generating a report.

---

## 2. One model, many kinds of work

**Concept.** A model often needs several *kinds* of work done to it. A `PresetPackage` might be **Fetched**, **Parsed**, **Minted**, then **Published**. Each kind is an **action**. The same `Execute` handles them all and switches on `ctx.Action`.

**Recipe.** Define your action tokens as constants, submit with an action, switch on `ctx.Action`.

**Sample.**

```csharp
public static class Stage     // typed tokens — symbol-checked, stored by name
{
    public const string Fetch   = nameof(Fetch);
    public const string Parse   = nameof(Parse);
    public const string Publish = nameof(Publish);
}

public sealed class PresetPackage : Entity<PresetPackage>, IKoanJob<PresetPackage>
{
    public string Source { get; set; } = "";
    public string? Raw { get; set; }
    public Preset? Parsed { get; set; }

    public static Task Execute(PresetPackage pkg, JobContext ctx, CancellationToken ct) => ctx.Action switch
    {
        Stage.Fetch   => Fetch(pkg, ct),
        Stage.Parse   => Parse(pkg, ct),
        Stage.Publish => Publish(pkg, ctx.Services.GetRequiredService<IPublisher>(), ct),
        _             => Task.CompletedTask,
    };
}
```

```csharp
await pkg.Job.Submit(Stage.Fetch);   // run one action
```

A single-action job (§1) simply ignores `ctx.Action`. Need a service inside the handler? Reach for `ctx.Services`—it's a scoped provider.

**When to use it.** Any aggregate that flows through stages, or any model that supports several discrete operations.

---

## 3. Pipelines: chaining actions

**Concept.** When actions run in sequence, declare a `[JobChain]`. On each step's success Koan persists the (mutated) entity and **auto-advances** to the next stage. The entity *is* the state that flows down the pipeline.

**Recipe.** Add `[JobChain]` listing the stages in order. Submit the first stage; the rest follow.

**Sample.**

```csharp
[JobChain(Stage.Fetch, Stage.Parse, Stage.Mint, Stage.Publish)]
public sealed class PresetPackage : Entity<PresetPackage>, IKoanJob<PresetPackage>
{
    // Fetch sets pkg.Raw → Parse reads pkg.Raw, sets pkg.Parsed → Mint → Publish
}
```

```csharp
await pkg.Job.Submit(Stage.Fetch);   // runs Fetch → Parse → Mint → Publish
```

**Steer the chain from inside a handler:**

```csharp
ctx.StopChain();                 // finish here, don't advance
ctx.ContinueWith(Stage.Publish); // branch: go to a specific stage next instead of the default
```

Prefer to drive it yourself? Skip `[JobChain]` and enqueue the next step explicitly:

```csharp
case Stage.Fetch:
    await Fetch(pkg, ct);
    await pkg.Job.Submit(Stage.Parse);   // manual hand-off — branch freely
    break;
```

**When to use it.** Multi-step processing where each step depends on the previous one. Keep chains **linear**—for branching or parallel fan-out, drive the steps yourself.

---

## 4. Policy: retries, timeouts, lanes

**Concept.** Each action gets its own policy via `[JobAction]`—how many times to retry, how long it may run, how many can run at once, and what a failure means for the chain. Unspecified knobs inherit sensible defaults.

**Recipe.** Decorate the model with one `[JobAction]` per action that needs tuning.

**Sample.**

```csharp
[JobAction(Stage.Fetch,   Timeout = "00:10:00", MaxAttempts = 5, OnFailure = OnFailure.Abort,
                          Lane = "upstream", MaxConcurrency = 4)]
[JobAction(Stage.Publish, Timeout = "00:00:30", MaxAttempts = 1)]
public sealed class PresetPackage : Entity<PresetPackage>, IKoanJob<PresetPackage> { … }
```

- **`Timeout`** — the handler is cancelled (cooperatively, via `ct`) if it runs too long.
- **`MaxAttempts`** — failed runs retry with exponential backoff; once exhausted the job is **Failed**.
- **`OnFailure`** — `Abort` (default) stops a chain on failure; `Continue` proceeds to the next stage anyway.
- **`Lane` / `MaxConcurrency`** — a lane is a concurrency pool. By default each action is its own lane, so a slow `Fetch` never starves `Publish`. Set `Lane` to share a pool, `MaxConcurrency` to cap it.
  - **Cross-lane fairness (JOBS-0008).** The worker multiplexes claims **fairly across all non-empty lanes** (weighted fair queuing): a perpetually-fed or older lane can no longer monopolize dispatch and starve another lane — including a downstream pipeline stage whose jobs are *newer* than a deep upstream backlog. You don't configure anything to get this; equal-share fairness is the default. To bias the split, use standard options configuration: `services.Configure<JobsOptions>(o => o.LaneWeights["translation"] = 3)` gives `translation` ~3× the dispatch share of a weight-1 lane. Weights are *relative*, never strict priority — a low-weight lane is throttled, never starved.

**One entity, one job at a time.** Independently of lanes, Koan serializes jobs by work-item id: two different actions on the *same* entity (say `FetchPreview` and `DriftCheck` on one `Package`) never run concurrently—the entity is processed in order, like a FIFO group keyed by its id. Different entities still run fully in parallel. If a type's actions are genuinely independent and you want them to overlap on one instance, opt out with `[ParallelSafe]` on the class—an assertion that they don't conflict.

**When to use it.** Whenever an action talks to something rate-limited, slow, or fragile—tune its retries, timeout, and concurrency independently of the others.

---

## 5. Idempotency and coalescing

**Concept.** Background work is delivered **at-least-once**, so handlers should be safe to run twice. `[JobIdempotent]` makes that ergonomic: declare the properties that identify "the same work," and Koan collapses duplicate submissions into one.

**Recipe.** Add `[JobIdempotent]` with the key properties.

**Sample.**

```csharp
[JobIdempotent(nameof(Source), nameof(Version))]
public sealed class PresetPackage : Entity<PresetPackage>, IKoanJob<PresetPackage>
{
    public string Source { get; set; } = "";
    public int Version { get; set; }
}
```

Submit the same `(Source, Version, action)` twice and only one job runs—the second submit coalesces onto the first.

**When to use it.** Any job that could be submitted more than once for the same logical unit (a webhook retried by the sender, a user double-clicking, a sweep re-enqueuing).

---

## 6. Cooperative backoff and resource gates

**Concept.** Sometimes a job *can't* make progress right now—an upstream returned `429 Too Many Requests` with a cooldown, or a resource is briefly locked. That's **not a failure**, so it shouldn't burn a retry. Instead the handler asks to be **rescheduled**, and—optionally—sets a shared **gate** so its peers back off too.

**Recipe.** Call `ctx.Reschedule(...)` to defer this job; `ctx.Backoff(...)` to defer *and* gate the whole resource. Declare the resource with `[JobGate]` so the orchestrator can skip gated jobs **before** running them.

**Sample.**

```csharp
[JobGate(nameof(Host))]   // the resource these jobs contend for
public sealed class FetchJob : Entity<FetchJob>, IKoanJob<FetchJob>
{
    public string Host { get; set; } = "";

    public static async Task Execute(FetchJob job, JobContext ctx, CancellationToken ct)
    {
        var res = await Http.Get(job.Host, ct);
        if (res.StatusCode == 429)
        {
            // back off this host for everyone; honor Retry-After, escalate if it keeps happening
            if (ctx.State.Reschedules >= 3) ctx.Reschedule(Tomorrow9am());
            else ctx.Backoff(res.RetryAfter ?? TimeSpan.FromMinutes(5));
            return;
        }
        // …success…
    }
}
```

Once one job hits the 429 and calls `Backoff`, every other job for the same `Host` is deferred **at dispatch—without running**—until the cooldown passes. `ctx.State` gives the handler its own history (`Reschedules`, `Attempt`, `FirstSubmittedAt`, …) so it can escalate from "+5 minutes" to "tomorrow morning."

**When the gate identity isn't a property on the entity.** `[JobGate]` can name a **method** instead of a property — an async resolver that composes the key from a related entity, a registry, or anything in DI, evaluated once at submit:

```csharp
[JobGate(nameof(ResolveHostGate))]
public sealed class Mirror : Entity<Mirror>, IKoanJob<Mirror>
{
    public string PackageId { get; set; } = "";

    // the host lives on the navigated Package, not on Mirror — so resolve it
    public async Task<string?> ResolveHostGate(IServiceProvider sp, CancellationToken ct)
    {
        var pkg = await Package.Get(PackageId, ct);
        return pkg is null ? null : $"host:{new Uri(pkg.UpstreamArchive).Host}";
    }
}
```

The resolved key is frozen onto the job exactly like a property gate, so dispatch matching is unchanged and `ctx.Backoff()` (no argument) reuses it — one host's 503 defers only the siblings that resolved to the *same* host. No synthetic `Host` column on the entity. (The resolver runs at submit, so the gate identity must be knowable then — true for a host or upstream endpoint; a value chosen only at call time is out of scope.)

- `ctx.Reschedule(TimeSpan after)` / `ctx.Reschedule(DateTimeOffset until)` — defer this job; **no retry consumed**.
- `ctx.Backoff(TimeSpan after, key?)` — defer **and** gate the resource so peers wait too.

**When to use it.** Anything that calls a rate-limited or occasionally-unavailable dependency. The gate turns a thundering herd of retries into one polite wait.

---

## 7. Scheduling: run work on a cadence

**Concept.** Besides kicking a job off directly (edge-triggered), an action can be **scheduled**. On the cadence you declare, Koan submits a fresh job for that action and runs it the normal way—claim, execute, settle. It's the reconcile-loop model: edge for latency, schedule for "make sure this keeps happening."

**Recipe.** Add a `Schedule` to the action's `[JobAction]`.

**Sample.**

```csharp
[JobAction("Reconcile",   Schedule = "00:10:00")]   // every 10 minutes (a TimeSpan interval)
[JobAction("NightlySweep", Schedule = "0 2 * * *")] // cron — daily at 02:00 UTC
[JobAction("Warm",         Schedule = "@boot")]      // once at startup
[JobAction("Pump",         Schedule = "@continuous")]// every scheduler tick
public sealed class Maintenance : Entity<Maintenance>, IKoanJob<Maintenance> { … }
```

**Code-first twin.** The same grammar registers at host-configuration time through the type gateway —
useful when the cadence lives in config or composition rather than on the type:

```csharp
Maintenance.Jobs.Schedule("Reconcile", TimeSpan.FromMinutes(10));
Maintenance.Jobs.Schedule("NightlySweep", "0 2 * * *");   // or a TimeSpan / "@boot" / "@continuous"
```

One cadence per action: re-registering the same action with the same expression is idempotent;
a *different* expression fails correctively naming both. `Maintenance.Jobs.ResetSchedules()` clears
gateway registrations for the type (test isolation); attribute schedules are untouched.

`Schedule` accepts a **TimeSpan interval** (`"00:10:00"`), a **cron** expression (`"0 2 * * *"`), or a **sentinel** (`@boot`, `@continuous`). A scheduled action runs on its own **singleton** work-item, so its handler typically does the bulk operation or fans out per-entity submits:

```csharp
public static async Task Execute(Maintenance _, JobContext ctx, CancellationToken ct)
{
    foreach (var pkg in await Package.Query(p => p.NeedsRefresh, ct))
        await pkg.Job.Submit("MirrorRefresh");   // the tick fans out edge jobs; the orchestrator stays domain-ignorant
}
```

Pair a scheduled action with `[JobIdempotent]` (a stable key) so an overlapping tick **coalesces** onto the one still in flight instead of piling up.

**When to use it.** Periodic reconciliation, retries of a whole class of work, "every night at…" maintenance. It also makes the system self-healing: edge submits give low latency, the schedule guarantees the work eventually happens. To run a scheduled action **on demand**, see `Trigger` in §8.

---

## 8. Pointwise sources and the type-level control plane

**Concept.** `model.Job.X` operates on one instance; `source.Submit(...)` repeats that same ledger
acceptance pointwise for a finite selection or provider-bounded stream. `MyModel.Jobs.X` is the
type-level **control plane**—trigger a singleton action, query, cancel, or inspect status by id.

**Recipe.** Submit directly from the source. Use the static facade only for type-wide operations that
do not have an ordinary source instance.

**Sample.**

```csharp
// one instance
await pkg.Job.Submit(Stage.Fetch);

// a thousand instances, pointwise ledger acceptance with a fixed-size summary
List<PresetPackage> packages = …;
JobSubmission selected = await packages.Submit(Stage.Fetch);

// the same intent over a lazy, provider-bounded Entity source
JobSubmission streamed = await PresetPackage
    .QueryStream(pkg => pkg.Ready)
    .Submit(Stage.Fetch);

// run a type-level action now, with no instance (the on-demand twin of a schedule)
await PresetPackage.Jobs.Trigger(Stage.Discover);

// the type's job subsystem
await PresetPackage.Jobs.Cancel(id);
var running = await PresetPackage.Jobs.WithStatus(JobStatus.Running);
var mine    = await PresetPackage.Jobs.Query(new JobQuery(Action: Stage.Fetch));
```

**What the result means.** Scalar `Submit` and type-level `Trigger` return a `JobHandle`, so you can
`await (await …).Completion(timeout)` to run-and-wait. Source `Submit` returns a fixed-size
`JobSubmission`: `Submitted` is new ledger acceptance, `Coalesced` is explicit declared-idempotency
acceptance, and `Accepted` is their sum. It reports acceptance, not handler completion, and never
retains one handle per source item. `PendingCommit` means new records are still contingent on the
ambient transaction.

If enumeration or an item submission fails, `JobSubmissionException` carries the confirmed prefix;
cancellation uses `JobSubmissionCanceledException`, which is still an `OperationCanceledException`.
There is no collection-atomicity claim. A provider call that throws is not counted as confirmed
acceptance, so retry the failing item under the job's declared idempotency policy.

**When to use it.** `source.Submit(action)` for bounded pointwise fan-out; `Jobs.Trigger(action)` for
"run the nightly sweep now" admin actions; `MyModel.Jobs` for dashboards and operating on jobs by id.

### 8.1 Draining a large source: window, don't multiply

**Concept.** `source.Submit(action)` mints one job *per item*. Its async form keeps producer memory
bounded and applies sequential backpressure, but it deliberately does not bound ledger growth. That's
exactly right for hundreds or a few thousand. Reach for it on "import a million rows from an external
feed" and you still mint a million ledger rows—millions of writes, a saturated active set, and slow
dashboards. The ledger is a **lifecycle/audit store** (durable claim, retry, per-row history); that
machinery is worth paying for a *job* and pure overhead a million times over for homogeneous rows. The
fix isn't a faster engine—it's a coarser **unit of work**: make the **window** the job, not the row.

**Recipe.** Model a *window* over the source as the work-item. Each run processes one window (a bulk `Save`), advances its own cursor, and re-queues itself at the next window with `ctx.ContinueWith`. At most one window is in flight, so the source drains through a handful of ledger rows instead of a million.

**Sample.**

```csharp
public sealed class ImportWindow : Entity<ImportWindow>, IKoanJob<ImportWindow>
{
    public const string Pull = nameof(Pull);
    public string Source { get; set; } = "";
    public long   Offset { get; set; }
    public int    Size   { get; set; } = 1000;

    [JobAction(Pull)]                                    // serialized per work-item by default (§17.2)
    public static async Task Execute(ImportWindow w, JobContext ctx, CancellationToken ct)
    {
        var page = await ExternalSource.Page(w.Source, w.Offset, w.Size, ct);
        await page.Rows.Save();                          // one bulk upsert of the window
        ctx.Progress(page.Done ? 1 : (w.Offset + page.Count) / (double)page.Total);
        if (!page.Done)
        {
            w.Offset += page.Count;                      // advance the cursor on THIS entity (auto-saved)
            ctx.ContinueWith(ImportWindow.Pull);         // re-queue the same work-item at the next window
        }
    }
}

await new ImportWindow { Source = "vendor:catalog" }.Job.Submit(ImportWindow.Pull);   // ONE ledger row
```

`ctx.ContinueWith(action)` re-queues the *same* work-item, and Koan persists your cursor automatically (it saw `Offset` change). Want parallelism without the explosion? Start a few conveyors over disjoint stripes (`Offset ≡ stripe (mod P)`) — `P` in flight, not a million.

**When to use it.** Any time the source is large or external (tens of thousands of items and up). If you do mint a job per row at scale, Koan won't stop you — but the worker will log a **job-per-row warning** (`JobPerRowWarnThreshold`) pointing right back here.

---

## 9. Cancellation

**Concept.** Cancellation is durable and cross-process. A queued job is cancelled before it runs; a running job is asked to stop cooperatively through its `CancellationToken`.

**Sample.**

```csharp
await pkg.Job.Cancel();                  // this instance
await PresetPackage.Jobs.Cancel(id);     // by id, from any node
```

Honor it in long-running handlers by passing `ct` to the calls you `await`. A cancelled job ends in the **Cancelled** state.

**When to use it.** User-initiated "stop," superseded work, draining before shutdown.

---

## 10. Durability: pick your tier

**Concept.** The same job code runs across tiers; the infrastructure you reference decides durability and scale—never correctness. The delivery contract (at-least-once, idempotent) is constant everywhere.

| You have… | Jobs are… |
|---|---|
| no data adapter | in-memory, fast, lost on restart |
| a data adapter (SQLite/Postgres/Mongo/SQL Server) | **durable**—survive restarts |
| several nodes on the same store | **distributed**—nodes share the work |

**Cross-node wake cadence (numbers).** With a durable ledger, every submission bumps a one-row stamp in
the same transaction and each node probes it every 250 ms (`JobsOptions.WakeProbeInterval`), so peers
pick up new work well under one second; the full claim scan still runs at most every `PollInterval`
(1 s default). A restarted host claims due work within roughly a second of boot — size kill/restart
tests accordingly. Referencing a Communication connector makes wake signal-instant instead (JOBS-0009).

**Per-type control.** Override the tier for one model with `[JobPersistence]`:

```csharp
[JobPersistence(JobPersistenceMode.InMemory)]   // keep this high-churn job ephemeral
public sealed class PingJob : Entity<PingJob>, IKoanJob<PingJob> { … }
```

`Auto` (default) follows your adapters; `InMemory` keeps a job's queue state volatile even when a store is present (the orchestration is ephemeral—handy for a torrent of fire-and-forget work you don't want touching the database); `DataStore` insists on durability. Mixed tiers coexist in one app: durable and in-memory jobs run side by side, and a cooperative gate (§6) set by one is honored by the other—so a 429 backoff still protects a shared host across tiers.

`DataStore` is corrective rather than aspirational: when no durable Data adapter is available, host composition fails
and names the affected work types. Koan never silently weakens that declaration to in-memory execution.

**Claim behavior.** When several nodes compete, Jobs automatically uses the selected Data adapter's conditional
compare-and-set capability. SQLite, PostgreSQL, SQL Server, and MongoDB each mark a ready job `Running` atomically,
so competing consumers cannot both win that ledger row. An adapter that does not declare conditional replace retains
the constant at-least-once contract through an honest optimistic fallback; handler idempotency remains required on
every tier. There is no claim-strategy setting or clock-skew-sensitive election protocol to choose.

**When to use which.** Single service or multiple replicas → use the automatic claim path. A torrent of
fire-and-forget pings you do not want cluttering the store → `[JobPersistence(InMemory)]`.

**Lane fairness is per node.** Each worker fairly multiplexes the lanes *it* claims (§4), so no lane starves on any node. Lane weights are honored per node too — so across many nodes the global split tracks the weights when feed is balanced across them, and stays approximate (but never starving) under node-asymmetric feed. Exact global weight proportions are deliberately *not* bought with per-claim cross-node coordination; see ADR JOBS-0008.

**Wake latency.** Out of the box, Jobs emits a bounded internal Communication wake
hint and otherwise polls at `PollInterval`. The process-local provider requires no configuration. If
the application directly references a Communication connector that claims framework signals, such as
RabbitMQ, the same hint uses
the elected mesh automatically—there is no Jobs transport package or bus registration. Replicas
compete to wake and claim from the shared ledger. This is purely a latency upgrade: the ledger is
still the truth, so a dropped or duplicated signal costs at most one poll interval and never
correctness.

**Deferred submit (not an outbox).** On the durable tier, a `Submit` inside an ambient transaction is
tracked with the surrounding Entity operations. It is dispatched in sequence on `Commit` and discarded
if the scope is rolled back before commit begins:

```csharp
using (EntityContext.Transaction("publish"))
{
    await order.Save();
    var submission = await new[] { order }.Submit(Stage.Notify);
    Debug.Assert(submission.PendingCommit); // accepted, but visible only if the transaction commits
    await EntityContext.Commit();
}
```

No configuration is required whenever a transaction is in scope. This coordination is not atomic: a
failure after the Entity save but before the job ledger write can leave the Entity durable without the
job, while the reverse order can leave a job without a later Entity write. A business invariant that
requires a durable outbox must model the intent in one atomic aggregate or add application-owned,
idempotent recovery; the ambient coordinator alone does not provide that guarantee.

**Retention.** A sweep keeps the active ledger lean three ways, all configurable in `JobsOptions` and all running every `ArchiveInterval` (default 1 h):

- **Completed/Cancelled** older than `ArchiveAfter` (default **7 days**) are purged.
- **Failed/Dead** older than `FailedAfter` (default **30 days**) are purged — queryable and replayable until then. Set `FailedAfter` to zero to retain failures indefinitely.
- A per-work-type **count cap**, `RetainPerWorkType` (default off): keep only the newest N terminal rows of a type and trim the rest — a burst guard that age windows alone miss.

Set any window to zero to disable it. For a high-throughput type, prefer a short `FailedAfter` plus a cap — and don't mint a job per row in the first place (§8.1). On a **TTL-capable store (Mongo)** each terminal row also carries an absolute `ExpireAt` (its settle time + the per-outcome window) and a native TTL index, so the store expires rows **continuously between sweeps** at zero app cost; other stores rely on the sweep alone, which remains the universal backstop everywhere.

**Throughput metrics (opt-in).** Active counts ("how many Queued/Running?") come straight from the indexed ledger and need nothing extra. For **throughput/trend history that survives retention** — completed/failed per period — turn on the rollup:

```csharp
builder.Services.Configure<JobsOptions>(o => o.MetricsEnabled = true);
```

Each node tallies terminal outcomes in memory and periodically flushes its own internal rollup shard, so dashboards read pre-aggregated counts cheaply and they **outlive the rows they counted** (retention deletes the `JobRecord`s; the counts remain):

```csharp
var since = DateTimeOffset.UtcNow.AddDays(-1);
IReadOnlyDictionary<string, long> byOutcome =
    await JobMetrics.Summary(typeof(ImportJob).FullName!, since, DateTimeOffset.UtcNow);
// { "Completed": 18234, "Failed": 12, … }
```

Off by default (the zero-config path stays write-free); the flush cadence is `MetricsFlushInterval` and the rollup itself is trimmed by `MetricsRetention`.

**Health signal (always on).** The Jobs pillar publishes an `IHealthContributor` (`Koan.Jobs`) to the standard `/health` surface: queued/running depth, reclaim backlog, and the **oldest-queued-age** — the cheap global signal that makes a stalled or starved queue self-evident instead of something you infer hours later. Set `o.QueueAgeWarning = TimeSpan.FromMinutes(5)` to flip the contributor to **Degraded** when the oldest job has waited past the budget (off by default — the facts are always published for scraping, the Degraded *signal* is opt-in).

---

## 11. Lifecycle observers: react without touching the job

**Concept.** Sometimes something should happen *because* a job finished — update a dashboard, fire a
webhook, log a trend — and the job itself shouldn't know or care. Lifecycle observers are
type-scoped hooks: **past participles** that observe after the fact, never intervene, and run after
the durable write. A throwing observer is swallowed by design — settlement is never affected.

**Recipe.** Register observers on the type gateway at host configuration time.

**Sample.**

```csharp
Poke.Jobs.OnCompleted(e => Console.WriteLine($"{e.Model?.Id} canonized"));
Poke.Jobs.OnFailed(e => logger.LogWarning("Poke failed: {Error}", e.Error));
Poke.Jobs.OnStalled(e => logger.LogWarning("Poke stalled — reclaimed from a dead worker"));
```

Full set: `OnClaimed`, `OnCompleted`, `OnFailed`, `OnDeadLettered`, `OnRescheduled`, `OnCancelled`,
`OnStalled`, `OnAbandoned` (this node lost the claim mid-flight and settled nothing). Each event
carries `Record` (the ledger row), `Model` (your typed Entity, when the pipeline has it loaded),
`Kind`, and `Error`. `Poke.Jobs.ResetEvents()` clears observers for the type (test isolation).

---

## 12. Testing your jobs

**Concept.** Jobs are testable without background timers. Inline mode is useful for the smallest handler test.
For schedules, leases, retries, deferrals, and chain boundaries, the xUnit-free
`Sylin.Koan.Jobs.Testing` package drives the production engine deterministically.

**Sample.**

```csharp
services.Configure<JobsOptions>(o => o.Mode = JobMode.Inline);

await new ThumbnailJob { SourceUrl = url }.Job.Submit();   // runs now, synchronously
var saved = await ThumbnailJob.Get(id);
saved!.ThumbUrl.Should().NotBeNull();
```

For schedules, timeouts, and deferrals, inject a `TimeProvider` (Koan uses the standard
`System.TimeProvider`) and a fake clock to **advance time** instead of waiting.

**When to use it.** Always—assert your handler's logic and chain decisions in milliseconds.

### Drive the production engine

Configure the same host the application test already owns:

```csharp
services.Configure<JobsOptions>(options =>
{
    options.EnableWorker = false;
    options.Mode = JobMode.Normal;
});

var driver = JobsTestDriver.From(host.Services);
```

`DrainAsync` runs all currently-ready work. Advance the test's fake clock, then call `TriggerDueAsync`,
`ReapAsync`, or `DrainAsync` explicitly. No sleep or second host is involved.

To assert one stage without running its successor, call
`driver.RunStageAsync(workItem, action)`. It returns:

| Field | What it holds |
|---|---|
| `result.Run.Signal` | The `JobSignal` the handler raised (`None`, `StopChain`, `ContinueWith`, `Reschedule`, `Backoff`) |
| `result.Run.NextAction` | The action passed to `ctx.ContinueWith(...)`, if any |
| `result.Run.DeferUntil` | The time the handler rescheduled to, if any |
| `result.Settled` | The `JobRecord` after settle (inspect `Status`, `Attempt`, etc.) |
| `result.Successor` | The next-stage `JobRecord` the chain appended, or null if stopped/rescheduled |

**Sample — two-stage chain:**

```csharp
var pipeline = new Pipeline();

var result = await driver.RunStageAsync(pipeline, Stage.Fetch);

// Assert the control signal
result.Run.Signal.Should().Be(JobSignal.None);   // default chain advance

// Assert settle
result.Settled.Status.Should().Be(JobStatus.Completed);

// Assert chain advancement -- Parse was appended but NOT run yet
result.Successor!.Action.Should().Be(Stage.Parse);
result.Successor.Status.Should().Be(JobStatus.Queued);

// Assert entity mutations (auto-save persisted the handler's writes)
var saved = await Pipeline.Get(pipeline.Id);
saved!.Fetched.Should().Be("raw");
saved.Trail.Should().Equal(Stage.Fetch);   // Parse has NOT run
```

**Asserting explicit signals:**

```csharp
// StopChain -- no successor appended
result.Run.Signal.Should().Be(JobSignal.StopChain);
result.Successor.Should().BeNull();

// ContinueWith -- branches to a named action
result.Run.Signal.Should().Be(JobSignal.ContinueWith);
result.Run.NextAction.Should().Be("MyBranchAction");
result.Successor!.Action.Should().Be("MyBranchAction");

// Reschedule -- same stage re-queued
result.Run.Signal.Should().Be(JobSignal.Reschedule);
result.Run.DeferUntil.Should().NotBeNull();
result.Settled.Status.Should().Be(JobStatus.Queued);
result.Successor.Should().BeNull();
```

The driver uses whichever Data adapter and settings the host composed, including a real external provider. It
contains no storage substitute or assertion framework.

**Driving multiple stages sequentially.** Call `RunStageAsync` once per stage and assert intermediate state after each:

```csharp
var stage1 = await driver.RunStageAsync(pipeline, Stage.Fetch);
stage1.Successor!.Action.Should().Be(Stage.Parse);

// Reload the mutated entity and run the next stage
var fetchedPipeline = await Pipeline.Get(pipeline.Id);
var stage2 = await driver.RunStageAsync(fetchedPipeline!, Stage.Parse);
stage2.Settled.Status.Should().Be(JobStatus.Completed);
```

---

## 13. The `JobContext` at a glance

Everything a handler needs arrives on `ctx`:

| Member | What it gives you |
|---|---|
| `ctx.Action` | the stage being executed (empty for single-action jobs) |
| `ctx.Services` | a scoped `IServiceProvider` for DI inside the handler |
| `ctx.Cancellation` | the token to honor (also the per-action timeout) |
| `ctx.Logger` | a logger for this job |
| `ctx.State` | read-only history: `Attempt`, `Reschedules`, `FirstSubmittedAt`, `LastError`, … |
| `ctx.Progress(0.5, "…")` | report durable progress (surfaced to dashboards) |
| `ctx.ContinueWith(action)` / `ctx.StopChain()` | steer a chain |
| `ctx.Reschedule(after \| until)` / `ctx.Backoff(after, key?)` | cooperative backoff |

---

## 14. Quick reference

```csharp
// Usings for everything below: Koan.Data.Core.Model (Entity<>, IKoanJob<>), Koan.Jobs (attributes, JobContext)

// Author
public sealed class MyJob : Entity<MyJob>, IKoanJob<MyJob>
{
    public static Task Execute(MyJob job, JobContext ctx, CancellationToken ct) => …;
}

// Ledger row (what Query/WithStatus return) — JobRecord : Entity<JobRecord>, so Id is a string
// Key fields: WorkType, WorkId, Action, Status (JobStatus), Attempt, VisibleAt, Owner,
//             ProgressFraction/Message, LastError, FirstSubmittedAt, Transitions

// Attributes
[JobChain(A, B, C)]                                  // linear pipeline
[JobAction(A, Timeout="…", MaxAttempts=…, OnFailure=…, Lane="…", MaxConcurrency=…, Schedule="…")]
[JobIdempotent(nameof(Key))]                          // dedupe + coalesce
[JobGate(nameof(Host))]                               // shared backoff key — a property, or an async resolver method
[JobPersistence(JobPersistenceMode.InMemory)]         // per-type durability
[ParallelSafe]                                        // opt out of per-entity serialization

// Submit & operate
await myJob.Job.Submit();                 await myJob.Job.Submit(action);
await myJob.Job.Submit(action, TimeSpan.FromMinutes(5));   // `after` is a TimeSpan delay
await myJob.Job.Submit("", TimeSpan.FromSeconds(10));      // single-action jobs: pass "" as the action to delay one
var handle = await myJob.Job.Submit();
var outcome = await handle.Completion(TimeSpan.FromSeconds(30));  // JobOutcome { Status, Error }; ledger-polled
await myJob.Job.Cancel();                 await MyJob.Jobs.Cancel(id);
await myJob.Job.Status();                 await MyJob.Jobs.WithStatus(JobStatus.Running);   // → IReadOnlyList<JobRecord>
// All .Job / .Jobs surfaces are ambient: they require the Koan host to be STARTED (after app.StartAsync()).
// Querying before startup throws KoanHostContextException naming the missing service.
await MyJob.Jobs.Trigger(action);         // type-level action, no instance (schedule's on-demand twin)
MyJob.Jobs.Schedule(action, TimeSpan.FromMinutes(5));   // code-first schedule — same grammar as [JobAction(Schedule=)]
MyJob.Jobs.ResetSchedules();              // clear gateway registrations for this type (tests)

// Lifecycle observers — past participles: they observe, never intervene. Fired after the durable
// write; a throwing observer is swallowed by design. ResetEvents() clears them (tests).
MyJob.Jobs.OnCompleted(e => Console.WriteLine($"{e.Record.WorkId} done"));   // e.Model is your typed Entity
MyJob.Jobs.OnFailed(e => Console.WriteLine(e.Error));     // terminal failure (retries exhausted)
MyJob.Jobs.OnClaimed(e => { });                           // engine claimed the job
MyJob.Jobs.OnDeadLettered(e => { });                      // carrier/unregistered/perpetual-deferral terminal
MyJob.Jobs.OnRescheduled(e => { });                       // cooperative deferral (reschedule / backoff)
MyJob.Jobs.OnCancelled(e => { });                         // cancelled
MyJob.Jobs.OnStalled(e => { });                           // lease lapse or confirmed worker death
MyJob.Jobs.OnAbandoned(e => { });                         // this node lost the claim mid-flight

// From a handler
ctx.Progress(0.4, "…");  ctx.ContinueWith(next);  ctx.StopChain();
ctx.Reschedule(5.Minutes());  ctx.Backoff(retryAfter);  // (TimeSpan helpers are illustrative)

// Tune — services.Configure<JobsOptions>(o => …)
o.ArchiveAfter;  o.FailedAfter;  o.RetainPerWorkType;   // retention: completed/failed windows + per-type cap (§10)
o.ClaimScanBatch;                                       // bounded per-lane claim seek window (§10)
o.LaneWeights["translation"] = 3;                       // lane-fair dispatch: relative per-lane weight (default 1 = equal share, §4)
o.QueueAgeWarning = TimeSpan.FromMinutes(5);            // health: oldest-queued-age tripwire → /health Degraded (default off, §10)
o.JobPerRowWarnThreshold;                               // warn when a work-type looks like job-per-row (§8.1)
```

That's the whole surface. Write the entity, write `Execute`, and let Koan run it—reliably, on whatever infrastructure you've got.
