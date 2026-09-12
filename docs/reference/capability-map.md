---
type: REFERENCE
domain: framework
title: "Koan Capability Map"
audience: [developers, architects, ai-agents]
status: current
last_updated: 2026-08-30
framework_version: v1.0.0
validation:
  date_last_tested: 2026-08-19
  status: verified
  scope: capability-to-package parity against the shipped inventory and claim ledger, and recipe link resolution
---

# Capability map

Translate an outcome into Koan pieces, name the exact package, then confirm behavior in the linked
recipe. This file carries what you need to *choose*; the links carry what you need to *build*.

It is the public index. Any coding agent can fetch it directly — no plugin, no local inspection — and
go from "I want to add search by meaning" to the exact package and its recipe in one hop. Fetch the
current revision rather than a pinned one: a frozen map hides every capability shipped since that tag.

## Start from what they actually said

This file is a lookup, and a lookup is the wrong first move for a vague ask. Route by the shape of the
request:

| They said | Start here |
|---|---|
| A vague outcome — *"I want to add AI"*, *"make it multi-tenant"* | the [recipe index](../recipes/index.md). Read it against the application in front of you and compose the answer: **Works if** says which recipes are a small step from here, **Costs** says what each would add to operate. |
| A named piece — *"add Mongo"*, *"use SqliteVec"* | the tables below |
| Only *"can this talk to X?"* | the [connector matrix](connector-matrix.md) — every provider Koan ships, on one screen, without reading this page |

Answering a vague ask with a package name skips the only part that mattered — which of five different
things they wanted, and whether the runtime they need exists at all.

## How to use this file

1. **Choose from the shelf below.** Package identifiers are exact — they are not derivable from a
   product name (`MongoDB` is `…Data.Connector.Mongo`, `PostgreSQL` is `…Data.Connector.Postgres`).
   Copy them; never construct them.
2. **A row is an outcome, not a package.** The relation runs both ways, so read the row, not the
   identifier:
   - **One outcome can need several packages.** Check *Also needs* before installing. Authentication
     needs a provider; media needs somewhere to put bytes; vector search needs an index. A `—` means
     the row stands on its own. Installing only the named package and stopping is the most common way
     to end up with a capability that composes but does nothing.
   - **One package can serve several outcomes,** so the same identifier appears in more than one row.
     That is not duplication — it is the same piece answering different business questions, and each
     row's recipe is the one that matters for that question.
3. **Open the linked recipe before writing code** against a piece you have not used in this
   application. It carries the install command, configuration keys, working code, and provider
   limits. If you cannot retrieve it, say so and use only what this file states.
4. **Prefer what the application already resolves.** Once a package is referenced, its own README is
   on disk beside the restored package and matches the version actually in use.

Every package identifier begins `Sylin.Koan.`. Namespaces remain `Koan.*`; the package and the
namespace deliberately differ.

The complete capability-to-package authority is
[product surface](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/product-surface.md).
Runnable end-to-end examples are in
[samples](https://github.com/sylin-org/koan-framework/blob/main/samples/README.md).

## Assessment

Most of this shelf carries a product claim: its behavior is assessed and evidenced. A few pieces ship
without one — they are installable and documented, but nothing has been promised about them.

| Package | Why it is listed |
|---|---|
| `Sylin.Koan.Storage.Connector.S3` — **not assessed** | Remote object storage; its own README calls it shelved — prefer the local path |
| `Sylin.Koan.AI.Connector.HuggingFace` — **not assessed** | The only hosted-model connector; the local providers are assessed |
| `Sylin.Koan.Cache.Adapter.Redis` — **not assessed** | The only shared cache; the durable local adapter is assessed |
| `Sylin.Koan.Data.AI` — **not assessed** | Owns `[Embedding]` and the `Entity.Ai` gateway — type-scoped semantic search and embed — which the search-by-meaning recipe teaches as the shortest path to vector indexing |
| `Sylin.Koan.Data.Vector.Connector.PgVector` — **not assessed** | Reuses an application's PostgreSQL service for exact vector search when the `vector` extension is available |
| `Sylin.Koan.Data.Vector.Connector.MongoAtlasVector` — **not assessed** | Reuses an application's Atlas deployment for exact vector search without adding another service |

Recommend one only when the outcome needs it, and say plainly that it is not assessed. The complete
picture is in the
[product surface](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/product-surface.md).

A reference declares availability. It does not prove provider parity, external infrastructure, or a
particular operation. State an evidence gap plainly and offer the nearest honest seam.

## Start here

| Piece | Package |
|---|---|
| Scaffold a new application | `Sylin.Koan.Templates` |
| Web application bundle | `Sylin.Koan.App` |
| Foundation bundle (no Web) | `Sylin.Koan` |

[Quickstart](https://github.com/sylin-org/koan-framework/blob/main/docs/getting-started/quickstart.md)
· [Adopt an existing app](https://github.com/sylin-org/koan-framework/blob/main/docs/getting-started/adopt-existing-app.md)

## Data

Pick exactly one Entity store unless the application genuinely owns more than one.

| Store | Package | Also needs | Recipe |
|---|---|---|---|
| JSON (file-backed) | `Sylin.Koan.Data.Connector.Json` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Json/README.md) |
| InMemory | `Sylin.Koan.Data.Connector.InMemory` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/InMemory/README.md) |
| SQLite | `Sylin.Koan.Data.Connector.Sqlite` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Sqlite/README.md) |
| DuckDB (embedded analytics) | `Sylin.Koan.Data.Connector.DuckDb` + `Sylin.Koan.Data.Connector.DuckDb.Native` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/DuckDb/README.md) |
| Declared analytics questions (named aggregations, materialized projections, agent asks) | `Sylin.Koan.Data.Analytics` + `Sylin.Koan.Data.Analytics.Web` | **one elected engine** — `Sylin.Koan.Data.Connector.DuckDb` (+ `.Native`) | [How-to](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/data/entity-analytics.md) · [Recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/recipes/entity-analytics.md) |
| MongoDB | `Sylin.Koan.Data.Connector.Mongo` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Mongo/README.md) |
| PostgreSQL | `Sylin.Koan.Data.Connector.Postgres` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Postgres/README.md) |
| MySQL or MariaDB | `Sylin.Koan.Data.Connector.MySql` | **a reachable MySQL 8.4 or MariaDB 11.8 LTS server** | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/MySql/README.md) |
| SQL Server | `Sylin.Koan.Data.Connector.SqlServer` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/SqlServer/README.md) |
| Redis | `Sylin.Koan.Data.Connector.Redis` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Redis/README.md) |
| Couchbase | `Sylin.Koan.Data.Connector.Couchbase` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Couchbase/README.md) |
| CockroachDB | `Sylin.Koan.Data.Connector.Cockroach` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Cockroach/README.md) |
| CouchDB (document store) | `Sylin.Koan.Data.Connector.CouchDb` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/CouchDb/README.md) |
| Firebird | `Sylin.Koan.Data.Connector.Firebird` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Firebird/README.md) |

Additive data behavior:

| Outcome | Package | Also needs | Recipe |
|---|---|---|---|
| Recoverable deletion (restore removed Entities) | `Sylin.Koan.Data.SoftDelete` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Data.SoftDelete/README.md) |
| Move the active default store, verifiably | `Sylin.Koan.Data.Cutover` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Data.Cutover/README.md) · [Guide](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/data/default-route-cutover.md) |

Do not hand-roll a deleted flag, a restore endpoint, or a copy script when the owning package exists.

[Entity recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/entity-capabilities-howto.md)
· [Data reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/data/index.md)

## Web

| Outcome | Package | Also needs | Recipe |
|---|---|---|---|
| HTTP conventions and `EntityController<T>` | `Sylin.Koan.Web` | — | [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/web/index.md) |
| Shaping, hooks, and projection add-ons | `Sylin.Koan.Web.Extensions` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Web.Extensions/README.md) |
| OpenAPI description | `Sylin.Koan.Web.OpenApi` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Web.Extensions/README.md) |
| Server-sent events | `Sylin.Koan.Web.Sse` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Web.Extensions/README.md) |
| Social/link preview cards | `Sylin.Koan.Web.OpenGraph` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Web.OpenGraph/README.md) |
| Authenticated development diagnostics | `Sylin.Koan.Web.Admin` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Web.Admin/README.md) |

## Trust and isolation

| Outcome | Package | Also needs | Recipe |
|---|---|---|---|
| Authentication and authorization | `Sylin.Koan.Web.Auth` | **a provider** — a sign-in connector below, or a configuration-only OIDC/OAuth2 provider (no connector package needed) | [Auth recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/auth-howto.md) · [Authorization recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/authorization-howto.md) |
| Google sign-in | `Sylin.Koan.Web.Auth.Connector.Google` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Web/Auth/Google/README.md) |
| Microsoft sign-in | `Sylin.Koan.Web.Auth.Connector.Microsoft` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Web/Auth/Microsoft/README.md) |
| Discord sign-in | `Sylin.Koan.Web.Auth.Connector.Discord` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Web/Auth/Discord/README.md) |
| Deterministic sign-in for tests | `Sylin.Koan.Web.Auth.Connector.Test` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Web/Auth/Test/README.md) |
| Issue tokens from this application | `Sylin.Koan.Web.Auth.Server` | — | [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/identity/index.md) |
| Durable person identity | `Sylin.Koan.Identity` · `Sylin.Koan.Identity.Web` | — | [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/identity/index.md) |
| Inbound token trust | `Sylin.Koan.Security.Trust` | — | [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/identity/index.md) |
| Tenant isolation | `Sylin.Koan.Tenancy` | — | [Recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/tenancy-howto.md) · [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Tenancy/README.md) |
| Tenant operator console | `Sylin.Koan.Tenancy.Web` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Tenancy.Web/README.md) |
| Identity-bound tenancy | `Sylin.Koan.Identity.Tenancy` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Identity.Tenancy/README.md) |
| Field-at-rest classification | `Sylin.Koan.Classification` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Classification/README.md) |
| Field hygiene (trim/casing on write) | `Sylin.Koan.Data.Hygiene` **not assessed** | [capability node](../capabilities/data/field-hygiene.md) | - |

## Work and integration

| Outcome | Package | Also needs | Recipe |
|---|---|---|---|
| Durable and scheduled work | `Sylin.Koan.Jobs` | — | [Recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/jobs-howto.md) · [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/jobs/index.md) |
| Deterministic Job tests | `Sylin.Koan.Jobs.Testing` | — | [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/jobs/index.md) |
| Entity events and snapshot transport | `Sylin.Koan.Communication` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Communication/README.md) · [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/communication/index.md) |
| RabbitMQ carriage | `Sylin.Koan.Communication.Connector.RabbitMq` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Communication/RabbitMq/README.md) |

## State and content

| Outcome | Package | Also needs | Recipe |
|---|---|---|---|
| Cache and derived state | `Sylin.Koan.Cache` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Cache/README.md) |
| Durable local cache | `Sylin.Koan.Cache.Adapter.Sqlite` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Cache.Adapter.Sqlite/README.md) |
| Shared Redis cache | `Sylin.Koan.Cache.Adapter.Redis` — **not assessed** | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Cache.Adapter.Redis/README.md) |
| Entity-owned files | `Sylin.Koan.Storage` · `Sylin.Koan.Storage.Connector.Local` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Storage/Local/README.md) |
| Remote object storage | `Sylin.Koan.Storage.Connector.S3` — **not assessed** (shelved) | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Storage/S3/README.md) |
| Media recipes and derivatives | `Sylin.Koan.Media.Core` · `Sylin.Koan.Media.Web` | **a Storage connector** — Storage itself arrives with Media, but the bytes still need somewhere to live | [Recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/media-recipes-howto.md) · [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Media.Core/README.md) |

Prefer the local Storage path and the durable local cache. S3 is shelved and neither it nor the Redis
cache adapter is assessed; recommend either only when the developer asks, and say so plainly.

## Intelligence

For "I want to add AI", start from the [recipe index](../recipes/index.md) rather than this table.
"Add AI" is several different projects with different runtimes and operating costs, and the index says
which ones this application is actually close to. It also states what does not exist: Koan's AI
connectors are local-first, and there is no OpenAI, Anthropic, or Gemini connector.

| Outcome | Package | Also needs | Recipe |
|---|---|---|---|
| Model operations | `Sylin.Koan.AI` | **one AI connector** from the rows below | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.AI.Contracts/README.md) · [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/ai/index.md) |
| Ollama | `Sylin.Koan.AI.Connector.Ollama` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/AI/Ollama/README.md) |
| LM Studio | `Sylin.Koan.AI.Connector.LMStudio` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/AI/LMStudio/README.md) |
| ONNX (in-process) | `Sylin.Koan.AI.Connector.Onnx` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/AI/Onnx/README.md) |
| llama.cpp (llama-server) | `Sylin.Koan.AI.Connector.LlamaCpp` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/AI/LlamaCpp/README.md) |
| Hugging Face | `Sylin.Koan.AI.Connector.HuggingFace` — **not assessed** | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/AI/HuggingFace/README.md) |
| Inspectable prompts and HTTP projection | `Sylin.Koan.AI.Prompt` · `Sylin.Koan.AI.Web` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.AI.Prompt/README.md) |
| Answer from your own Entities (RAG), branch, parse to a type, stream | `Sylin.Koan.AI.Orchestration` — **not assessed** | **a chat provider**; retrieval steps also need the Entity's embedding and vector path | [RAG recipe](../guides/ai-rag-howto.md) · [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.AI.Orchestration/README.md) |
| Bounded agents whose tools are generated from `Entity<T>` | `Sylin.Koan.AI.Agents` — **not assessed** | **a chat provider**, plus the Data providers behind the Entity tools; `WithSearch<T>()` also needs the embedding and vector path | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.AI.Agents/README.md) |
| Acquire, convert, deploy, and version model artifacts | `Sylin.Koan.AI.Models` — **not assessed** | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.AI.Models/README.md) |
| Human approve/reject/edit queues over AI output | `Sylin.Koan.AI.Review` — **not assessed** | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.AI.Review/README.md) |
| Measure results, gate on metrics, detect drift | `Sylin.Koan.AI.Eval` — **not assessed** | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.AI.Eval/README.md) |
| `[Embedding]` on an Entity, and `Entity.Ai` search/embed operations | `Sylin.Koan.Data.AI` — **not assessed** | **an AI connector** to compute embeddings and **a vector connector** to store them | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Data.AI/README.md) |
| Entity vector semantics | `Sylin.Koan.Data.Vector` | **one vector connector** from the rows below | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Data.Vector.Abstractions/README.md) · [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/ai/vector.md) |
| In-memory vector index | `Sylin.Koan.Data.Vector.Connector.InMemory` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Vector/InMemory/README.md) |
| Durable local vector index | `Sylin.Koan.Data.Vector.Connector.SqliteVec` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Vector/SqliteVec/README.md) |
| pgvector | `Sylin.Koan.Data.Vector.Connector.PgVector` — **not assessed** | **Postgres with the `vector` extension** | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Vector/PgVector/README.md) |
| Redis vectors | `Sylin.Koan.Data.Vector.Connector.RedisVector` — **not assessed** | **a Redis deployment with Search/vector support**; plain Redis is insufficient | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Vector/RedisVector/README.md) |
| Mongo Atlas vectors | `Sylin.Koan.Data.Vector.Connector.MongoAtlasVector` — **not assessed** | **an Atlas deployment with Vector Search**; ordinary MongoDB is insufficient | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Vector/MongoAtlasVector/README.md) |
| Qdrant | `Sylin.Koan.Data.Vector.Connector.Qdrant` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Vector/Qdrant/README.md) |
| Weaviate | `Sylin.Koan.Data.Vector.Connector.Weaviate` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Vector/Weaviate/README.md) |
| Milvus | `Sylin.Koan.Data.Vector.Connector.Milvus` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Vector/Milvus/README.md) |
| Chroma | `Sylin.Koan.Data.Vector.Connector.Chroma` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/Vector/Chroma/README.md) |
| Elasticsearch vectors | `Sylin.Koan.Data.Connector.ElasticSearch` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/ElasticSearch/README.md) |
| OpenSearch vectors | `Sylin.Koan.Data.Connector.OpenSearch` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Connectors/Data/OpenSearch/README.md) |

`[Embedding]` and the `Entity.Ai` gateway live in `Sylin.Koan.Data.AI`, and nothing else brings it in. Reference it
explicitly whenever an Entity save should produce a vector — the AI connector and the vector store do
not supply it on their own.

[AI and vector recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/ai-vector-howto.md)

## Agent surfaces

| Outcome | Package | Also needs | Recipe |
|---|---|---|---|
| MCP tools and resources over Entities | `Sylin.Koan.Mcp` | — | [Agent-native recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/mcp-agent-native-howto.md) · [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Mcp/README.md) |
| Remote HTTP/SSE transport | `Sylin.Koan.Mcp` | — | [HTTP/SSE recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/mcp-http-sse-howto.md) |
| Human MCP console | `Sylin.Koan.Mcp.Explorer` | — | [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/agents/index.md) |
| Operational agent tools | `Sylin.Koan.Mcp.Operations` | — | [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/agents/index.md) |
| Agents ask declared analytics questions | `Sylin.Koan.Data.Analytics.Web` | `Sylin.Koan.Data.Analytics` + engine (`Sylin.Koan.Data.Connector.DuckDb`) | [Entity analytics how-to](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/data/entity-analytics.md) |

## Trusted records

| Outcome | Package | Also needs | Recipe |
|---|---|---|---|
| Reconcile imperfect arrivals | `Sylin.Koan.Canon` | — | [Recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/canon-capabilities-howto.md) · [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/canon/index.md) |
| Review and commit over HTTP | `Sylin.Koan.Canon.Web` | — | [Reference](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/canon/index.md) |

## Proof and operations

| Outcome | Package | Also needs | Recipe |
|---|---|---|---|
| Application tests through a real host | `Sylin.Koan.Testing` · `Sylin.Koan.Testing.Hosting` | — | [Recipe](https://github.com/sylin-org/koan-framework/blob/main/docs/guides/testing-your-app.md) |
| Real backing services in tests | `Sylin.Koan.Testing.Containers` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Testing.Containers/README.md) |
| OpenTelemetry export | `Sylin.Koan.Observability` | — | [README](https://github.com/sylin-org/koan-framework/blob/main/src/Koan.Observability/README.md) |

Facts, health, and composition evidence need no package — they arrive with the foundation. See the
main skill for their addresses, and
[operations](https://github.com/sylin-org/koan-framework/blob/main/docs/reference/operations/index.md).

## Choosing a data store

Keep Entity calls stable; choose the physical store from the business guarantee.

| Store | Best first fit | Boundary to settle |
|---|---|---|
| JSON | File-backed, zero-service state | Concurrency, process sharing, growth, and recovery |
| InMemory | Disposable tests and ephemeral work | No durability claim |
| SQLite | Durable local or single-node state | File ownership, concurrency, backup, and deployment shape |
| MongoDB | Document-oriented networked state | Query/consistency semantics, database naming, and existing data |
| PostgreSQL | General networked relational state | Schema ownership, transactions, connection policy, and operations |
| MySQL or MariaDB | Widely operated networked relational state | Schema ownership, transactions, connection policy, and operations |
| SQL Server | Microsoft relational environments | Schema ownership, transactions, connection policy, and operations |
| Redis | Keyed Entity state near a Redis topology | Query/stream limits, durability mode, and memory policy |
| Couchbase | Distributed document/key-value state | Bucket/query services, consistency, and operational topology |
| CockroachDB | Distributed SQL with PostgreSQL-shaped access | Supported SQL behavior, schema policy, latency, and topology |

Do not assume identical filters, paging, ordering, streaming, transactions, counts, or isolation
across stores. Inspect the selected one before promising an operation. Adding a store never moves
existing data — that is what the cutover package is for.

Keep search stores distinct from ordinary Entity persistence unless evidence proves both roles.

## AI and vector choices

Keep four choices separate even when one story uses all of them:

1. the named application operation;
2. the model provider route;
3. the embedding owner and provider;
4. the vector store and retrieval contract.

- Use the in-memory index only for bounded disposable work.
- Prefer SqliteVec when a durable local index matches the application's topology.
- Prefer PgVector when the application already operates Postgres and can enable its `vector` extension.
- Prefer RedisVector when the application already operates Redis with Search/vector support; plain Redis is
  not enough.
- Prefer MongoAtlasVector when the application already operates Atlas with Vector Search; ordinary MongoDB
  is not enough.
- Treat Qdrant, Weaviate, and Milvus as external dependencies with provider-specific filtering,
  paging, and operations.
- Treat Elasticsearch and OpenSearch as search-engine-backed paths; do not infer dedicated-vector
  behavior.
- Never imply that adding AI creates embeddings, acquires a model artifact, provisions an index, or
  migrates vectors.

## Boundaries that stay explicit

- Keep provider choice at composition and routing boundaries; keep ordinary Entity operations
  provider-neutral.
- Use explicit source, adapter, partition, and tenant scopes only where the outcome needs them.
- Apply the same authorization, tenant, and lifecycle rule across HTTP, MCP, Jobs, events, storage,
  media, AI, and vectors.
- Keep retry, timeout, cancellation, size, paging, and concurrency bounds visible.
- Do not treat a development identity, disposable store, hidden fallback, or optional provider as a
  production success path.
- Do not assume Koan provisions infrastructure, supplies backup or disaster recovery, or owns
  platform failover.
