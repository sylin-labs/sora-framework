---
type: SPEC
domain: framework
title: "R13-21 - Promote MySQL and MariaDB Entity persistence"
audience: [architects, maintainers, developers, ai-agents]
status: current
last_updated: 2026-09-12
framework_version: v1.0
validation:
  status: in-progress
  scope: MySQL and MariaDB family oracles, package-only consumer, product truth, and publication
---

# R13-21 — Promote MySQL and MariaDB Entity persistence

## Outcome

Promote `Sylin.Koan.Data.Connector.MySql` as one supported MySQL-protocol Entity provider with two
explicitly proven server lines: MySQL 8.4 and MariaDB 11.8 LTS. MariaDB does not create a second Koan
package, adapter identifier, or application API; it is an accepted server behind the existing
`mysql` adapter and MySqlConnector transport.

## Architecture checkpoint

**Application intent:** an application references the connector, calls `AddKoan()`, and uses ordinary
`Entity<T>` save, query, paging, streaming, batch, conditional-write, and source-routing operations
against either a reachable MySQL or MariaDB service.

**Public expression:** the complete common path is one package reference, ordinary `AddKoan()`, an
`Entity<T>` model, and either autonomous local MySQL discovery or a standard MySqlConnector connection
string for MySQL or MariaDB. The selected database must exist. No repository or provider-registration
API enters application code.

**Guarantee/correction:** the connector preserves Entity CRUD, native filtering, stable paging,
provider-bounded streaming, batch and conditional writes, source routing, schema policy, readiness,
and declared isolation semantics on the stated MySQL and MariaDB lines. Unsupported or incompatible
schema, storage engine, SQL behavior, DDL intent, connection configuration, or server line fails at
the connector boundary rather than silently weakening the operation or selecting another store.

**Complete intent surface:** connector package reference; `AddKoan()`; Entity statics; optional
standard connection configuration; one reachable supported server; startup facts and health; the
existing provider suite run unchanged against both engines; one staged package-only consumer; product
claim and generated public truth. No additional application action exists.

**Public concepts:** the existing package, `mysql` adapter key, `MySqlOptions`, and normal connection
string are sufficient. MariaDB support is a server-compatibility guarantee, not a new provider concept.

**Coalescence:** R13-07 and R13-08 are the closest promotion patterns. Keep shared relational law in
`Koan.Data.Relational`, MySQL-protocol mechanics in the existing connector, and server-specific
compatibility at that adapter boundary. Do not create a MariaDB connector, duplicate the repository,
or add application-side flavor switches.

**Ergonomics:** choosing either engine changes only the reachable endpoint. Entity code and
IntelliSense remain identical; developers do not translate MariaDB into a second Koan vocabulary.

## Evidence boundary

Per ARCH-0120, promotion requires all five conditions:

1. Package-owned documentation states both supported server lines, guarantees, limits, and explicit
   non-claims.
2. The complete existing provider suite passes without skips against real MySQL 8.4 and MariaDB 11.8
   LTS runtimes, including shared AODB/filter/sort proofs and provider-specific schema/index tests.
3. Both are real provider boundaries; source compatibility or driver documentation alone is not proof.
4. A clean external project restores the staged package without project references, composes
   `AddKoan()`, and completes an Entity save/get/query journey against both engines.
5. The package packs cleanly, API policy remains active, the supported claim is valid, and generated
   product truth and repository coherence are current.

## Exit state

This card passes only after the evidence above is recorded with exact commands and outcomes, the
package owns one `supported-extension` claim naming both server lines, and the changed artifacts are
published through the normal release boundary. MariaDB compatibility discovered only by source review
or a partial test remains unassessed.

## Focused evidence — 2026-09-12

- real MySQL boundary: official MySQL Community Server 8.4.11 portable runtime, complete existing
  connector project 12/12 passed with zero skips;
- real MariaDB boundary: official MariaDB 11.8.9 portable runtime (archive SHA-256
  `830c46727d9278eae212ae3eca44eeb9e71b2a68704e95f344a64fba7b1963f5`), complete existing connector
  project 12/12 passed with zero skips;
- compatibility correction: MariaDB's `JSON` alias is accepted only when its exact
  `json_valid(column)` check is present, and legacy integer display widths normalize without changing
  integer storage semantics; the same final build remained green on MySQL 8.4.11;
- shared filter correction: C# 14 array `Contains` normalizes to structured `In`; the complete Data
  Filtering suite passed 112/112 with zero skips, including fail-closed comparer and lookalike controls;
- product truth: one `supported-extension` claim names both server lines behind the existing
  `Sylin.Koan.Data.Connector.MySql` package and `mysql` adapter key; generated product surface contains
  47 claims across 107 packages;
- package quality: 107/107 packages structurally ready with zero repair or review findings.

Staged package-only consumer and public release evidence are recorded below when those gates finish.
