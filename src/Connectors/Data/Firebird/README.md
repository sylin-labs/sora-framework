# Sylin.Koan.Data.Connector.Firebird

Firebird 5 relational storage for Koan. Reference the package, call `AddKoan()`, and `Entity<T>`
persistence runs against a Firebird server — same `Save`/`Get`/`Query`/`Page` vocabulary as every
Koan store.

> **Supported provider** (`firebird-data-provider`): the record-plane conformance suite passes against a
> real Firebird 5 server — conformance, filter convergence, sort and paging oracles, capability truth —
> and the connector is machine-checked under NativeAOT by `scripts/aot-verify.ps1`. The Limits below are
> part of the claim.

## Zero configuration

Start Firebird with its root password and use the app unchanged — the adapter resolves `auto` to
the conventional local endpoint, composes the engine's shipped `SYSDBA`/`masterkey` credentials,
and creates `koan.fdb` on first use (managed lifecycle) if the file does not exist yet:

```powershell
docker run -d -p 3050:3050 -e FIREBIRD_ROOT_PASSWORD=masterkey firebirdsql/firebird:5.0.4
```

Discovery health-checks the server, not the database: a fresh container where `koan.fdb` does not
exist yet is discoverable. Point at a different database or credentials with
`Koan:Data:Firebird:Database` / `UserId` / `Password` or a full connection string.

## Install

```powershell
dotnet add package Sylin.Koan.Data.Connector.Firebird
```

```json
{ "Koan": { "Data": { "Sources": { "Default": {
  "Adapter": "firebird",
  "ConnectionString": "DataSource=localhost;Port=3050;Database=/var/lib/firebird/data/koan.fdb;User=SYSDBA;Password=masterkey;Charset=UTF8" } } } } }
```

A `firebird://user:password@host:port/database` URI is accepted and normalized to the same form.
The database is a file on the server; under managed lifecycle policy the adapter creates it when
absent (`FbConnection.CreateDatabase`) before the first table DDL.

## What it adds

| Capability | State |
|---|---|
| `Query.Linq` / `Query.String` / `Query.Filter` (scalar operators) | declared and store-executed |
| Isolation: row / container / database scoped | declared, proven by the record-plane suite |
| Atomic batch, bulk upsert/delete, fast remove, conditional replace | declared |
| Collection filter operators (`$in`/`$all`/`$size` on `List<T>`) | **not declared** — Firebird has no JSON functions; the framework floor answers them, and the query records a runtime fallback fact |
| Nested-path filters | **not declared** (`NestedPaths=false`) — top-level scalars only |
| Provider-bounded paging (streams) | **not declared** — streaming rejects with `QueryStreamRejectedException`; use `Page` |

## How it works without JSON functions

The shared mapping stores an entity as one JSON document column. Other relational adapters lower
paths inside it with store JSON functions; Firebird 5 has none. This adapter mirrors every top-level
scalar into a plain **shadow column** — created by the schema executor, written beside the document
from the same encoded value, read by filters and sorts as a flat column — so scalar predicates and
orders are still answered by the store, and framework-managed isolation discriminators are still
enforced at the row level. Deeper paths refuse with a corrective message.

## Firebird server notes (learned the hard way)

- The official `firebirdsql/firebird` image accepts `FIREBIRD_CONF_*` variables as `firebird.conf`
  keys — spelled exactly as in the file (`FIREBIRD_CONF_WireCrypt`, not `FIREBIRD_CONF_WIRECRYPT`).
- The FirebirdClient cannot negotiate `WireCrypt=Required` or the Srp256-only default auth set: start
  the server with `FIREBIRD_CONF_WireCrypt=Enabled` and `FIREBIRD_CONF_AuthServer="Srp256, Srp"`, or
  every connection fails with a misleading "user name and password are not defined".
- The SYSDBA password is set by `FIREBIRD_ROOT_PASSWORD`; `ISC_PASSWORD` is ignored by this image.

The container fixture in the test project wires all of this; it is the working reference.

## Limits

- Identifier cap: 63 bytes (Firebird); default naming style is `HashedNamespace`, which keeps table
  names well under it.
- Strings: non-identity text columns are `VARCHAR(8191)`; oversize values fail with a corrective
  truncation error rather than being clipped.
- No `TRUNCATE` on this engine — `RemoveStrategy.Fast` is a plain `DELETE FROM`.
- Schema validation reports column presence and primary-key membership; it does not compare column
  definitions (a store type a CLR type cannot see is not faked), matching the SQLite/DuckDB posture.

## Enum storage contract

Default Entity storage preserves enum names as strings, including nullable values, nested values, collections,
and declared EnumMember aliases. Unnamed numeric values fail instead of silently changing the storage format.
Queries use the same spelling. Ordinary enum ordering uses declared ordinal ranks in native expressions while
the stored value stays a string; native ordering of arbitrary Flags combinations rejects correctively.
An explicit external mapping codec remains responsible for its declared physical representation.
Existing numeric rows or columns require a separate, explicit migration; upgrades do not rewrite them automatically.
