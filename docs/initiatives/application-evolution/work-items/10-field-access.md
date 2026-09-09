# AE-10: Conditional field access

Explore, 2026-09-09. Baseline `e07a84cc3` on dev. The lead authorized a bounded
Web authorization/MVC/shared Entity implementation, with separate MCP integration and independent
review. No commit, remote operation, version change or application edit belongs to this work.
Implementation evidence will be recorded separately from this design.

**Task:** Keep internal Entity fields available to authorized operators while protecting typed
REST/MCP output and caller-controlled field operations.

**Business sentence:** Anyone may see the published Work; only an administrator may see or submit
its claimant identifiers. Existing MCP exclusions remain stronger even for administrators.

```csharp
[Access(read: "is:admin", write: "is:admin")]
[McpIgnore]
public List<string> ClaimedByUserIds { get; set; } = [];
```

The existing Web reference and `AddKoan()` suffice. Property `read`, `write`, and `all` reuse the
existing gate vocabulary. Unspecified directions remain open. Property `remove` and `owner` are
unsupported and fail composition; there is no row owner evaluator for a field. Field admission
adds a restriction and never opens a denied Entity operation. No storage serializer or trusted
Data query is affected.

## Source findings and owner decision

- Root README and architecture principles establish Entity-first intent and compiled structural
  metadata. Engineering README and TOC establish this initiative card as the engineering receipt;
  package README/TECHNICAL remain the reusable capability documentation.
- `Authorization/Access.cs`, `AccessGateParser.cs`, `AccessGateCache.cs` and
  `AccessGateRegistrar.cs` already own declaration, parsing, immutable metadata and boot validation.
  Extend their property coverage; do not create another policy language or registry.
- `EntityFloorAuthorizationProvider.cs` evaluates the token first, then resource-specific
  AgentGrants when denied. Extract its existing gate/grant fallback for both entity and property
  decisions. An open public entity read must not prevent a restrictive field gate from consulting
  grants. `AgentGrantStore` already memoizes request reads; do not introduce another grants cache.
- `Extensions/ServiceCollectionExtensions.cs` installs MVC Newtonsoft input/output. The default
  camel-case contract has no conditional field policy. Operation-local contracts can use the
  Newtonsoft settings copy constructor and preserve ordinary serializer settings.
- `McpFieldPolicy` and `McpContractResolver` own unconditional MCP exclusion, not conditional
  cross-protocol authorization. Retain exclusion and wire-name behavior while consuming Web's
  common property admission. Web must not depend upward on MCP.
- `EntityEndpointService.FreezeReadQuery` still has the caller JSON filter separate from the
  server Filter. Validate the former before combination. Validate incoming sort before BuildOptions
  adds trusted server choices. DeleteQuery has a separate caller parser and must be covered.
- JSON Patch includes disclosure through `test`, `copy`, and `move`; destination-only write checks
  are insufficient. Full replacement can erase an omitted protected property, so refusing a
  replacement is the initial supported behavior when any affected property is not writable.
- Existing `EntityRequestContext`, `AuthorizeRequest`, `ActionGate`, `MemberPath`, MVC options and
  patch payloads are reused. Searches found no conditional Web member owner to preserve, and no
  need for application activation options, a new authorization provider ladder or registry.

Closest pattern: MCP contract exclusion. Keep the protocol-specific exclusion; absorb conditional
member mechanics into existing Web authorization. A Data owner would conflate trusted persistence
with caller input. An application scrubber would repeat the same protection across controllers,
formatters, MCP and query paths.

## Exact implementation placement

| Owner | Files and change |
|---|---|
| Declaration and metadata | `src/Koan.Web/Authorization/Access.cs`, `AccessGateCache.cs`, `AccessGateRegistrar.cs`: property validation and member metadata |
| Effective authority | `src/Koan.Web/Authorization/EntityFloorAuthorizationProvider.cs`: reuse one asynchronous gate/grant fallback |
| Operation member admission | `src/Koan.Web/Authorization/FieldAccess.cs`: metadata closure, explicit-principal decisions, path and replacement admission, operation serializer settings |
| Newtonsoft contracts | `src/Koan.Web/Authorization/FieldAccessContractResolver.cs`: consume prepared decisions without shared contract mutation |
| MVC integration | `src/Koan.Web/Extensions/ServiceCollectionExtensions.cs`, bounded formatter integration under `src/Koan.Web/Serialization/`, initialization only where needed |
| Shared caller boundaries | `src/Koan.Web/Endpoints/EntityEndpointService.cs`: query/sort/delete/patch/replacement preflight and known built-in shaping inputs |
| MCP consumer | Existing MCP serializer, request translator, delta and schema owners, delegated separately after the common contract is fixed |
| Proof | Dedicated tests in `tests/Suites/Web/Koan.Web.WellKnown.Tests/`; focused Access/MCP suites as affected |
| Documentation | Web/MCP package README/TECHNICAL, this card, existing initiative PROGRESS/NOW |

Only the attribute is new application authoring. The editor-hidden cross-package contract is an
operation `FieldAccess`, prepared asynchronously from root CLR type, explicit services/principal and
cancellation. It exposes member read/write decisions, path/replacement checks and creation of
operation-owned serializer settings. MCP can supply additional pure exclusion predicates; it cannot
override denied field access.

## Metadata and serialization discipline

Preparation follows Newtonsoft's typed contracts, wire names, ignored members, object properties,
array item types and dictionary value types. It does not walk runtime object values. Start with the
actual top-level runtime type for output and declared model type for input. Stop at an unresolved
dynamic type; if serialization later encounters a governed member that was not prepared, refuse
correctively before buffered bytes are committed. No blocking asynchronous grant calls from a
serializer callback, no response-wide application-type scan, and no generic object scanner.

Only neutral structural metadata may live in shared caches. Principal-dependent decisions and
Newtonsoft property callbacks belong to one operation. MVC `ObjectResult` and typed `JsonResult`
must apply the same contract, including explicit per-result settings. A converter or custom resolver
that bypasses a configured typed field contract is unsupported unless that path can preserve the
guarantee. Existing formatter buffering must prevent a failed serialization from emitting a partial
governed response. Arbitrary unrelated custom JSON remains valid.

REST Work roots, lists, typed dictionaries and custom typed detail are required. Built-in map/dict
shaping must check its chosen ID/display source fields before discarding CLR member identity.
Projection DTOs retain their own declarations; arbitrary copied values are not tracked.

## Caller admission and failure behavior

Caller field resolution uses the same Newtonsoft member names plus canonical CLR identity.
Read-denied fields cannot participate in caller filter/sort/delete predicates. Validate nested
ancestors as well as the final member. Trusted Access predicates, lifecycle code and direct Data
queries remain ordinary server operations.

Patch `path` requires write admission; `test` requires read; `copy` requires read on `from` and write
on destination; `move` additionally requires write on `from`. Parent object or collection operations
must include declared protected descendants or refuse the unsupported shape. Reject denied input
before any persistence, including every item of a bulk request. Replacement requests lacking field
write permission fail rather than silently dropping input or clearing omitted persisted fields.

Static schemas may describe conditional fields as a superset. They must not cache caller-specific
membership. Existing MCP unconditional input/output exclusions remain omitted from applicable
schemas and output even when a member Access gate permits an administrator.

## Proof plan and limits

Use real AddKoan/MVC formatting to prove anonymous versus administrator typed detail/list/custom
ObjectResult/JsonResult, including per-result settings and concurrent requests. Verify resource-scoped
AgentGrants independently of the open entity read. Prove unknown governed polymorphic output and
unsupported converters fail without partial bytes; unrelated raw JSON continues to work.

Exercise caller filter/sort inference, nested aliases, DeleteQuery, all relevant patch directions,
whole replacement omission, bulk preflight and unchanged stored protected values. Trusted server
filters and persistence must retain field access. MCP exercises the same owner plus stronger
exclusion for admin/nonadmin, inputs, schema and deltas. No full certification per local edit.

This is not information-flow tracking: custom code may copy a secret to an unannotated DTO, JToken,
string, custom query or external channel. Typed member contracts and supported caller parser
boundaries are protected; arbitrary application dataflow remains application-owned. No universal
custom-code privacy claim, ambient Data restriction or projection registry is introduced.

## Execution evidence and final handoff

Lead repository coherence, `green-ratchet.ps1 -Configuration Release -Base e07a84cc3 -SkipTests`,
passed all eight executed legs, including the solution/sample build and composition locks. The build
retains 24 existing warnings outside the changed owners. The 132 focused checks below remain the
behavior evidence; this gate does not claim full-suite or NativeAOT runtime certification. Publication
and public-package consumption follow the normal generated dependency-stamp and main boundary.

Implementation and independent source review are complete locally. No commit, publication, package
version change, production connection or application edit was performed by this owner. The build lock
is released to the lead for repository coherence and normal release processing.

| Focused receipt | Passed | Scope |
|---|---:|---|
| `TEMP/koan-ae10/web-field-review4.trx` | 73 | 59 field/MVC journeys, 11 scoped mutation and 3 counterpart refusal regressions |
| `TEMP/koan-ae10/web-counterpart-conflict.trx` | 2 | Native Mongo post-selection 409, collection and Query |
| `TEMP/koan-ae10-mcp/mcp-field-final-20260909-193826712.trx` | 49 | 35 new field/shape/relationship journeys plus 14 affected MCP checks |
| `TEMP/koan-ae10-mcp/mcp-field-exclusion.trx` | 5 | Existing stronger exclusion compatibility |
| `TEMP/koan-ae10-mcp/mcp-custom-tools.trx` | 3 | Existing custom-tool compatibility |

All five receipts were XML-checked: **132 passed, zero failed or not executed**. Their owner logs
report zero compiler warnings. These are focused receipts, not complete certification or published
consumer proof. Native Mongo uses the existing isolated fixture. The final Web receipt includes the
last ignored-member and ISerializable corrections; earlier transport compatibility receipts remain
scoped to their tested surfaces.

Actual MVC journeys cover anonymous/admin typed roots, custom ObjectResult and JsonResult with
explicit settings, wire casing, concurrency, resource grants, trusted origin, access sidecars, known
related root/parent/child privacy, partial/merge refusal and allowed JSON Patch/admin edits, raw Entity
emit refusal, unknown polymorphic/legacy serializer refusal without partial output, and ordinary
closed-type formatter compatibility. Inherited gates use the concrete containing resource. An explicitly
Access-declared ignored property still blocks replacement; the fixture explicitly acknowledges that
InMemory's existing serializer does not persist its JsonIgnore value and proves public Name remains
unchanged on denial. No ignored-field storage guarantee is invented.

The summary-controller follow-up proves the normal MVC collection emit hook receives `summary` by
default, returns its personalized marker, and preserves explicit `view=full` and keyed full behavior.
The native 409 follow-up preserves the custom denial status while discarding rows, total/page/access
headers and manifest. These are separate existing Web corrections, not property-access guarantees.

Independent review accepted the final typed contract, containing-type decisions, operation binding,
whole-value partial/merge refusal, typed framework wrappers, origin normalization, ignored-member
admission and legacy retained-wrapper refusal. No concrete source blocker remained at handoff.
The exact 22 Web-owned changed/additional paths are also recorded in
`TEMP/koan-ae10/web-owned-paths.txt`; MCP owns its separate 17-path inventory.

### Final compatibility and ownership refinements

- Only normal MVC action invocation is covered by the result filter. Direct ExecuteResultAsync outside
  that pipeline does not invoke it. Complete typed bodies, including custom action arguments and
  constructor/omitted members, require whole-value write admission before binding. Native model-state
  refusal is 400; known denied shared caller fields are 403; unknown/unsupported caller intent is 400.
- Structural property decisions are keyed by actual containing CLR type plus member. A PublicA field
  inherited from SharedBase uses PublicA's grant resource. Existing scoped AgentGrantStore owns
  memoization. MemberInfo metadata retains MCP public-field exclusions without expanding Access syntax.
- Protocol field preparation follows EntityRequestContextBuilder's effective origin/principal. One-time
  context binding retains stricter exclusions and validates root plus captured identities/claims. Final
  map/dict admission occurs after BuildOptions. No Items key, authority registry or second evaluator.
- AccessProjection uses an internal generic envelope retaining the payload CLR type. Relationship
  collections retain RelationshipGraph<T>; only that known graph family adds declared edge types from
  IRelationshipMetadata. No runtime graph traversal or parallel type registry prepares policy.
- Object/interface, nonsealed object and ISerializable contracts require guarded serialization. This
  conservatively includes a nonsealed root obtained from value.GetType(). Custom formatter/resolver or
  converter combinations can therefore refuse on an otherwise plain open DTO. The explicit closed/open
  compatibility test records this choice; no authoring switch bypasses it. Sealed fully known ordinary
  contracts retain the native formatter path.
- Native ProblemDetails converters remain supported because they serialize their wrapper and extensions
  through the supplied serializer. Retained unprepared governed extension values still refuse. This was
  checked against the [ASP.NET Core 10.0.10 converter source](https://github.com/dotnet/aspnetcore/blob/v10.0.10/src/Mvc/Mvc.NewtonsoftJson/src/ProblemDetailsConverter.cs).
- JSON Patch is the selective-edit path: `PATCH /works/id`, `application/json-patch+json`,
  `[{"op":"replace","path":"/name","value":"Updated"}]`. Partial JSON and Merge Patch require
  permission to write every protected field, even if only a public field is named. This avoids the
  existing normalizer's empty-object omission and unescaped slash-alias behavior. Those remain Data
  owner findings; no patch-normalization repair is claimed here.
- EntityController.DefaultCollectionView is a collection-only semantic default. Summary controllers
  supply summary before shared hooks; explicit full and keyed/model behavior remain intact.

### Preserved failed attempts

Intermediate Web receipts record 22 and 31 passes. An intermediate 52-pass receipt was accidentally
overwritten; it is not counted. The surviving `web-field-final.trx` has 54 total, 41 passed and 13 failed
when stricter dynamic qualification rejected native ProblemDetails converters. Review2 then passed 68.
Review3 has 71 total, 69 passed and two failed fixture expectations about JsonIgnore persistence; only
those expectations were corrected. Unique review4 is the final 73-pass Web receipt above.
