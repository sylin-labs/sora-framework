# Existing Mongo index compatibility

**Task:** Start the current Gposingway build on an unchanged production database copy.
**Application intent:** An ordinary framework upgrade must preserve existing data and equivalent indexes.
**Public expression:** Existing Mongo package reference, `AddKoan()`, Entity models and `[Index]` declarations.
**Guarantee/correction:** An unnamed index declaration accepts an existing equivalent index regardless of its
historical generated name. Key order, uniqueness, expiry, collation and filtering must agree. Incompatible
indexes fail with corrective guidance; the adapter never drops or renames them automatically.
**Complete intent surface:** No new application configuration, migration or public type.
**Public concepts:** Existing explicit index names remain explicit; only convention-generated names are flexible.

**Docs read:** README and architecture principles require business intent over provider ceremony. Engineering
README and the TOC establish the owner documentation. Mongo README/TECHNICAL assign collection/index realization
to the adapter. The publishing workbook requires the normal main boundary and public package consumption.
**Code read:** MongoSchema blindly creates every declared index. MongoEntityPlan owns physical field names.
IndexMetadata preserves explicit names but leaves convention names null. MongoRepository ensures schema before
reads. MongoLegacyMappingSpec and MongoEnumStorageSpec provide the existing real-server characterization pattern.
**Reusing:** Existing index metadata, physical mapping, schema readiness gate, driver definitions and Mongo fixture.
Searches found no existing index reconciliation owner or option. No option is needed.

**Creating new:** Private comparison at MongoSchema's existing realization boundary, Mongo BSON field constants
in Infrastructure/Constants, and a focused MongoExistingIndexSpec in the existing connector test suite.
**Coalescence:** Rebuild unconditional index creation at the Mongo adapter owner. Keep Entity declarations and
the bounded readiness cache. Other adapters have different DDL semantics; no shared cross-provider abstraction.
**Ergonomics:** Applications retain their existing model declarations; no historical index names leak into them.
**Constraints satisfied:** No HTTP change, repository wrapper, new application service, production write,
configuration switch or version override. Tests use the existing Mongo service and owner fixture.
**Risks:** Matching only keys would silently accept partial, sparse or differently unique indexes. Explicit names
and incompatible definitions must remain actionable failures. Collection default collation must be considered.

The restored production copy contains 115,016 documents across 56 collections. Startup failed on `ix_Slug`
and `ix_WorkType_Status`: older Koan generated CLR-based names, while the current driver generates BSON-based
names. The app was stopped after capturing the restart-loop evidence. Production remained read-only.

The owner accepted per-physical-collection caching in the existing readiness gate. The five initial
characterizations failed before implementation. All 51 connector tests now pass, including nine new cases
covering historical names, constraints, collection collation, partitions, concurrent readers, explicit names
and retry after operator correction. Successful initialization retains the resolved names in its cached task.

The uniqueness characterization also exposed unconditional duplicate-key relabeling as a cross-scope write.
The existing MongoRepository error boundary now proves a conflicting scoped identity before applying that
diagnostic; ordinary single/bulk unique-key failures retain their driver exception. The full connector suite
includes the existing managed-field isolation oracle and both ordinary duplicate-write shapes.

## Native BSON preservation

The read-only codec scan covered 115,010 remaining copied documents after startup/TTL activity. All
materialized and serialized in memory without identity changes. However, CLR int fields became BSON Int64:
MongoEntityPlan.Write serializes to JSON text and reparses it, losing numeric widths, decimal token types
and binary token identity. MongoValues already preserves those native token kinds. The existing Data.Core
SerializeDocumentToken uses a JTokenWriter for the same reason. Reuse that standard Json.NET mechanism
inside MongoEntityPlan with its existing adapter-owned settings; no new public surface or application fix.
The native scalar characterization fails on Int32 versus Int64 before the change. Verify native widths,
decimal/binary round trips and the full Mongo owner suite before releasing.

The final Mongo suite passes all 52 tests, including the native scalar regression. The detailed copied-data
codec scan found no semantic value loss; prior nested differences were field ordering or numeric widths.
