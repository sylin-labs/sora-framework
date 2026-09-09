using Koan.Core.Capabilities;

namespace Koan.Data.Abstractions.Capabilities;

/// <summary>
/// The data pillar's capability tokens (ARCH-0084). Tokens live here — not in a global catalog — so
/// referencing the data abstractions is what surfaces them (Reference = Intent).
/// </summary>
public static class DataCaps
{
    /// <summary>Query negotiation tokens.</summary>
    public static class Query
    {
        /// <summary>Provider accepts server-side string queries.</summary>
        public static readonly Capability String = new("query.string");
        /// <summary>Provider accepts LINQ predicates.</summary>
        public static readonly Capability Linq = new("query.linq");
        /// <summary>Provider exposes a fast (approximate-allowed) count.</summary>
        public static readonly Capability FastCount = new("query.fastCount");
        /// <summary>Provider exposes an optimized exact count.</summary>
        public static readonly Capability OptimizedCount = new("query.optimizedCount");
        /// <summary>Filter pushdown — carries a <see cref="Filtering.FilterSupport"/> detail describing the operator set.</summary>
        public static readonly Capability Filter = new("query.filter");
        /// <summary>
        /// Physical filter execution posture. Carries a <see cref="FilterExecutionProfile"/> so
        /// consumers can distinguish native backend filtering from in-memory and keyspace scans.
        /// </summary>
        public static readonly Capability FilterExecution = new("query.filter.execution");
        /// <summary>
        /// Provider-enforced bounded paging. The provider faithfully executes the coordinator-supplied
        /// pushable candidate filter and applies <see cref="QueryDefinition.PageSize"/> before candidates
        /// are materialized into application memory. Each result must report provider-handled pagination
        /// and the complete total order. This does not imply snapshot isolation, resumability, or
        /// mutation-safe iteration.
        /// </summary>
        public static readonly Capability ProviderBoundedPaging = new("query.paging.providerBounded");
    }

    /// <summary>Write negotiation tokens.</summary>
    public static class Write
    {
        /// <summary>Provider supports bulk upsert.</summary>
        public static readonly Capability BulkUpsert = new("write.bulkUpsert");
        /// <summary>Provider supports bulk delete.</summary>
        public static readonly Capability BulkDelete = new("write.bulkDelete");
        /// <summary>Provider supports atomic batches.</summary>
        public static readonly Capability AtomicBatch = new("write.atomicBatch");
        /// <summary>Provider returns an exact insert/update result from its native upsert boundary.</summary>
        public static readonly Capability MutationOutcomes = new("write.mutationOutcomes");
        /// <summary>Provider atomically inserts an absent identity without replacing an existing row.</summary>
        public static readonly Capability InsertOnly = new("write.insertOnly");
        /// <summary>Provider supports a fast (unsafe-for-hooks) remove path.</summary>
        public static readonly Capability FastRemove = new("write.fastRemove");
        /// <summary>Provider supports an atomic conditional replace (compare-and-set by Id) — see
        /// <see cref="IConditionalWriteRepository{TEntity,TKey}"/>. Enables contention-free claiming (JOBS-0005 §20.3).</summary>
        public static readonly Capability ConditionalReplace = new("write.conditionalReplace");
    }

    /// <summary>Isolation negotiation tokens — the AODB three-mode conformance suite (ARCH-0103 §6; DATA-0105 /
    /// ARCH-0095). Axis-free: each token names the adapter <b>guarantee</b>, not the consumer that needs it
    /// (Koan.Tenancy <c>Require</c>s the mode its axis declares). Each token is <b>co-defined with its conformance
    /// check</b> (ARCH-0094): an adapter that declares a token but does not realize its mode fails the matching cell of
    /// <c>AodbConformanceSpecsBase</c> — so over-claim cannot stay green.</summary>
    public static class Isolation
    {
        /// <summary>Shared mode (FieldFilter). Provider can persist and filter a framework-managed row discriminator (a
        /// managed field, see <see cref="Pipeline.ManagedFieldDescriptor"/>): it stores the injected key with each
        /// record AND pushes a scalar equality on it, guarding a cross-scope write. A managed-scoped entity routed to an
        /// adapter lacking this token fails closed (the only token enforced at routing time today).</summary>
        public static readonly Capability RowScoped = new("isolation.rowScoped");

        /// <summary>Container mode (Particle). Provider resolves a <b>distinct physical container</b> (collection /
        /// table / keyspace / directory) per ambient partition, so writes under one partition are physically separate
        /// from another's — proven by the Container cell of <c>AodbConformanceSpecsBase</c> (per-partition isolation +
        /// concurrent no-leak + partition-name survival). Realized via the shared naming particle plane.</summary>
        public static readonly Capability ContainerScoped = new("isolation.containerScoped");

        /// <summary>Database mode (Moniker). Provider routes a Database-mode axis to a <b>distinct physical isolation
        /// unit</b> per routed source key — a distinct data <i>source</i> (connection / file / logical database) on the
        /// record plane, or a distinct collection/index/class via the source-name fold on the vector plane. The core
        /// guarantee is uniform: distinct source ⇒ distinct physical isolation. The <b>provisioning posture</b>, however,
        /// differs by plane and is NOT part of the token's guarantee: the record plane is external-only (a routed source
        /// absent from the registry <b>fails closed</b> — proven by the Database cell of <c>AodbConformanceSpecsBase</c>),
        /// while the vector name-fold floor is lazy (any ambient source resolves to a distinct name, so there is nothing
        /// to fail closed on — proven by <c>VectorAodbConformanceSpecsBase</c>). Realized via the shared <c>RoutedSource</c>
        /// + the per-source factory placement (record) or the source-name particle (vector).</summary>
        public static readonly Capability DatabaseScoped = new("isolation.databaseScoped");
    }

    /// <summary>Retention negotiation tokens.</summary>
    public static class Retention
    {
        /// <summary>Provider supports a store-native TTL index — a <c>[Index(Ttl = true)]</c> timestamp property whose
        /// rows the store expires automatically once it is in the past (e.g. Mongo <c>expireAfterSeconds = 0</c>).
        /// Adapters without this token ignore TTL indexes entirely. (JOBS-0005 §20.4)</summary>
        public static readonly Capability TtlIndex = new("retention.ttlIndex");
    }

    /// <summary>
    /// Resolves the data capabilities of <paramref name="source"/> from its
    /// <see cref="IDescribesCapabilities"/> declaration; returns an empty set when it declares none.
    /// </summary>
    public static CapabilitySet Describe(object source, string? owner = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return CapabilityResolver.TryDescribe(source, owner) ?? new CapabilitySet(owner);
    }
}
