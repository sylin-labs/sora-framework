# CouchDB adapter — provider contract and operations

Technical reference for maintainers. Status: **supported** (`couchdb-data-provider`); conformance proven against
`couchdb:3.5` (3.5.2) over the HTTP API with `System.Net.Http` only, .NET 10, Windows host,
2026-08-29. No driver dependency. The adapter implements the document repository contracts directly
(the document seam has no shared substrate) following the Mongo exemplar's shape.

## Verified provider facts (probe log)

| Fact | Value |
|---|---|
| Revisions | updates/deletes require `_rev`; missing → 409 conflict |
| Create/update | `PUT /{db}/{id}` (upsert = read rev then PUT); `POST /{db}/_bulk_docs` for batches |
| Keyed batch read | `POST /{db}/_all_docs?include_docs=true` with `keys` — order-preserving, `null` per missing |
| Mango | `_find` full-scans with a `no_matching_index` warning (store-executed); `sort` hard-fails `no_usable_index` without a matching index — only `_id` sorts free |
| Collection operators | `$all` (has/all), `$in` (any), `$nin` (none, present-field semantics), `$size` all verified; bare element equality parser-lowered to has |
| Element-LIKE | `$regex` does not cross array elements → undeclared, floor carries it, fact recorded |
| Reserved members | top-level `_`-prefixed members rejected (`doc_validation`); managed discriminators ride a `koan` subdocument |
| Count | no count endpoint — `_find` with `"fields":[]` materialized and counted (exact) |
| Database lifecycle | `PUT /{db}` creates (managed consent only); `GET /{db}` 404 = absent |
| Auth | Basic; 3.x disables the admin party by default; 401 reads "not a server admin" for non-admin writes |
| Errors | JSON body carries `error`/`reason` verbatim → `CouchDbException` |

## Runtime map

| Concern | Owner |
|---|---|
| Route resolution (endpoint, credentials, prefix), naming capability | `CouchDbAdapterFactory` |
| HTTP driver (auth, endpoints, status translation) | `CouchDbClient` / `CouchDbClientManager` (one client per endpoint, host-owned) |
| Entity plan (Newtonsoft serialization, temporal encodings, `koan` member encoding, identity mapping) | `CouchDbEntityPlan` |
| Capability declaration | `CouchDbFeatures` (full scalar + collection `FilterSupport`; no sort/stream/atomicity tokens) |
| Mango selector compilation | `CouchDbQueryCompiler` (canonical member paths, DATA-0100 comparands) |
| Repository surface | `CouchDbRepository` |
| Readiness (database validate/provision per source policy) | `DataSourceReadinessCoordinator` over the factory's registry plan |
| Health, discovery, options | `CouchDbHealthContributor` (`/_up`), `CouchDbDiscoveryAdapter`, `CouchDbOptions(Configurator)` |

## Design decisions

- **One database per entity container.** CouchDB's only namespace is the database; the route's
  `Database` is a prefix and the entity's storage name becomes `koan_<container>` (sanitized to
  CouchDB's database grammar). Partitions and routed sources resolve to distinct physical databases,
  which is what container/database isolation requires.
- **Managed discriminators in a `koan` subdocument.** Managed storage names are underscore-prefixed
  and CouchDB rejects top-level underscore members; the subdocument is legal, and the selector path
  `koan.<name>` keeps the isolation predicate store-enforced (the AODB row cell proves it).
- **No explicit mapping plans.** A declared map cannot keep framework-managed fields isolated on
  this store, so maps refuse at construction with that reason rather than shipping a leak.
- **Upserts are read-rev-then-PUT.** Outcome-correct, not atomic; the conditional replace rides the
  same revision as its compare-and-set. Batch writes declare `NotGuaranteed` atomicity with complete
  item outcomes, which is exactly what `_bulk_docs` gives.
- **Sorts are not claimed.** `_find` sort is index-gated; only the identity order is store-side, so
  `SortHandled` claims exactly that and everything else rides the framework sorter visibly.

## Evidence

- `Koan.Data.Connector.CouchDb.Tests` — 12 specs, all green (two consecutive runs): AODB record-plane
  conformance (isolation modes declared+realized, streaming fail-closed, polymorphic roots), the full
  filter-convergence corpus WITH the strict pushdown guard (`$like` posture pinned residual-and-
  recorded), paged windows through the declared sort fallback, capability truth, boot provenance.
- Packaging: `dotnet pack` with the release-train version; package id `Sylin.Koan.Data.Connector.CouchDb`.

Conditional replacement consumes the normalized Filter, qualifies it through CompileConditional, and
retains the observed `_rev` fence. Managed, binary and DateTime guard fields refuse before document
reads because the CLR evaluator does not establish the required comparison meaning for those shapes.
