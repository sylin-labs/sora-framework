---
type: GUIDE
domain: web
title: "Patch an Entity"
audience: [developers, architects, ai-agents]
status: current
last_updated: 2026-09-10
framework_version: v1.0.0
validation:
  status: reviewed
  scope: source-aligned HTTP and Entity patch guidance; existing typed PATCH owner and public consumer evidence
---

# Patch an Entity

Use PATCH when a caller changes part of an existing Entity. `EntityController<T>` supplies the HTTP
surface through the application's normal `AddKoan()` composition. Reference Web and a Data connector;
there is no patch service to register or provider-specific patch API to configure.

Start with the [quickstart](../getting-started/quickstart.md) for host setup and the
[Web contract](../../src/Koan.Web/README.md) for governing the controller's complete surface.

## Choose the format

| Intent | Content-Type | Body |
|---|---|---|
| Change a few fields with ordinary JSON | `application/json` | A partial object |
| Merge an object and remove entries with null | `application/merge-patch+json` | An RFC 7386 object |
| Edit array elements or address specific fields | `application/json-patch+json` | An RFC 6902 operation array |

For a Todo with `Title`, enum `State`, and a string-keyed `Labels` dictionary, a partial update is:

```http
PATCH /api/todos/123
Content-Type: application/json

{ "title": "Buy oat milk", "state": "Pending" }
```

Omitted fields keep their stored values. Enum names are strings; use the model's declared spelling.
Dictionary keys retain their exact case and punctuation. Merge and partial documents replace arrays
as whole values, so use JSON Patch when the intent is to add or remove one element.

```http
PATCH /api/todos/123
Content-Type: application/merge-patch+json

{ "labels": { "Temporary/Flag": null } }
```

That merge removes the dictionary entry. For ordinary typed properties, merge-patch nulls follow
`MergePatchNullsForNonNullable`: the default `SetDefault` uses the explicit CLR default for a
non-nullable member and null for a nullable member. Partial JSON has a separate `PartialJsonNulls`
policy: `SetNull`, `Ignore`, or `Reject`. Configure these only when the business contract needs a
different default. The [protocol reference](../api/patch-normalization.md) owns exact null behavior,
request overrides, and status codes.

```http
PATCH /api/todos/123
Content-Type: application/json-patch+json

[
  { "op": "replace", "path": "/title", "value": "Buy oat milk" },
  { "op": "add", "path": "/tags/-", "value": "errand" }
]
```

JSON Patch uses the ASP.NET Core JsonPatch engine. Its `test`, `copy`, and `move` operations are
available on this HTTP path. Escape a slash in a JSON Pointer segment as `~1` and a tilde as `~0`.
A `test` compares the loaded working copy; it does not make the later write a database compare-and-swap.

## Keep permission at the public boundary

The endpoint applies its update constraints and shared member Access rules before accepting the
change. Partial and merge documents require whole-value write admission, including protected members
omitted from the body. JSON Patch supports selective admission through its operation paths.

Typed restoration preserves private stored state, but a private setter is not caller permission.
Do not expose internal `Data.Patch` instructions as a replacement HTTP route: they do not run Web
endpoint admission, hooks, stamps, or audit. Declare policy at its owning boundary and let REST and
the compatible MCP Entity projection share that policy.

## Preserve the document

HTTP merge and partial updates merge the Entity document, then restore a typed working copy through
the Data serialization contract. This preserves omitted properties, collection contents, dictionary
keys, string enums, and the stored family shape. The request does not edit the stored row in place.

The typed paths refuse identity or family-discriminator edits, duplicate member names differing only
by case, unsupported values, and attempts to create an absent abstract or interface value. A caller
cannot choose an arbitrary runtime type. Refusal happens before persistence; the detailed
[PATCH contract](../api/patch-normalization.md) distinguishes admission, parsing, and application errors.

A successful endpoint follows its normal stamps, save hooks, Data lifecycle, and response projection.
A dry-run applies and validates the working copy without persisting it. Neither dry-run nor PATCH
reserves the Entity's revision against concurrent writers. Use
[conditional replacement](../../src/Koan.Data.Core/README.md#guarded-replacement) with a captured
persisted revision when the business transition requires that guarantee.

## Patch from application code

The Entity statics provide partial and merge semantics without HTTP:

```csharp
await Todo.Patch(id, new { Title = "Buy oat milk" }, ct: ct);
await Todo.PatchMerge(id, new { Title = (string?)null }, ct: ct);
```

These statics normalize their input into a Data `PatchPayload` and use the canonical in-process
executor. This is a different path from the HTTP typed applicators. It supports `add`, `remove`, and
`replace`; `copy`, `move`, and `test` are not supported there. Normalization recursively emits property
paths; empty objects and property names requiring JSON Pointer escaping need particular care. Use
ordinary Entity reads and saves when the desired update is clearer as typed business code.

Data patching still uses the repository facade and Data lifecycle. It does not recreate caller
authorization or Web endpoint behavior. The [Data lifecycle contract](../reference/data/entity-lifecycle.md)
and [protocol reference](../api/patch-normalization.md) describe the two boundaries.

## Verify the business result

Exercise the actual endpoint and reload the stored Entity. Check the requested change, omitted
fields, enum names, collection contents, and protected fields. Include a denied update and confirm
that persistence stayed unchanged. Use the real connector when claiming storage compatibility.

The [Shared Approvals sample](../../samples/applications/SharedApprovals/README.md) demonstrates a
pending expense correction that retains omitted employee and receipt fields, followed by an approved
expense whose protected business fields cannot be changed through PATCH. Its verifier exercises
real HTTP responses and the subsequently stored result.
