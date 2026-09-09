# AE-16: typed merge and partial patch restoration

Explore accepted for bounded implementation, 2026-09-09, after release-boundary commit
`7ceb16c9fec333cd345cf561be417386e22dfc3c`. The lead owns publication; this work does not change
versions, remote state or application sources. Implemented 2026-09-09 with Astra High corrections
applied the same day (including the confirmed dictionary-key blocker follow-up); receipts under
`artifacts/agent-work/ae16/`.

## Intent and existing evidence

An authorized caller changes one Entity field without losing existing additive collections,
private stored state, nested siblings, identity or runtime family shape. Existing HTTP routes,
field authority, provider identity and stored representation remain authoritative.

The real Gposingway HTTP baseline had three failures, all 500 before persistence, caused by bare
`JToken.ToObject` in the merge/partial applicators (receipt
`TEMP/koan-ae11/public-generalized-patch-before-20260909-181454203.trx`, copied to this repo as
`artifacts/agent-work/ae16/patch-before.trx` for the typed spec baseline: 18 cases, 11 pass /
7 fail, four additive-collection restorations and three merge-null failures).

## Owner and accepted API decision

The shared Web endpoint invokes the two applicators in Data.Core/Patch/PatchApplicators.cs. They
previously serialized with bare defaults, edited case-sensitive JObject members, deserialized, then
serialized and merged again before populating the target, bypassing `EntityJsonSerialization` and
AE-14 additive collection construction, and exploding on null-to-non-nullable conversion.

The applicators now serialize the target with `EntityJsonSerialization.SerializeDocumentToken`,
admit and merge supplied members into that document through one shared engine
(`EntityPatchDocument`), and restore via a new internal
`EntityJsonSerialization.MaterializeDocument` (same document serializer, stored-only family
classification as persistence). The lead accepted removing the public `void Apply(TEntity)` methods
in favor of `ApplyToCopy(TEntity): TEntity`; both endpoint call sites assign the returned working
copy. The input target is never mutated, so refused, malformed, or non-convertible patches leave it
exactly as it was and throw before any save. Application Entity and HTTP grammar is unchanged.

Admission (Astra High corrected):

- Native Newtonsoft contract metadata only: writable, non-ignored members with the document's
  camel wire names; `JsonObjectContract`, `JsonDictionaryContract.DictionaryValueType`,
  `JsonArrayContract.CollectionItemType`. No hand-built reflection taxonomy, no field metamodel,
  no registry, no serializer configuration.
- Admission applies recursively to EVERY supplied typed subtree: nested objects, dictionary
  entries, and array elements, including newly created subtrees and wholesale array replacement,
  so persistence restoration (which may restore private setters) can never admit what public code
  could not assign. Ignored private-setter input stays silently ignored.
- Dictionary keys are exact data: never case-aliased, never member-defaulted, and no longer
  recased by the document serializer itself. Merge null on a dictionary entry removes the entry
  (RFC 7386); `Reject` refuses nulls targeting non-nullable values. Typed members under merge-null
  take an explicit CLR default (non-nullable, e.g. 0) or explicit null (reference/nullable), so
  constructor seeds do not resurface; dictionary-entry removal stays separate. Partial policies
  keep SetNull/Ignore/Reject; partial SetNull on a non-nullable member is an honest conversion
  refusal.
- Identity and family discriminator refuse at every family node, by name (`Id`, `__koan_type`,
  case-insensitive) AND by resolved member (catches `[JsonProperty("key")]`-aliased identity).
  A new subtree over an abstract/interface member type refuses: concrete shape comes from stored
  state (the serialized child's own `__koan_type`), never caller input. Two patch members that
  resolve to the same member under case-insensitive matching refuse as ambiguous. `JToken`/`object`
  members are raw caller data and pass through.

Web: `EntityEndpointService.Patch` wraps both applicator calls; `JsonException`,
`InvalidDataException`, `InvalidOperationException`, `ArgumentException` become a corrective 422
with the applicator's message: after `BeforePatch`, before stamps, `BeforeSave`, and any save.
Field admission (whole-value `DemandReplacement`), row constraints, hooks, stamps, audit, dry run
(rehearses the restored copy, persists nothing) and the typed Microsoft JSON Patch path are
unchanged. Ordinary save was not upgraded to CAS or a cross-document transaction.

## Scope taken (files)

- `src/Koan.Data.Core/Patch/PatchApplicators.cs`: applicators + shared admission/merge engine.
- `src/Koan.Data.Core/Polymorphism/EntityJsonSerialization.cs`: internal `MaterializeDocument`;
  `DocumentSettings` now uses `DefaultContractResolver` with
  `CamelCaseNamingStrategy { ProcessDictionaryKeys = false, OverrideSpecifiedNames = true }`, so
  property wire names are unchanged while dictionary keys keep their exact stored casing. Caller
  supplied `Apply` settings (for example the Json provider's own resolver) are untouched.
- `src/Koan.Web/Endpoints/EntityEndpointService.cs`: assign returned copy, corrective 422, comment
  corrected (refusal is after `BeforePatch`, before `BeforeSave`/save).
- `src/Koan.Data.Core/TECHNICAL.md`, `src/Koan.Web/TECHNICAL.md`: behavior sections.
- `tests/Suites/Web/Koan.Web.WellKnown.Tests/TypedPatchDocumentSpec.cs`: regression spec (40 cases).
- `tests/Suites/Web/Koan.Web.WellKnown.Tests/Koan.Web.WellKnown.Tests.csproj`: analyzer reference
  to `Koan.Core.Registry.Generators`, required for the generated Entity-family fixture (same
  pattern as the Data.Core tests project). Necessary test-infrastructure scope for the demanded
  family coverage.
- `tests/Suites/Data/Core/Koan.Tests.Data.Core/Specs/Entity/EntityRoundTripSymmetry.Spec.cs`: one
  shared document/token dictionary-key round-trip regression (review-mandated bounded extension).
- `tests/Koan.Web.PatchOps.Tests/ExecutionTests.cs`: call sites updated to the accepted API break
  (applicator tests only; `PatchOpsExecutor` untouched).
- This card. No versions, NOW, PROGRESS, Gposingway, or other dirty work (AE-15, AE-17/18) touched.

## Proof (all commands `C:/Tools/DotNet/dotnet.exe ... -c Release`, receipts in artifacts/agent-work/ae16)

- Baseline: `patch-before.trx`, TypedPatchDocumentSpec, 18 cases, 11 pass / 7 fail / 0 skip.
- First Astra correction baseline (tests first, defective source): `patch-defects-before.trx`,
  39 cases, 26 pass / 13 fail (subtree forgery both kinds, dictionary/array admission both kinds,
  stored-family shape both kinds, reject-null-in-new-subtree, merge defaults vs seeds both kinds,
  identity wire alias, duplicate casing). After fixes: `patch-defects-after.trx`, 39/0/0.
- Dictionary blocker baseline (tests first, still-defective DocumentSettings):
  `dictionary-collapse-before.trx` (2/2 patch modes fail) and `dictionary-roundtrip-before.trx`
  (Data round trip fails). After the DocumentSettings and admission fix:
  `dictionary-collapse-after.trx` 40/0/0; `dictionary-datacore-after.trx` 32/32.
- Owners: `dictionary-wellknown-after.trx` full Koan.Web.WellKnown.Tests 139/139;
  `dictionary-patchops-after.trx` 14/14; `dictionary-adapter-inmemory-after.trx` 95/95;
  `dictionary-adapter-json-after.trx` 59/59; `dictionary-canonstage-after.trx` 5/5 (Canon unit,
  direct `EntityJsonSerialization` consumer). Final disposal cleanup re-proved the affected Web
  owner only, per review: `disposal-wellknown-after.trx` 139/139. Earlier equivalents from the
  first correction round: `patch-wellknown-after.trx` 138/138, `patch-datacore-family-after.trx`
  31/31. All runs 0 warnings, 0 skips. Full certification not run (per assignment).
  Mongo/Sqlite/Redis/Postgres adapter suites not run: the merge-patch HTTP path is
  provider-neutral and was proven on InMemory and Json; no new provider dependency was introduced.

## Astra High correction record (2026-09-09)

1. New-subtree/array admission bypass (private-state forgery through restoration): reproduced by
   `New_subtree_admission_blocks_private_state_forgery`,
   `New_dictionary_entry_and_array_elements_are_admitted`,
   `Stored_family_child_admits_through_its_own_variant_shape`,
   `Rejected_null_inside_new_subtree_refuses_without_touching_the_original`; fixed by
   contract-driven recursive admission (`AdmitValue`/`AdmitObject`/`AdmitDictionary`/`AdmitArray`).
2. Merge SetDefault resurrecting constructor seeds: reproduced by
   `Merge_null_defaults_beat_constructor_seeds` (Note "seed" to null, Seeded 41 to 0, nested
   sibling); fixed by explicit CLR-default/null tokens; dictionary-entry removal kept separate
   (`Merge_null_dictionary_entry_is_removed_not_defaulted`).
3. Identity refusal by spelling only: reproduced by `Identity_refuses_through_a_mapped_wire_alias`
   (`[JsonProperty("key")]`); fixed by refusing the resolved identity member as well as reserved
   names.
4. Fixture defect: `TypedPatchDocumentSpec.Start` never stopped its hosts;
   `AppHostBinderHostedService` releases its lease only in `StopAsync`. Every started host is now
   stopped in `finally` and disposed via `using var host` (final review follow-up), and a failed
   start attempts `StopAsync` before `Dispose` (a partially started host may already hold the
   AppHost lease) while preserving the original start failure. With the leak fixed, the full
   WellKnown suite, including `WebStartupHostOwnershipSpec.Pipeline_construction_preserves_a_newer_attached_owner`
   (previously failing deterministically in full-suite order), passes. Earlier attribution runs
   showed the same failure without any AE-16 file present: the new fixtures' leaked leases were
   the deterministic trigger on top of older fixtures' pre-existing leak pattern; the fix removes
   this slice's contribution and the observed interaction. No global `AppHost.Current` reset.
5. Added `Dry_run_rehearses_the_copy_and_persists_nothing` (endpoint-level, additive/private
   stored state unchanged), `Duplicate_cased_patch_members_refuse_ambiguity`, and
   dictionary-key controls as demanded.
6. CONFIRMED dictionary-key blocker (follow-up): the new PATCH path serializes through
   `DocumentSettings`, whose `CamelCasePropertyNamesContractResolver` camelized dictionary keys,
   so any later patch collapsed stored mixed-case keys (the first-round test stopped before a
   second patch and missed it). Fixed in the Data owner: `DocumentSettings` now uses
   `DefaultContractResolver` with `CamelCaseNamingStrategy { ProcessDictionaryKeys = false,
   OverrideSpecifiedNames = true }`; patch `Admission` aligned. Reproduced first:
   `dictionary-collapse-before.trx` and `dictionary-roundtrip-before.trx` fail (both patch modes
   collapse `first`/`FIRST` on a name-only re-patch; shared document/token round trip recases
   keys). Regression `Mixed_case_dictionary_keys_survive_repeated_patches` seeds distinct
   `first`/`FIRST` before the first patch and proves: name-only patch retains both exact
   keys/values, a targeted `FIRST` patch touches only `FIRST`, and a subsequent unrelated patch
   retains both. Shared Data regression
   `Mixed_case_dictionary_keys_survive_document_and_token_round_trips` covers
   `SerializeDocument` and `SerializeDocumentToken` round trips. No patch-specific serializer was
   added and no caller-supplied `Apply` settings were overridden.

## Explicit non-claims

The separate `Entity.Patch`/`PatchMerge` to `Data.Patch` to `PatchOpsExecutor` path does not call
these applicators; its per-operation bare JSON, null-policy and partition issues are explicitly
unrepaired by this slice. Existing unused controller `NormalizeFrom*` helpers are not an active
execution path. The Data.Patch normalizer's empty-object omission and unescaped alias paths remain
open Data findings. The Json file provider keeps its own caller-supplied resolver (dictionary keys
in its own files follow its existing convention); only the shared Data `DocumentSettings` changed.

## Hypotheses withdrawn

- "The WebStartupHostOwnershipSpec full-suite failure is unrelated/pre-existing": partially
  withdrawn. It required both an ordering interaction and leaked host leases; this slice's
  fixtures leaked and were the deterministic trigger in the observed ordering. Fixed at the
  fixture, not by a production change.
- "Clone-wholesale is acceptable for supplied subtrees/arrays": withdrawn by Astra counterexample;
  replaced by recursive admission.
- "Member removal implements merge SetDefault": withdrawn; explicit CLR defaults/nulls are
  required so constructor seeds cannot resurface.
- "The document serializer's key casing is outside this slice": withdrawn by the confirmed
  blocker; keys are data, and the Data-owned `DocumentSettings` now preserves them exactly.
