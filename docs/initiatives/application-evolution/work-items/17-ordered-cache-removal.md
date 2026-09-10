# AE-17: ordered cache removal

Status: implemented, focused-owner verified and independently reviewed, 2026-09-09, including the accepted
LayeredCache write-settling extension. Initial characterization used `7ceb16c9f`; final verification used
the working tree on `dfe9291f7`, including AE-15's keyed-lease prerequisite.
Production owners are `src/Koan.Cache/Stores/CacheClient.cs` and `src/Koan.Cache/Topology/LayeredCache.cs`
(Write only). The seven-case ordering spec went from one intended failure to seven passes across the Write
change, and the full Cache topology owner suite went from seven intermittent fixture failures to 82/82 twice.
Independent Astra High review passed. Root owns the following scoped commit and normal public release.

## Intent and expression

Business sentence: after a successful ordinary removal of a physical cache key in this process, a GetOrAdd
factory that was already running cannot afterwards republish its pre-removal value, including the case where
the factory's own layered publication has one tier write fail while another is still in flight. The guarantee
is local ordering only. Values already returned to callers, direct setters, direct `Get`/`Set` writes, peer
processes and factories that a tag flush has not discovered remain outside it. Subsequent GetOrAdd calls
may run a new factory; Cache does not establish the freshness of that factory's source.

Public expression is unchanged, verified existing grammar:

```csharp
var value = await Cache.WithJson<Bundle>(key).GetOrAdd(factory, ct);
await Cache.WithJson<Bundle>(key).Remove(ct);
```

No new verb, option, constant, generation map, ownership registry or concept was introduced. The implemented
public semantics: a Remove that completes has been ordered after every in-flight local GetOrAdd fill of that
physical key, including the L2-hit L1 backfill and including a fill whose layered Write has a failed tier
write with another tier write still pending. A Remove that cannot acquire the existing keyed lease fails on
`Cache:DefaultSingleflightTimeout` (`TimeoutException`) or the caller's cancellation
(`OperationCanceledException`) before any eviction and claims nothing. A reentrant same-key removal from
inside a GetOrAdd factory fails the same bounded way, never bypasses, and a factory that catches that failure
may still return its value because removal did not succeed. Policy identity, broadcast, and the bool return
(an entry was removed, not durability or peer acknowledgement) are unchanged.

## Explore and ownership

Production owners: `src/Koan.Cache/Stores/CacheClient.cs` and the `Write` method of
`src/Koan.Cache/Topology/LayeredCache.cs`, and no other production file. In `CacheClient`,
`RemovePhysical` evicted and broadcast without the keyed lease, while `ExecuteGetOrAddAsync` performed an
unleased outer read and only then entered `IKeyedLeaseGate.RunAsync` under the already bound physical key for
a second read, the factory and publication. `LayeredCache.Read` backfills L1 from an L2 hit, so leasing only
the factory would still let a parked backfill resurrect L1 after eviction. Independent Astra review then
identified the second, deeper hole: `LayeredCache.Write` started both tier Set ValueTasks and awaited them
sequentially, so an asynchronously failing L1 Set propagated its error and released the GetOrAdd gate while
the L2 Set was still pending; a removal could then complete before the stale L2 publication landed.

Implemented corrections: `RemovePhysical` enters the same keyed lease with the already bound physical key,
the existing `CacheOptions.DefaultSingleflightTimeout`, and the caller's cancellation; eviction,
instrumentation and the always-on broadcast run inside the lease action without recursing through the gated
method. `ExecuteGetOrAddAsync`'s unleased outer read is deleted, so the single read, factory and publication
run under the lease. `LayeredCache.Write` still starts every tier write before awaiting any (parallel writes
unchanged), but now settles every started write before propagating any failure: the first failure, whether a
synchronous start or capability-validation throw or the first settled write fault, is captured and rethrown
through `ExceptionDispatchInfo` (same exception instance, preserved stack) only after every started write has
settled; later failures are observed but do not replace the first. Cancellation keeps its meaning and is
settled the same way. No registry, generation, transaction or new API. Deletion count: two statements (the
unleased outer `TryGetPhysicalValueAsync` read and its early return) plus the original sequential-only await
loop. AE15's Core keyed-lease lifetime correction is committed and untouched; Core source and gate tests were
not edited.

## Characterization tests

`tests/Suites/Cache/Topology/Koan.Tests.Cache.Topology/Specs/CacheRemovalOrderingSpec.cs`: real `AddKoan()`
hosts, the existing `Support/FakeCacheStore`, the real Core gate, and public `Cache` grammar. The entry
builder's `Remove` discards both the bool and the options, so the tests resolve `ICacheClient` from the host
and remove the same business key; that is the identical bind and code path the builder forwards to. One
private store subclass, `RemovalObservingStore` (`FakeCacheStore, ICacheStore`), adds a write-side hold so
tier writes, an L2-hit L1 backfill or either side of a layered publish, can be parked or failed
deterministically.

Review-driven structure, applied to every test: operations run inside `AppHost.PushScope(host.Services)`;
cleanup starts immediately after each held operation launches, releasing all barriers and draining parked
workers under a linked 10 second deadline token with a 5 second per-task drain bound; no sleeps. The
time-based cases set the existing host configuration `Cache:DefaultSingleflightTimeout` to 250 milliseconds.

The seventh case, `Layered_write_failure_settles_pending_tier_writes_before_removal_completes`, is the
two-tier failing-write regression required before the Write fix: the L2 Set is held, the L1 Set fails
asynchronously after the L2 Set has started, and a removal with a live token must fail on the configured
gate `TimeoutException` before the L2 release (a broken Write releases the factory lease on the L1 fault, so
the removal wrongly succeeded instead), with no eviction attempted on either tier; after the release the
factory fails with the L1 `IOException`, an ordinary removal succeeds against the value the settled L2 write
published, and a fresh GetOrAdd probe returns the new value with exactly one factory run. A first draft of
this case used an immediate `IsCompleted` check, passed once on the broken Write because the check raced the
asynchronously delivered first-tier fault, and was replaced with the configured gate-timeout requirement per
the lead's clarification; the stored before receipts are the corrected run.

| Test | Result before Write fix | Result after |
| --- | --- | --- |
| `Remove_waits_for_inflight_factory_and_prevents_late_repopulation` | Passed (CacheClient fix already in) | Passed |
| `Layered_read_backfill_and_removal_share_one_ordering` | Passed (CacheClient fix already in) | Passed |
| `Unrelated_key_progresses_while_a_factory_is_held` | Passed | Passed |
| `Cancelled_removal_waiting_for_the_lease_fails_boundedly_without_evicting` | Passed (CacheClient fix already in) | Passed |
| `Removal_gate_timeout_abandons_without_a_late_eviction` | Passed (CacheClient fix already in) | Passed |
| `Factory_reentrant_same_key_removal_fails_boundedly_without_bypass` | Passed (CacheClient fix already in) | Passed |
| `Layered_write_failure_settles_pending_tier_writes_before_removal_completes` | Failed: removal wrongly succeeded (`TimeoutException` expected, found null) | Passed |

Fixture scope correction, authorized by root after the lead's attribution: `ConditionalPublicationCacheSpec`
and `EntityCacheCompositionSpec` used static Entity facades while other classes start hosts in parallel, and
the assembly permits parallel classes. Each fact now holds `using var appScope =
AppHost.PushScope(host.Services);` immediately after host startup, per the tests/README own-host law. No
assertion changed, no global reset, no assembly-wide parallelization change. This converted the seven
intermittent full-suite failures into two consecutive fully green runs.

## Honest limits

`FakeCacheStore.Fetch` never enforces expiry, so the tests prove ordering only and say nothing about TTL or
expiration. Broadcast delivery is not observed; no fixture hook exists for `FrameworkSignalRuntime`, so
broadcast semantics are preserved but not re-proven. The 250 millisecond configured wait and the live-token
cancellation paths are exercised; the default five-second wait path is not. The regression proves the
asynchronous first-tier-fault case; the synchronous start/validation throw settling of earlier writes is
implemented but not separately exercised by an owned test. Values already returned to callers, direct
setters, direct `Get`/`Set` writes, peer processes, cross-host coherence and undiscovered tag factories
remain outside the contract, and passing tests do not claim otherwise. Removal now waits for a current fill
of the same key, including its tier-write settlement; the wait is bounded by the existing configured timeout
and the caller's cancellation, with no factory-duration timeout.

## Receipts

All runs: `dotnet test` (dotnet at `C:/Tools/DotNet/dotnet.exe`), Release, `--no-restore`, xUnit TRX in
`artifacts/agent-work/ae17`. No warnings in any run.

| Receipt | Exit code | Result |
| --- | --- | --- |
| `ordering-before.trx` (`ordering-before-run1.log`), before the CacheClient change (prior worker) | 1 | 6 total: 5 failed as designed, 1 unrelated-key control passed |
| `ordering-after.trx` (`ordering-after-run1.log`), after the CacheClient change (prior worker) | 0 | 6/6 passed |
| `layered-write-before.trx` (`layered-write-before-run1.log`), seventh case added, Write not yet fixed | 1 | 7 total: the write-settling case failed for the intended cause (removal wrongly succeeded instead of `TimeoutException`), 6 passed |
| `layered-write-after.trx` (`layered-write-after-run1.log`), after the Write fix | 0 | 7/7 passed |
| `scoped-fixtures-after.trx` (`scoped-fixtures-after-run1.log`), the three affected classes together | 0 | 19/19 passed |
| `cache-topology-final.trx` (`cache-topology-final-run1.log`), full owner suite | 0 | 82/82 passed |
| `cache-topology-final-run2.trx` (`cache-topology-final-run2.log`), full owner suite, stability rerun | 0 | 82/82 passed |
| `cache-topology-before.trx` / `cache-topology-after.trx` (prior worker), full suite around the CacheClient change | 1 | 12 failed/69 passed, then 7 failed/74 passed: the same seven fixture failures throughout |

Attribution of the seven former full-suite failures: they predate the CacheClient change (identical in the
reverted full-suite run), varied between runs in which theory cases failed, vanished when either affected
class ran alone (prior worker's isolated receipts), and vanished permanently once both classes selected
`AppHost.PushScope(host.Services)`, which is the tests/README own-host requirement for parallel-class
assemblies. The concrete mechanism is the static Entity facades resolving the process-default
`AppHost.Current` while another class's host starts or stops concurrently. The attribution is now proved by
the two consecutive green full-suite runs, not assumed.

No complete certification is claimed. Independent source and fixture review passed; publication and
public-package application acceptance follow separately.
