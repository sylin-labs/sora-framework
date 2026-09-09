using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Data.Core.Relationships;
using Koan.Web.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Koan.Web.Endpoints;

/// <summary>
/// AN-leak (docs/assessment/09 §10) — governed relationship expansion for the agent/HTTP read path.
///
/// Domain <c>Entity&lt;T,K&gt;.Relatives()</c> traversal is app-authority: it deliberately has no HTTP
/// request predicates. Reusing it on the agent/HTTP path would let a caller read a visible parent and
/// receive related rows a direct query of that type would hide (WEB-0068).
///
/// This expander resolves each edge as a <em>governed</em> query through the related type's own
/// visibility pipeline: it runs the related type's <see cref="IRequestOptionsHook{TEntity}"/>s for the
/// same request (principal + headers), AND-composes the contributed predicates with the foreign-key
/// filter, and hands the normalized query to the shared capability-aware executor. An edge inherits its resolved query's
/// projection (DECIDED #1); a related row hidden by predicate produces no count, no field name, no
/// existence signal (walled-means-silent, §9.4). The domain API remains app-authority while both paths
/// share Data.Core's bounded backend negotiation.
///
/// MCP rides the same <see cref="IEntityEndpointService{TEntity,TKey}"/>, so fixing the endpoint fixes
/// every transport — the governance is not duplicated per transport (the AN3 lesson).
/// </summary>
internal static class GovernedRelationshipExpander
{
    private static readonly MethodInfo ResolveChildrenMethod =
        typeof(GovernedRelationshipExpander).GetMethod(nameof(ResolveChildrenMany), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo ResolveParentMethod =
        typeof(GovernedRelationshipExpander).GetMethod(nameof(ResolveParentsMany), BindingFlags.NonPublic | BindingFlags.Static)!;

    public static async Task<RelationshipGraph<TEntity>> ExpandAsync<TEntity, TKey>(
        TEntity entity,
        TKey entityId,
        EntityRequestContext context,
        ICollection<Func<bool>>? retainedEvidence = null)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var graphs = await ExpandManyAsync<TEntity, TKey>([(entity, entityId)], context, retainedEvidence);
        return graphs[0];
    }

    public static async Task<IReadOnlyList<RelationshipGraph<TEntity>>> ExpandManyAsync<TEntity, TKey>(
        IReadOnlyList<(TEntity Entity, TKey Id)> roots,
        EntityRequestContext context,
        ICollection<Func<bool>>? retainedEvidence = null)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        if (roots.Count == 0) return Array.Empty<RelationshipGraph<TEntity>>();

        var services = context.Services;
        var metadata = services.GetRequiredService<IRelationshipMetadata>();
        var ct = context.CancellationToken;
        var graphs = roots.Select(root => new RelationshipGraph<TEntity> { Entity = root.Entity }).ToArray();
        var evidence = new List<Func<bool>>();

        // Parents: a relationship foreign key on the root resolves to the parent type, gated by the
        // parent type's own visibility predicates. A null FK keeps the existing present-with-null shape
        // (the relationship simply isn't set); a walled or missing parent is omitted — the FK scalar
        // already lives on the root row, so omission discloses nothing new (T-parent, walled-means-silent).
        foreach (var (propertyName, parentType) in metadata.GetParentRelationships(typeof(TEntity)))
        {
            var foreignKeys = roots.Select(root => ReadForeignKey(root.Entity, propertyName)).ToArray();
            var ids = foreignKeys.Where(key => key is not null).Cast<TKey>().Distinct().ToArray();
            var task = (Task<IReadOnlyDictionary<TKey, object>>)ResolveParentMethod
                .MakeGenericMethod(parentType, typeof(TKey))
                .Invoke(null, new object?[] { services, context, ids, evidence, ct })!;
            var parents = await task;
            for (var index = 0; index < roots.Count; index++)
            {
                var foreignKey = foreignKeys[index];
                if (foreignKey is null)
                {
                    graphs[index].Parents[propertyName] = null;
                    continue;
                }
                if (parents.TryGetValue((TKey)foreignKey, out var parent))
                    graphs[index].Parents[propertyName] = parent;
            }
        }

        // Children: each (referenceProperty, childType) edge resolves to a governed query of the child
        // type filtered by FK in the root-id set, AND-composed with the child's visibility predicates and
        // negotiated once per edge for the whole root batch. An edge that resolves to zero
        // visible rows is omitted entirely — no empty-but-present edge that would leak the relationship.
        foreach (var (referenceProperty, childType) in metadata.GetChildRelationships(typeof(TEntity)))
        {
            var task = (Task<IReadOnlyDictionary<TKey, IReadOnlyList<object>>>)ResolveChildrenMethod
                .MakeGenericMethod(typeof(TEntity), childType, typeof(TKey))
                .Invoke(null, new object?[] { services, context, referenceProperty, roots.Select(root => root.Id).ToArray(), evidence, ct })!;
            var rowsByParent = await task;
            for (var index = 0; index < roots.Count; index++)
            {
                if (!rowsByParent.TryGetValue(roots[index].Id, out var rows) || rows.Count == 0)
                    continue;

                var graph = graphs[index];
                var childTypeName = childType.Name;
                if (!graph.Children.TryGetValue(childTypeName, out var byProperty))
                {
                    byProperty = new Dictionary<string, IReadOnlyList<object>>();
                    graph.Children[childTypeName] = byProperty;
                }

                byProperty[referenceProperty] = rows;
            }
        }

        if (evidence.Any(check => !check()))
            throw new NotSupportedException("Relationship query authorization evidence changed before graph projection.");
        if (retainedEvidence is not null)
            foreach (var check in evidence) retainedEvidence.Add(check);
        return graphs;
    }

    private static async Task<IReadOnlyDictionary<TKey, IReadOnlyList<object>>> ResolveChildrenMany<TParent, TChild, TKey>(
        IServiceProvider services,
        EntityRequestContext rootContext,
        string referenceProperty,
        IReadOnlyCollection<TKey> parentIds,
        ICollection<Func<bool>> retainedEvidence,
        CancellationToken ct)
        where TParent : class, IEntity<TKey>
        where TChild : class, IEntity<TKey>
        where TKey : notnull
    {
        var relatedContext = await ResolveVisibilityContext<TChild>(services, rootContext, ct);
        if (relatedContext is null)
        {
            // The child type's options pipeline short-circuited (denied) — the whole edge is walled.
            return new Dictionary<TKey, IReadOnlyList<object>>();
        }

        var visibility = relatedContext.Options.Filter;
        var proof = EndpointReadProof<TChild, TKey>.Prepare(relatedContext,
            QueryDefinition.All.Where(Filter.And(Filter.In(referenceProperty, parentIds.Cast<object?>().ToArray()), visibility)));
        var options = services.GetRequiredService<IOptions<EntityEndpointOptions>>().Value;
        var policy = new RelationshipQueryPolicy
        {
            MaxResults = options.RelationshipMaxResults,
            MaxFallbackCandidates = options.RelationshipFallbackMaxCandidates
        };
        var executor = services.GetRequiredService<IRelationshipQueryExecutor>();
        var edge = await executor.LoadChildren<TParent, TChild, TKey>(
            parentIds,
            referenceProperty,
            visibility,
            policy,
            rootContext.HttpContext?.TraceIdentifier,
            ct);
        if (proof is not null)
        {
            var rows = edge.ByParent.Values.SelectMany(items => items).ToArray();
            if (!proof.Bind(edge.ReadEvidence, rows))
                throw new NotSupportedException("The child query authorization evidence changed before relationship projection.");
            retainedEvidence.Add(() => proof.IsValid(relatedContext, rows));
        }
        return edge.ByParent.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<object>)pair.Value.Cast<object>().ToArray());
    }

    private static async Task<IReadOnlyDictionary<TKey, object>> ResolveParentsMany<TParent, TKey>(
        IServiceProvider services,
        EntityRequestContext rootContext,
        IReadOnlyCollection<TKey> parentIds,
        ICollection<Func<bool>> retainedEvidence,
        CancellationToken ct)
        where TParent : class, IEntity<TKey>
        where TKey : notnull
    {
        if (parentIds.Count == 0) return new Dictionary<TKey, object>();
        var relatedContext = await ResolveVisibilityContext<TParent>(services, rootContext, ct);
        if (relatedContext is null)
        {
            return new Dictionary<TKey, object>();
        }

        var filter = Filter.And(Filter.In(nameof(IEntity<TKey>.Id), parentIds.Cast<object?>().ToArray()),
            relatedContext.Options.Filter);
        var query = QueryDefinition.All.Where(Filter.Snapshot(filter)).WithPagination(1, parentIds.Count);
        var proof = EndpointReadProof<TParent, TKey>.Prepare(relatedContext, query);
        var repository = services.GetRequiredService<IDataService>().GetRepository<TParent, TKey>()
            as IQueryRepository<TParent, TKey>
            ?? throw new NotSupportedException("The selected connector does not support governed parent queries.");
        var result = await repository.Query(query.WithCountStrategy(null), ct);
        if (proof is not null && !proof.Bind(result.ReadEvidence, result.Items))
            throw new NotSupportedException("The parent query authorization evidence changed before relationship projection.");
        if (proof is not null) retainedEvidence.Add(() => proof.IsValid(relatedContext, result.Items));
        return result.Items.ToDictionary(parent => parent.Id, parent => (object)parent);
    }

    /// <summary>
    /// Runs the related type's <see cref="IRequestOptionsHook{TEntity}"/>s for THIS request (same
    /// principal + headers) so its WEB-0068 visibility predicates are produced. Returns <c>null</c> when
    /// the options pipeline short-circuits (a denial) — the edge is then treated as fully walled.
    /// </summary>
    private static async Task<EntityRequestContext?> ResolveVisibilityContext<TRelated>(
        IServiceProvider services,
        EntityRequestContext rootContext,
        CancellationToken ct)
        where TRelated : class
    {
        var options = new QueryOptions();
        var relatedContext = new EntityRequestContext(
            services, options, ct, rootContext.HttpContext, rootContext.User);

        var pipeline = services.GetRequiredService<IEntityHookPipeline<TRelated>>();
        var hookContext = pipeline.CreateContext(relatedContext);

        var allowed = await pipeline.BuildOptions(hookContext, options);
        options.Filter = Filter.Snapshot(options.Filter);
        return allowed ? relatedContext : null;
    }

    private static object? ReadForeignKey(object entity, string propertyName)
    {
        var property = entity.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        return property?.GetValue(entity);
    }
}
