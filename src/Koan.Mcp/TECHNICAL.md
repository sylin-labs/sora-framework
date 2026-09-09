---
uid: reference.modules.Koan.mcp
title: Koan.Mcp - Technical Reference
description: Model Context Protocol tools, resources, transports, and runtime inspection for Koan applications.
packages: [Sylin.Koan.Mcp]
source: src/Koan.Mcp/
---

## Relationship expansion

MCP entity reads use the shared Web `IEntityEndpointService`; there is no MCP-specific relationship
loader. Related-type visibility, backend negotiation, result/candidate limits, and corrective
rejections therefore match REST. The selected or rejected child-edge strategy is inspectable through
`koan://facts`. Runtime facts are a latest-state snapshot, not an operation history.

The same boundary carries normalized `Filter` access constraints, including `Where(predicate,
partition: "")` counterpart authority, through collection, body-query, keyed and relationship reads.
Counts and per-row access metadata describe the authorized result from that execution. MCP does not
compile a replica predicate or rebuild a second read decision. Unsupported providers and changed read
evidence produce the shared corrective result. These reads do not authorize mutations or guarantee that
moderation cannot change after the query. See [Web's constraint contract](../Koan.Web/TECHNICAL.md#normalized-read-constraints-and-counterpart-authority).

Source-bound `EmitDecision.Project(rows, mapper)` views use the same endpoint checks and per-row access
manifest. Collection and Query tools return the mapped view without a second MCP projection or policy
decision. Normalized `QueryOptions.Shape` and `IncludeRelationships` let application emit hooks choose
`Next` for framework shapes on either transport. A mapping failure returns no partial view.

## What this package composes

Discovery supplies the surface -- `[McpEntity]` entities and `[McpTool]` workflows -- and the caller's
identity and grants decide which of it exists for that session. What the caller receives back is MCP
tools and resources, JSON-RPC results, and transport health, with a structured failure rather than a
silent omission for an invalid envelope, an unavailable tool, a denied grant, or a transport fault.

## Runtime fact resource

`RuntimeFactsResourceProvider` owns `koan://facts`. It resolves `IKoanRuntimeFacts` from the same host
and serializes `Current` with `KoanFactJson`; it does not rebuild composition or scrape logs.

The resource contains no arbitrary provider payload, raw exception text, stack trace, or configuration
value. It can expose topology identifiers, so remote MCP transports must retain their authentication
and authorization posture.

Consumers must:

1. check envelope `schema` before binding new fields;
2. treat `complete: false` as unknown;
3. branch on fact `code`, `reasonCode`, `kind`, and `state`;
4. present `summary` and `correction` as guidance, not stable protocol tokens.

## Composition

- Resource providers register through `IMcpResourceProvider` and are projected by the shared MCP RPC
  handler.
- `koan://self` introduces the application and its caller-visible Entity plus custom-workflow surface.
- `koan://entities` describes caller-visible entities and verbs.
- `koan://facts` explains host composition decisions and degraded states.

Custom-tool visibility is calculated once by `CustomToolProjection` and reused by protocol listing,
remote dispatch, Explorer projection, and `koan://self`. A null principal preserves the established
trusted local-STDIO surface; a concrete remote principal applies server authentication, required
scopes, and operational-toolset enablement. Disabled or unauthorized tools leave no trace.

`McpCustomToolRegistry` owns custom input schemas. A tool declared with `IsMutation=true` advertises
the shared `McpDryRun.ArgumentName` control; the handler already accepts that control and returns an
honest non-executing partial rehearsal. Entity schema synthesis treats the CLR/wire property name as
the normal description fallback. Optional prose metadata improves agent guidance but its absence is
not an operational warning.

## Transports

STDIO is enabled by default. When `EnableStreamableHttpTransport` is true, Koan hosts modern Streamable
HTTP on the single `HttpRoute` (`/mcp` by default): client JSON-RPC, including `initialize`, uses
`POST`; an established session may use `GET` for resumable server push and `DELETE` for termination.
The initialize response mints `Mcp-Session-Id`, which the client echoes with the negotiated
`MCP-Protocol-Version` on subsequent requests.

`EnableLegacySseTransport` separately opts into the deprecated `/mcp/sse` plus `/mcp/rpc` shape.
Both HTTP shapes delegate to the same dispatcher, session, authorization, and projection core.
Transport choices are host-level options; every enabled edge projects the same governed capability
surface. Application-owned inputs, Entity results, custom-tool results, and Code Mode objects share
one camelCase JSON contract while protocol envelopes retain their specification-defined names.

## References

- Runtime facts: `/docs/engineering/runtime-facts.md`
- ARCH-0111: `/docs/decisions/ARCH-0111-unified-runtime-facts.md`
- MCP conformance suite: `/tests/Suites/Mcp/Koan.Mcp.Conformance.Tests/`

## Transport configuration migration

The retired `Koan:Mcp:EnableHttpSseTransport` key fails options validation at startup. Remove it and
configure `EnableStreamableHttpTransport`. To retain clients using `/mcp/sse` and `/mcp/rpc`, also set
`EnableLegacySseTransport` explicitly. No transport is enabled implicitly from the retired key.
## Rejected mutation deltas

MutationDeltaProjector returns no delta for a short-circuited endpoint result, even if a before-state
probe or operation label was prepared. This prevents rejected insert collisions from appearing as
committed creates. Dry-run remains prospective and uses only the existing visible before state.

## Conditional field access

MCP consumes Web's operation-bound property `[Access]` policy. It contributes the existing
`McpFieldPolicy` input/output exclusions as additional restrictions at preparation, so serialization
and caller admission cannot disagree about `[McpIgnore]`. The policy has the explicit caller,
request services, and declared input or actual typed output root. No principal-dependent decisions
live in `McpContractResolver`, schema caches, or the tool registry.
The public infrastructure methods `RequestTranslator.Translate` and `ResponseTranslator.Translate`
return `Task<RequestTranslation>` and `Task<McpToolExecutionResult>` respectively. Direct adapter
callers await them; the registered executor handles this for normal MCP dispatch.

Request translation admits caller filters, sorts, delete predicates, and normalized patch operations
before endpoint dispatch. The common Web path walker checks nested ancestors and descendants;
`test` reads its target, `copy` reads its source and writes its destination, and `move` additionally
writes its source. Full replacements, including bulk and dry-run requests, refuse if an omitted
input-protected field could be cleared. There is no implicit merge of stored private fields.
Trusted endpoint access filters are not reclassified as caller input.
The adapter binds its effective policy once to the endpoint context. Built-in map/dict shaping
checks its actual identity and display source members after option and emit hooks, so a hook-selected
shape cannot copy an MCP-excluded member into an unannotated display string.

Entity results, typed short-circuit payloads, and custom-tool Task/ValueTask results serialize with
operation-local Newtonsoft contracts. Custom dispatch owns a DI scope spanning binding, invocation,
and output. A supplied typed custom argument is a whole value: it requires replacement permission
before deserialization, including for omitted or constructor-bound protected members. A policy
failure does not fall through to the lenient ungoverned argument binder or invoke the tool with a
default value. Custom imperative effects remain application-owned; typed argument admission does
not inspect what a tool later saves.

The delta projector checks effective read access before fetching a field value, serializes nested
values through the same policy, and compares the visible JSON representations. A change confined to
an unreadable descendant therefore cannot create a parent-field delta. Protocol metadata retains its
existing shape; rejected calls have no mutation delta or returned data.

Schemas remain structural. Conditional fields are a caller-neutral superset, while unconditional
MCP input exclusions remain absent from input schemas. Code Mode Entity calls receive already-governed
tool results; its context-free JSON facade cannot acquire grants and refuses Access-decorated CLR
contracts. Unrelated JSON conversion remains supported.

This protects supported typed member contracts, not arbitrary application dataflow. Copying a secret
into an unannotated view, string, or prebuilt JToken discards its field provenance. Unknown governed
runtime types and converters that bypass typed policy fail correctively; serialization is completed
before a tool payload is returned. Custom-tool side effects already executed before output failure
are not rolled back.
