---
type: REFERENCE
domain: web
title: "PATCH Formats and Normalization"
audience: [developers, architects, ai-agents]
status: current
last_updated: 2026-09-10
framework_version: v1.0.0
validation:
  status: reviewed
  scope: source-aligned with EntityController.Patch dispatch, EntityEndpointService.Patch, and Data.Core typed patch applicators; no runtime tests executed
---

# PATCH formats and normalization

`EntityController<TEntity, TKey>` accepts `PATCH /{id}` in three media types. One controller action
dispatches by Content-Type to two application paths. A successful request returns the updated model,
shaped by the same hooks and transformers as any other write.

| Content-Type | Parsed as | Application path |
|---|---|---|
| `application/json-patch+json` (RFC 6902) | `JsonPatchDocument<TEntity>` | the maintained ASP.NET Core JsonPatch engine applies the ops in place to a working copy |
| `application/merge-patch+json` (RFC 7386) | JToken object | `MergePatchApplicator` merges the Entity document and restores a typed working copy |
| `application/json` (partial body) | JToken object | `PartialJsonApplicator` merges and restores the same way |

## Two application paths

The formats do not share one executor.

JSON Patch is op-driven. Property admission walks each op's pointer: `test` reads `path`, `copy`
reads `from` and writes `path`, `move` writes both, and `add`/`replace`/`remove` write `path`. The
engine then applies the document to a working copy of the loaded row.

Merge-patch and partial JSON are whole-value documents for admission. Permission to write every
protected member is required before the body applies. The typed applicators never edit the stored row
in place: they merge the body into the Entity's serialized document and restore a typed working copy
through the same serialization persistence reads, so additive collections, sibling members, private
stored state, and family shape survive a patch. A private setter the restorer can write is not
permission for a caller to assign it.

A dry-run request applies and validates the working copy and persists nothing.

## Status contract

| Condition | Status |
|---|---|
| Body cannot be parsed for the declared Content-Type | `400` |
| Payload `id` disagrees with the route id | `400` `web.patch.idMismatch` |
| Known member denied by property access | `403` `web.fieldAccess.denied` |
| Unsupported caller field intent | `400` `web.fieldAccess.unsupported` |
| Unknown id, or a row outside applicable update constraints | `404` (existence-hiding) |
| Patched `id` differs from the route id | `409` |
| Typed merge/partial refusal | `422` with the applicator's message |

Typed refusals cover identity and family-discriminator edits, members repeated with names that differ
only by case, malformed payloads, policy-rejected nulls, and non-convertible values. They surface
after `BeforePatch` and before stamps, `BeforeSave`, and any save. The endpoint checks identity after
application, so a JSON Patch that changes the `id` value passes the engine and answers `409`; the
typed paths refuse an identity member outright with `422`.

## Null policy

Merge-patch and partial JSON read their null policy from `KoanWebOptions` and accept a per-request
override. JSON Patch carries nulls as explicit ops and uses no policy.

- `MergePatchNullsForNonNullable` (default `SetDefault`): a null member takes the member's explicit
  CLR default when its type is non-nullable and null otherwise; `Reject` refuses a null on a
  non-nullable member or dictionary value. A null dictionary entry is removed; member defaulting
  never rewrites data keys.
- `PartialJsonNulls` (default `SetNull`): `SetNull` assigns null, `Ignore` leaves the member
  untouched, `Reject` refuses the document.

Per-request overrides (querystring): `nulls=default|null|ignore|reject` sets both policies at once,
or use the granular `mergeNulls=default|reject` and `partialNulls=null|ignore|reject`.

## Merge semantics

Both typed documents merge recursively by member and replace arrays wholesale; an array in the body
never merges element-wise with the stored array. Dictionary keys keep their exact spelling, and enum
values bind by member name. A member with no matching property is ignored unless the contract carries
extension data, which receives the raw value. Creating a value where no stored value exists refuses
when the declared member type is abstract or an interface: the caller cannot choose the runtime
shape.

## Canonical ops are a Data instruction, not the HTTP path

The canonical `PatchPayload`/`PatchOp` model still exists for application code: `PatchNormalizer`
builds it from RFC 6902, RFC 7386, or partial JSON, and `Data.Patch(payload)` applies it through
`PatchOpsExecutor` with get, apply, and upsert semantics. The HTTP surface does not use it. The
governed endpoint applies patches in process and saves through the ordinary upsert, because the
Data-layer instruction bypasses Web endpoint gates, member admission, row constraints, stamps, hooks,
and audit, and belongs to internal or application-composed use. Its facade upsert still runs Data
lifecycle and managed admission. See
[DATA-0116](../decisions/DATA-0116-canonical-patch-operations.md) for the canonical model decision.
