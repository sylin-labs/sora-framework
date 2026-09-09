using Koan.Data.Abstractions;
using Koan.Web.Hooks;

namespace Koan.Web.Endpoints;

internal sealed class DefaultEntityHookPipeline<TEntity> : IEntityHookPipeline<TEntity> where TEntity : class
{
    private readonly HookRunner<TEntity> _runner;

    public DefaultEntityHookPipeline(
        IEnumerable<IRequestOptionsHook<TEntity>> optionHooks,
        IEnumerable<ICollectionHook<TEntity>> collectionHooks,
        IEnumerable<IModelHook<TEntity>> modelHooks,
        IEnumerable<IEmitHook<TEntity>> emitHooks)
    {
        _runner = new HookRunner<TEntity>(optionHooks, collectionHooks, modelHooks, emitHooks);
    }

    public HookContext<TEntity> CreateContext(EntityRequestContext requestContext)
        => new HookContext<TEntity>(requestContext);

    public Task<bool> BuildOptions(HookContext<TEntity> context, QueryOptions options)
        => _runner.BuildOptions(context, options);

    public Task<bool> BeforeCollection(HookContext<TEntity> context, QueryOptions options)
        => _runner.BeforeCollection(context, options);

    public Task<bool> AfterCollection(HookContext<TEntity> context, List<TEntity> items)
        => _runner.AfterCollection(context, items);

    public Task<bool> BeforeModelFetch(HookContext<TEntity> context, string id)
        => _runner.BeforeModelFetch(context, id);

    public Task<bool> AfterModelFetch(HookContext<TEntity> context, TEntity? model)
        => _runner.AfterModelFetch(context, model);

    public Task<bool> BeforeSave(HookContext<TEntity> context, TEntity model)
        => _runner.BeforeSave(context, model);

    public Task<bool> AfterSave(HookContext<TEntity> context, TEntity model)
        => _runner.AfterSave(context, model);

    public Task<bool> BeforeDelete(HookContext<TEntity> context, TEntity model)
        => _runner.BeforeDelete(context, model);

    public Task<bool> AfterDelete(HookContext<TEntity> context, TEntity model)
        => _runner.AfterDelete(context, model);

    public Task<bool> BeforePatch(HookContext<TEntity> context, string id, object patch)
        => _runner.BeforePatch(context, id, patch);

    public Task<bool> AfterPatch(HookContext<TEntity> context, TEntity model)
        => _runner.AfterPatch(context, model);

    public Task<(bool replaced, object payload)> EmitCollection(HookContext<TEntity> context, object payload)
        => _runner.EmitCollection(context, payload);

    public Task<(bool replaced, object payload)> EmitModel(HookContext<TEntity> context, object payload)
        => _runner.EmitModel(context, payload);
}
