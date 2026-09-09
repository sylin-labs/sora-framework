using Koan.Core.Hosting.App;
using Koan.Data.Core;
using Koan.Web.Endpoints;
using Koan.Web.Hooks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Koan.Web.AdapterSurface.InMemory.Tests.PredicateHook;

public sealed class ModelHookAuthoritySpecs(InMemoryAdapterFactory factory) : IClassFixture<InMemoryAdapterFactory>
{
    [Theory]
    [InlineData("read", false)]
    [InlineData("new", false)]
    [InlineData("save", false)]
    [InlineData("save", true)]
    [InlineData("batch", false)]
    [InlineData("batch", true)]
    [InlineData("delete", false)]
    [InlineData("delete", true)]
    [InlineData("patch", false)]
    [InlineData("patch", true)]
    [InlineData("patch-save", false)]
    public async Task A_hook_stop_prevents_later_work_and_preserves_storage(string operation, bool dryRun)
    {
        using var appScope = AppHost.PushScope(factory.Services);
        using var scope = factory.Services.CreateScope();
        var id = Guid.CreateVersion7().ToString("N");
        await VisibilityWidget.Upsert(new VisibilityWidget { Id = id, Name = "before", Status = VisibilityStatus.Published });
        var context = new EntityRequestContext(scope.ServiceProvider, new QueryOptions(), default);
        context.Items[StoppingModelHook.StopKey] = operation switch
        {
            "read" or "new" => "read", "save" or "batch" or "patch-save" => "save", _ => operation
        };
        var endpoint = scope.ServiceProvider.GetRequiredService<IEntityEndpointService<VisibilityWidget, string>>();
        var changed = new VisibilityWidget { Id = id, Name = "after", Status = VisibilityStatus.Published };
        EntityEndpointResult result = operation switch
        {
            "read" => await endpoint.GetById(new() { Context = context, Id = id, With = "all" }),
            "new" => await endpoint.GetNew(new() { Context = context }),
            "save" => await endpoint.Upsert(new() { Context = context, Model = changed, DryRun = dryRun }),
            "batch" => await endpoint.UpsertMany(new() { Context = context, Models = [changed], DryRun = dryRun }),
            "delete" => await endpoint.Delete(new() { Context = context, Id = id, DryRun = dryRun }),
            _ => await endpoint.Patch(new() { Context = context, Id = id,
                Patch = JObject.Parse("{\"Name\":\"after\"}"), DryRun = dryRun })
        };
        Assert.IsType<NotFoundResult>(result.ShortCircuitResult);
        Assert.Null(result.Payload);
        Assert.Equal("before", (await VisibilityWidget.Get(id))?.Name);
        await VisibilityWidget.Remove(id);
    }
    [Theory]
    [InlineData("save")]
    [InlineData("delete")]
    [InlineData("patch")]
    public async Task Post_write_stop_preserves_the_completed_mutation(string operation)
    {
        using var appScope = AppHost.PushScope(factory.Services);
        using var scope = factory.Services.CreateScope();
        var id = Guid.CreateVersion7().ToString("N");
        await VisibilityWidget.Upsert(new VisibilityWidget { Id = id, Name = "before", Status = VisibilityStatus.Published });
        var context = new EntityRequestContext(scope.ServiceProvider, new QueryOptions(), default);
        context.Items[StoppingModelHook.StopKey] = "after-" + operation;
        var endpoint = scope.ServiceProvider.GetRequiredService<IEntityEndpointService<VisibilityWidget, string>>();
        EntityEndpointResult result = operation switch
        {
            "save" => await endpoint.Upsert(new() { Context = context,
                Model = new VisibilityWidget { Id = id, Name = "after", Status = VisibilityStatus.Published } }),
            "delete" => await endpoint.Delete(new() { Context = context, Id = id }),
            _ => await endpoint.Patch(new() { Context = context, Id = id, Patch = JObject.Parse("{\"Name\":\"after\"}") })
        };
        Assert.IsType<NotFoundResult>(result.ShortCircuitResult);
        Assert.Null(result.Payload);
        Assert.Equal(operation == "delete" ? null : "after", (await VisibilityWidget.Get(id))?.Name);
        await VisibilityWidget.Remove(id);
    }

}

internal sealed class StoppingModelHook : IModelHook<VisibilityWidget>
{
    public const string StopKey = "model-hook-stop";
    public int Order => 0;
    private static Task Stop(HookContext<VisibilityWidget> context, string phase)
    {
        if (context.Request.Items.TryGetValue(StopKey, out var selected) && Equals(selected, phase))
            context.ShortCircuit(new NotFoundResult());
        return Task.CompletedTask;
    }
    public Task OnBeforeFetch(HookContext<VisibilityWidget> c, string id) => Task.CompletedTask;
    public Task OnAfterFetch(HookContext<VisibilityWidget> c, VisibilityWidget? model) => Stop(c, "read");
    public Task OnBeforeSave(HookContext<VisibilityWidget> c, VisibilityWidget model) => Stop(c, "save");
    public Task OnAfterSave(HookContext<VisibilityWidget> c, VisibilityWidget model) => Stop(c, "after-save");
    public Task OnBeforeDelete(HookContext<VisibilityWidget> c, VisibilityWidget model) => Stop(c, "delete");
    public Task OnAfterDelete(HookContext<VisibilityWidget> c, VisibilityWidget model) => Stop(c, "after-delete");
    public Task OnBeforePatch(HookContext<VisibilityWidget> c, string id, object patch) => Stop(c, "patch");
    public Task OnAfterPatch(HookContext<VisibilityWidget> c, VisibilityWidget model) => Stop(c, "after-patch");
}
