# AE-13: MCP enum names on the wire

Explore accepted for implementation, 2026-09-09, baseline `f5e978f7ffc4`.
The application contract requires enum names, including typed summaries and relationship output.
GW's registered EndpointToolExecutor currently produces integer `1` for `MirrorState.Ready`.
This is already a JToken before the test observes it, not an HTTP-input helper artifact.

## Existing owner and bounded correction

`ResponseTranslator` and typed custom tools delegate application serialization to `McpJson`.
Its camelCase/null-omission template lacks an enum converter. `FieldAccess` copies that template;
it does not remove a converter. `MutationDeltaProjector` separately declares StringEnumConverter,
so payload and delta disagree. The schema already describes enum member strings.

Use the existing McpJson JSON owner for an output-only StringEnumConverter with
`AllowIntegerValues = false`. Its `CanRead = false` preserves the existing Newtonsoft input
binding, including accepted legacy numeric enum input. Web currently installs default
StringEnumConverter, permitting numeric input; Data's stored-entity convention disables numeric
values. This correction changes MCP output without changing Data or Web serialization policy.
Undefined enum output fails before a partial tool result is returned; it is not coerced to a name.

Delete delta's duplicate settings and use McpJson's prepared application serializer. Reuse only
the stateless enum converter in the existing context-free Code Mode serializer, preserving its
field exclusion resolver, conditional-field refusal and date/float parsing settings. No new public
configuration, serializer registry or caller policy vocabulary is introduced.

## Owned scope and proof

Production: `src/Koan.Mcp/Execution/McpJson.cs`, `MutationDeltaProjector.cs`, and
`src/Koan.Mcp/CodeMode/Json/NewtonsoftJsonFacade.cs`. Documentation: this card and package TECHNICAL.
Tests: one new `EnumWireContractSpec.cs` in the existing MCP Conformance owner.
Registered get/query, terminal summary, relationship and custom Task/ValueTask outputs must emit
named strings while field restrictions remain effective. Mutation payload and dry-run/real delta
must agree. Code Mode must retain its conditional-field refusal and legacy numeric input behavior.
Undefined typed output must fail.

Baseline `TEMP/koan-ae13/enum-before-20260909-175412395.trx` executed 14 cases. Twelve
reproduced the output defect: 11 expected strings were integers, and undefined custom output
was accepted. Two relationship cases used Query rather than the Collection operation that owns
`with=all`, and failed their fixture shape. They were corrected to Collection; this receipt is
not relationship proof. An earlier compile attempt lacked the test's Core extension namespace;
it ran no tests.

Final `TEMP/koan-ae13/enum-after-20260909-175736217.trx` passes all 58 selected cases with
zero failures, skips or compiler warnings: enum contract 14, existing FieldAccess 35, dry-run/state
delta 4 and wire-shape 5. The corrected relationship pair passes for anonymous and admin callers.
The enum cases exercise registered dispatch, real persistence/dry-run controls, terminal Project
and the existing Code Mode facade. Numeric custom input binds as before and returns a named enum;
Code Mode numeric input also remains accepted. Undefined custom output returns an error, and
undefined Code Mode output throws. Existing conditional CLR refusal remains effective.

The lead independently read the source/tests and parsed all 58 successful results. A separate
source reviewer accepted the three production edits and preserved limits. All eight combined
repository coherence legs pass in `TEMP/koan-ae14/coherence.log`.

The after run includes AE-14's concurrently developed shared additive collection resolver, with
serialized builds and source ownership. No HTTP transport suite or public package consumption is
included. Production and test source are frozen for lead review; the Koan build lock is released.

Prebuilt JToken/string data has no CLR enum identity and is not reinterpreted. Explicit application
converters remain application-owned; this is the framework's standard typed JSON convention.
No public package adoption, HTTP transport certification or application translation fix is claimed.
Serialization failure after a mutation does not establish rollback of that mutation.
