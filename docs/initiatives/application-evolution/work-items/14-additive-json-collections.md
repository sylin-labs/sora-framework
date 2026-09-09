# AE-14: additive JSON collections

Explore accepted for implementation, 2026-09-09. The lead authorized extracting only existing
collection construction and reusing it in Data and typed transport binding. No Agyo dependency,
application converter, private-setter widening, discriminator transport or publication is authorized.
Focused verification and independent source review are complete. Public-package adoption and release
remain lead-owned and are not claimed here.

**Task:** Make additive collection arrays readable through the same typed HTTP contracts that emit them.

**Application intent:** An administrator edits a Work or Article with nested public and private tag
categories; the body binds without discarding values or bypassing member authorization.

**Public expression:** Ordinary `Entity<T>`, `EntityController<T>`, `AddKoan()`, and a concrete
`IEnumerable<T>` collection exposing a public parameterless constructor and `Add(T)`. Existing
`[Access]` declarations remain the field authority. No new application reference, registration,
configuration or converter is required beyond the existing Data/Web packages.

**Guarantee/correction:** The existing JSON array shape is reconstructed with its public collection
API when native Json.NET construction is unavailable. Constructor-seeded values require `Clear()`
and are replaced, not appended. Native collection/constructor contracts remain authoritative.
Unsupported construction fails without silently returning an empty or partially populated model.
Request binding does not gain access to nonpublic setters or persistence-family discriminators.

**Complete intent surface:** No additional user action. Custom serializers remain custom contracts;
this slice does not promise compatibility with arbitrary converters, immutable domain collections,
untyped copied JSON, or every standalone JSON API.

**Public concepts:** None in application code. One editor-hidden base resolver is a cross-pillar
implementation seam, not a new capability, registry or operation.

**Docs read:** Root README and architecture principles establish Entity-first code and one owner.
CLAUDE and Explore require this pre-edit receipt. Engineering README and TOC identify current owner
documentation. Core README/TECHNICAL establish existing generic JSON infrastructure and Newtonsoft
availability. Data.Core README/TECHNICAL establish persistence round-trip symmetry. Web
README/TECHNICAL and AE-10 establish typed request authorization and serializer compatibility.

**Code read:** `EntityJsonContractResolver.CreateArrayContract` already implements the exact generic
constructor/Add behavior; its other overrides are persistence-only. `FieldAccess` prepares authority
and builds per-operation settings; `FieldAccessContractResolver` enforces member decisions.
`FieldAccessMvcOptions` currently bypasses prepared settings for ungoverned input. `EntityController.Put`
materializes through plain `ToObject` and therefore needs the same prepared serializer. Core's
`JsonDefaults` already owns generic JSON settings. Agyo TagCategory supplies ctor/Add/Clear and
enumeration but not ICollection; its current JSON category arrays must not be reshaped.

**Reusing:** JsonArrayContract and native creators; existing constructor/Add/Clear implementation;
existing field metadata, gate evaluation, per-operation contract cache, MVC formatters/settings,
request-context builder and endpoint authorization. Searches found existing field-denial codes and
MVC options; this correction needs no new constants, options or result DTO.

**Creating new:**

| Owner | Change |
| --- | --- |
| `src/Koan.Core/Json/KoanJsonContractResolver.cs` | Own the existing additive array construction only |
| `src/Koan.Data.Core/Polymorphism/EntityJsonContractResolver.cs` | Inherit that owner and delete copied construction |
| `src/Koan.Web/Authorization/FieldAccessContractResolver.cs` | Inherit common array construction while retaining field decisions |
| `src/Koan.Web/Authorization/FieldAccess.cs` | Recognize supported standard resolver templates through one check |
| `src/Koan.Web/Serialization/FieldAccessMvcOptions.cs` | Apply common construction to ordinary standard typed input too |
| `src/Koan.Web/Controllers/EntityController.cs` | Use prepared field settings for PUT materialization |
| `tests/Suites/Web/Koan.Web.WellKnown.Tests/AdditiveCollectionInputSpec.cs` | Real HTTP governed/ordinary/PUT, seeded dictionaries and encapsulation proof |
| Core/Data/Web package README/TECHNICAL | State the supported construction and authority boundaries |

**Coalescence:** Absorb Data's generic array creator into existing Core.Json; keep persistence setters
and discriminators in Data and operation authority in Web. Reusing the entire persistence resolver
would cross that boundary. A vendor-specific converter or duplicate array builder is unnecessary.
Ordinary and prepared transport settings use the same owner; no global principal or new cache appears.

**Ergonomics:** Application collections and controller declarations stay unchanged. The collection
shape is inferred from standard reflection contracts already used by Data, with corrective refusal
for unsupported construction. This eliminates a transport-specific surprise without adding setup.

**Constraints satisfied:** Controller routes only; Entity-first test persistence; existing options
and error codes; no backend change, unbounded data query, worker, service or provider registry.
Package docs carry the change; existing TOC entries already point to the affected owner references.

**Risks:** Preserve custom resolver/formatter compatibility, default naming and constructors. Do not
copy Data's private-setter behavior to input. PUT field preflight must use the effective request
principal and retain the normal endpoint authorization afterwards. A converter or constructor can
have application side effects; no transactional deserialization or arbitrary object provenance is claimed.

The lead independently read the source and tests and parsed the 68 Web plus six Data passes.
All eight combined repository coherence legs pass in `TEMP/koan-ae14/coherence.log`; its build
retains 21 existing warnings outside the selected owners. This is not complete framework certification.

## Proof plan

Retain expected failures before production edits. Exercise real AddKoan HTTP POST for ordinary and
governed nested dictionary arrays, denied caller/no write, seed replacement, private-setter refusal,
native constructor compatibility and corrective refusal when seeds cannot clear. Exercise actual
PUT as the other complete replacement route, including route-ID authority. Run existing Data
round-trip and Web field-policy owners after the extraction. Typed MCP uses the prepared field
resolver; any direct serializer proof must not be described as an HTTP MCP test.

## Execution and independent review

Receipts are retained privately under `TEMP/koan-ae14`:

| Receipt | Result | Meaning |
| --- | --- | --- |
| `collections-before.trx` | 6 expected failures, 2 controls passed | Ordinary/admin POST and prepared binding cannot construct arrays; PUT fails during plain materialization |
| `collections-after.trx` | 8 passed | Initial extraction repairs binding and retains field/identity controls |
| `custom-reader-before.trx` | 1 expected failure | Independent review reproduced loss of an overridden Newtonsoft input reader |
| `web-collections-final.trx` | 68 passed | Nine collection/compatibility cases plus 59 existing field-policy cases |
| `data-roundtrip-after.trx` | 6 passed | Existing Data enum, additive collection, constructor-seed and encapsulation round trips |

All receipts compiled without warnings; final receipts have no failures or skips. The custom-reader
correction retains the original formatter for unrestricted input when it is a Newtonsoft subclass,
or uses a custom resolver. Governed preparation retains its existing boundary. The regression checks
both the custom response marker and the custom bound payload, not just the formatter's CLR type.

The independent reviewer accepted the exact construction extraction, native constructor precedence,
separate persistence encapsulation policy, and final custom-reader correction. The related MCP
owner also reports its 58 enum/field/delta/wire checks passing with the shared resolver. This owner's
new prepared-serializer case is direct typed binding, not a registered MCP or HTTP MCP journey.

No Agyo package, service, registry, protocol payload shape, persistence discriminator or private-setter
policy changed. Unsupported additive shapes retain corrective deserialization failure; arbitrary
custom JSON, standalone JsonConvert/Code Mode conversion, and generic JSON Patch collection editing
are not newly guaranteed. GW's actual Work/Article public-package acceptance remains separate.
