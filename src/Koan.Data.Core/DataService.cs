using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Sources;
using Koan.Data.Core.Configuration;
using Koan.Data.Core.Polymorphism;
using System.Reflection;
using Koan.Data.Core.Runtime;
using Koan.Data.Core.Routing;
using Microsoft.Extensions.Options;

namespace Koan.Data.Core;

/// <summary>
/// Default <see cref="IDataService"/> implementation.
/// Uses multi-dimensional caching per (entity, key, adapter, source) combination.
/// </summary>
public sealed class DataService : IDataService
{
    private readonly IServiceProvider _sp;
    private readonly BoundedSingleFlightCache<CacheKey, object> _cache;
    private readonly BoundedSingleFlightCache<(Type Variant, Type Key), object> _variantCache;

    private static readonly MethodInfo CreateVariantRepositoryMethod = typeof(DataService)
        .GetMethod(nameof(CreateVariantRepositoryCore), BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo GetRootScopeDiagnosticsMethod = typeof(DataService)
        .GetMethod(nameof(GetRootScopeDiagnosticsCore), BindingFlags.Instance | BindingFlags.NonPublic)!;

    private record CacheKey(
        Type EntityType,
        Type KeyType,
        string Adapter,
        string Source,
        string BindingIdentity);

    public DataService(IServiceProvider services, IOptions<DataRuntimeOptions> options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        _sp = services;
        _cache = new BoundedSingleFlightCache<CacheKey, object>(
            options.Value.RepositoryEntries,
            "Entity repository cache");
        _variantCache = new BoundedSingleFlightCache<(Type Variant, Type Key), object>(
            options.Value.VariantRepositoryEntries,
            "Entity variant repository cache");
    }

    /// <inheritdoc />
    public IDataRepository<TEntity, TKey> GetRepository<TEntity, TKey>()
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var descriptor = EntityRootDescriptor.For(typeof(TEntity));
        if (descriptor.KeyType != typeof(TKey))
        {
            throw new InvalidOperationException(
                $"Entity '{typeof(TEntity).FullName}' uses key '{descriptor.KeyType.FullName}', not '{typeof(TKey).FullName}'.");
        }

        if (descriptor.IsVariant)
        {
            return (IDataRepository<TEntity, TKey>)_variantCache.GetOrAdd(
                (typeof(TEntity), typeof(TKey)),
                () => CreateVariantRepositoryMethod
                    .MakeGenericMethod(descriptor.RootType, typeof(TEntity), typeof(TKey))
                    .Invoke(this, null)
                    ?? throw new InvalidOperationException(
                        $"Could not create the '{typeof(TEntity).FullName}' view of Entity root '{descriptor.RootType.FullName}'."));
        }

        var sourceRegistry = _sp.GetRequiredService<DataSourceRegistry>();
        var decision = AdapterResolver.ResolveDecisionForEntity<TEntity>(_sp, sourceRegistry);
        var adapter = decision.Adapter;
        var source = decision.Source;
        var binding = decision.Binding
            ?? throw new InvalidOperationException("The resolved Data route did not carry an operation binding.");
        var sourcePlan = binding.Plan;

        var key = new CacheKey(typeof(TEntity), typeof(TKey), adapter, source, binding.RepositoryIdentity);

        return (IDataRepository<TEntity, TKey>)_cache.GetOrAdd(
            key,
            () => CreateRepository<TEntity, TKey>(decision, sourcePlan, binding));
    }

    private object CreateRepository<TEntity, TKey>(
        AdapterResolutionDecision decision,
        DataSourcePlan sourcePlan,
        DataRouteBinding binding)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var factory = decision.Factory;
        var source = decision.Source;

        // Selection is the activation boundary, including failed first use. From this point the
        // application depends on this route, so readiness must report it even when repository
        // construction or the first provider operation cannot connect.
        var diagnostics = _sp.GetService<DataDiagnostics>();
        diagnostics?.ObserveSourcePlan(sourcePlan, DataClaimSet.Describe(factory, source));
        diagnostics?.ObserveParticipation(
            factory.Provider,
            source,
            binding.IsDefaultDerived
                ? DataAdapterParticipationRole.DefaultDerived
                : DataAdapterParticipationRole.Explicit);

        // Create repository with source context
        var repo = factory.Create<TEntity, TKey>(_sp, source);

        // Provider/module decorators sit inside the Data-owned facade. They may cache or specialize
        // physical access, but cannot bypass guards, isolation, transforms, or Lifecycle by returning
        // early. The facade is therefore the one unavoidable application-facing repository boundary.
        var decorated = ApplyDecorators(typeof(TEntity), typeof(TKey), repo, binding, _sp);

        // Wrap once with the Data-owned semantic boundary: source policy, guards, isolation, transforms,
        // write stamps and Lifecycle. The provisioning-ready seam is used only for Managed + ReadWrite;
        // constrained sources require adapter-earned non-creating readiness.
        var guards = _sp.GetServices<Pipeline.IStorageGuard>().ToArray();
        var readContributors = _sp.GetServices<Pipeline.IReadFilterContributor>().ToArray();
        var lifecycle = _sp.GetService<Lifecycle.EntityLifecyclePlan<TEntity, TKey>>();
        var segmentation = _sp.GetRequiredService<Semantics.DataSegmentationPlan>().For(typeof(TEntity));
        var fieldTransforms = _sp.GetRequiredService<Pipeline.StorageFieldTransformPlan>();
        var facade = new RepositoryFacade<TEntity, TKey>(
            decorated,
            guards,
            readContributors,
            lifecycle,
            segmentation,
            fieldTransforms,
            sourcePlan,
            _sp.GetRequiredService<DataOperationHorizon>(),
            binding);

        // Repository construction is the activation boundary: inspection and route description remain pure, while
        // any runtime path that actually asks for a repository makes that provider/source visible to readiness.
        diagnostics?.Observe(new EntityConfigInfo(
            typeof(TEntity).FullName ?? typeof(TEntity).Name,
            typeof(TKey).FullName ?? typeof(TKey).Name,
            factory.Provider,
            AggregateMetadata.GetIdSpec(typeof(TEntity))?.Prop.Name));

        return facade;
    }

    /// <inheritdoc />
    public Axes.IAxisScopeDiagnostics GetScopeDiagnostics<TEntity, TKey>()
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var descriptor = EntityRootDescriptor.For(typeof(TEntity));
        if (descriptor.IsVariant)
        {
            return (Axes.IAxisScopeDiagnostics)(
                GetRootScopeDiagnosticsMethod
                    .MakeGenericMethod(descriptor.RootType, typeof(TKey))
                    .Invoke(this, null)
                ?? throw new InvalidOperationException(
                    $"Could not inspect Entity root '{descriptor.RootType.FullName}'."));
        }

        // Mirror GetRepository's raw-adapter resolution but return the UNDECORATED facade (the diagnostic authority that
        // holds the raw adapter for the IQueryRepository check). Cheap + connection-free: capability description is
        // static. Not cached — Explain / the boot pre-flight call it rarely, never on a hot path.
        var sourceRegistry = _sp.GetRequiredService<DataSourceRegistry>();
        var decision = AdapterResolver.ResolveDecisionForEntity<TEntity>(_sp, sourceRegistry);
        var source = decision.Source;
        var binding = decision.Binding
            ?? throw new InvalidOperationException("The resolved Data route did not carry an operation binding.");
        var sourcePlan = binding.Plan;
        var factory = decision.Factory;
        _sp.GetService<DataDiagnostics>()?.ObserveSourcePlan(sourcePlan, DataClaimSet.Describe(factory, source));
        var repo = factory.Create<TEntity, TKey>(_sp, source);
        var guards = _sp.GetServices<Pipeline.IStorageGuard>().ToArray();
        var readContributors = _sp.GetServices<Pipeline.IReadFilterContributor>().ToArray();
        var lifecycle = _sp.GetService<Lifecycle.EntityLifecyclePlan<TEntity, TKey>>();
        var segmentation = _sp.GetRequiredService<Semantics.DataSegmentationPlan>().For(typeof(TEntity));
        var fieldTransforms = _sp.GetRequiredService<Pipeline.StorageFieldTransformPlan>();
        return new RepositoryFacade<TEntity, TKey>(
            repo,
            guards,
            readContributors,
            lifecycle,
            segmentation,
            fieldTransforms,
            sourcePlan,
            _sp.GetRequiredService<DataOperationHorizon>(),
            binding);
    }

    /// <inheritdoc />
    public Direct.IDirectSession Direct(string? source = null, string? adapter = null)
    {
        var svc = _sp.GetService<Direct.IDirectDataService>()
            ?? throw new InvalidOperationException("IDirectDataService not registered. It is registered by default via AddKoanDataCore() (ARCH-0090 §1) — ensure Koan data core is initialized.");
        return svc.Direct(source, adapter);
    }

    /// <summary>Applies an Entity instruction's source ceiling before repository/provider construction.</summary>
    internal void DemandForEntity<TEntity>(DataOperationEffect effect, string operation)
        where TEntity : class
    {
        var sourceRegistry = _sp.GetRequiredService<DataSourceRegistry>();
        var decision = AdapterResolver.ResolveDecisionForEntity<TEntity>(_sp, sourceRegistry);
        sourceRegistry.GetPlan(decision.Source, decision.Adapter).Demand(effect, operation);
    }

    private static IDataRepository<TEntity, TKey> ApplyDecorators<TEntity, TKey>(
        Type entityType,
        Type keyType,
        IDataRepository<TEntity, TKey> repository,
        DataRouteBinding binding,
        IServiceProvider services)
        where TEntity : class, IEntity<TKey>
        where TKey : notnull
    {
        var decorators = services.GetService<IEnumerable<Decorators.IDataRepositoryDecorator>>();
        if (decorators is null)
        {
            return repository;
        }

        object current = repository;
        var context = new Decorators.DataRepositoryDecorationContext(
            binding.Plan.Source,
            binding.Plan.Adapter,
            binding.Namespace);

        foreach (var decorator in decorators)
        {
            var result = decorator is Decorators.IDataRouteAwareRepositoryDecorator routeAware
                ? routeAware.TryDecorate(entityType, keyType, current, context, services)
                : decorator.TryDecorate(entityType, keyType, current, services);
            if (result is not null)
            {
                current = result;
            }
        }

        return (IDataRepository<TEntity, TKey>)current;
    }

    private IDataRepository<TVariant, TKey> CreateVariantRepositoryCore<TRoot, TVariant, TKey>()
        where TRoot : class, IEntity<TKey>
        where TVariant : class, TRoot, IEntity<TKey>
        where TKey : notnull
    {
        EntityTypeCatalog.Register(typeof(TVariant));
        return new EntityVariantRepository<TRoot, TVariant, TKey>(
            () => GetRepository<TRoot, TKey>());
    }

    private Axes.IAxisScopeDiagnostics GetRootScopeDiagnosticsCore<TRoot, TKey>()
        where TRoot : class, IEntity<TKey>
        where TKey : notnull
        => GetScopeDiagnostics<TRoot, TKey>();

}
