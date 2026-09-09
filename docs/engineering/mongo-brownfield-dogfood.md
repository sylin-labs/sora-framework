# Mongo brownfield compatibility

**Task:** Restore managed BSON enum representation and custom collection round trips.
**Application intent:** Existing documents must remain readable and searchable without rewriting live data.
**Public expression:** Existing Entity models, including IEnumerable<T> value buckets with Add(T), saved and queried through AddKoan and Mongo.
**Guarantee/correction:** Managed enum writes use the documented string convention; queries use the same string representation. Numeric regression data requires a separate explicit repair. Collection materialization reconstructs the values Koan serialized. Unsupported collection shapes retain Json.NET's corrective error.
**Complete intent surface:** No application options or new model attributes.
**Public concepts:** None added.
**Docs read:** Mongo README and TECHNICAL establish one compiled adapter and BSON ownership. CLAUDE and architecture principles require one semantic owner and brownfield fidelity. EntityRoundTripSymmetrySpec defines read/write symmetry.
**Code read:** MongoEntityPlan serializes through Json.NET; MongoValues encodes enums numerically; MongoQueryCompiler shares filter encoding; EntityJsonContractResolver owns round-trip symmetry; MongoIdentityEncodingMatrixSpec describes string enum storage but only tested queries.
**Reusing:** StringEnumConverter, compiled field resolution, existing array contracts and constructor factories, existing Mongo fixture and Entity round-trip specs. Constant/options/contract searches found no new required identifiers or options.
**Creating new:** A CreateArrayContract override in Data.Core, focused raw BSON Mongo regression in the adapter tests, and custom collection round-trip cases in the core tests.
**Coalescence:** Rebuild array hydration at the shared Entity JSON owner; restore enum encoding at Mongo. No application converter or alternate repository. Existing default factories and parameterized collection constructors remain authoritative.
**Ergonomics:** Entity.Save/Get/Query remain the entire consumer expression.
**Constraints satisfied:** No new HTTP routes, no provider replacement, no data deletion, no application identities, bounded fixture tests against existing Mongo.
**Risks:** Enum comparisons must preserve string storage; explicit maps retain their declared codecs. Constructor/Add behavior is domain-owned, as in ordinary collection hydration.
