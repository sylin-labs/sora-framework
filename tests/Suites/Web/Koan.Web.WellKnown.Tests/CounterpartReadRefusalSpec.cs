using System.Net;
using AwesomeAssertions;
using Koan.Core;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Web.Authorization;
using Koan.Web.Controllers;
using Koan.Web.Extensions;
using Koan.Web.Hooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Koan.Web.WellKnown.Tests;

public sealed class CounterpartReadRefusalSpec
{
    [Fact]
    public async Task Unsupported_provider_refuses_collection_and_keyed_reads_before_model_callbacks()
    {
        var callbacks = new CounterpartRefusalCallbacks();
        using var host = await Start(callbacks);
        var row = await new UnsupportedCounterpartMemo { Text = "Must stay private" }.Save();
        var client = host.GetTestClient();
        foreach (var path in new[] { "/counterpart-refusal", "/counterpart-refusal/" + row.Id })
        {
            var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var text = await response.Content.ReadAsStringAsync();
            text.Should().ContainEquivalentOf("counterpart").And.NotContain("Must stay private");
        }
        callbacks.AfterFetch.Should().Be(0);
        await host.StopAsync();
    }

    [Fact]
    public async Task Template_refuses_a_counterpart_realization_before_template_callback()
    {
        var callbacks = new CounterpartRefusalCallbacks();
        using var host = await Start(callbacks);
        var response = await host.GetTestClient().GetAsync("/counterpart-refusal/new");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("persisted identity");
        callbacks.AfterFetch.Should().Be(0);
        await host.StopAsync();
    }

    [Fact]
    public async Task Row_only_projection_refuses_a_mutable_identity_before_mapper()
    {
        var hook = new MutableProjectionHook();
        using var host = await Start(new CounterpartRefusalCallbacks(), hook);
        await new MutableProjectionMemo { Id = [1, 2], Text = "Source" }.Save();
        var response = await host.GetTestClient().GetAsync("/mutable-projection");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("immutable");
        hook.Maps.Should().Be(0);
        await host.StopAsync();
    }

    private static async Task<IHost> Start(CounterpartRefusalCallbacks callbacks, MutableProjectionHook? projection = null)
    {
        var host = Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.UseEnvironment("Test");
            web.ConfigureServices(services =>
            {
                services.AddKoan();
                services.AddKoanControllersFrom<UnsupportedCounterpartController>();
                services.AddSingleton<IModelHook<UnsupportedCounterpartMemo>>(callbacks);
                services.AddSingleton<ICollectionHook<UnsupportedCounterpartMemo>>(callbacks);
                if (projection is not null) services.AddSingleton<IEmitHook<MutableProjectionMemo>>(projection);
            });
            web.Configure(_ => { });
        }).Build();
        await host.StartAsync();
        return host;
    }
}

public sealed class MutableProjectionMemo : Entity<MutableProjectionMemo, byte[]>
{
    public string Text { get; set; } = "";
}

[Route("mutable-projection")]
public sealed class MutableProjectionController : EntityController<MutableProjectionMemo, byte[]>;

internal sealed class MutableProjectionHook : IEmitHook<MutableProjectionMemo>
{
    public int Order => 0;
    public int Maps { get; private set; }
    public Task<EmitDecision> OnEmitCollection(HookContext<MutableProjectionMemo> context, object payload)
        => Task.FromResult(EmitDecision.Project((IEnumerable<MutableProjectionMemo>)payload, row =>
        {
            Maps++;
            row.Id[0]++;
            return new { row.Text };
        }));
    public Task<EmitDecision> OnEmitModel(HookContext<MutableProjectionMemo> context, object payload)
        => Task.FromResult<EmitDecision>(EmitDecision.Next());
}

[Access(read: Access.Anyone)]
public sealed class UnsupportedCounterpartMemo : Entity<UnsupportedCounterpartMemo>
{
    public string Text { get; set; } = "";
}

public sealed class UnsupportedCounterpartAccess : EntityAccess<UnsupportedCounterpartMemo>
{
    public override IAccessFilter<UnsupportedCounterpartMemo> Constrain(IAccessFilter<UnsupportedCounterpartMemo> filter, AccessAction action)
        => action == AccessAction.Read ? filter.Where(row => true, partition: "authority") : filter;
}

[Route("counterpart-refusal")]
public sealed class UnsupportedCounterpartController : EntityController<UnsupportedCounterpartMemo>;

internal sealed class CounterpartRefusalCallbacks : IModelHook<UnsupportedCounterpartMemo>, ICollectionHook<UnsupportedCounterpartMemo>
{
    public int Order => 0;
    public int AfterFetch { get; private set; }
    public Task OnBeforeFetch(HookContext<UnsupportedCounterpartMemo> context, string id) => Task.CompletedTask;
    public Task OnBeforeFetch(HookContext<UnsupportedCounterpartMemo> context, QueryOptions options) => Task.CompletedTask;
    public Task OnAfterFetch(HookContext<UnsupportedCounterpartMemo> context, UnsupportedCounterpartMemo? model)
    { AfterFetch++; return Task.CompletedTask; }
    public Task OnAfterFetch(HookContext<UnsupportedCounterpartMemo> context, List<UnsupportedCounterpartMemo> models)
    { AfterFetch++; return Task.CompletedTask; }
    public Task OnBeforeSave(HookContext<UnsupportedCounterpartMemo> context, UnsupportedCounterpartMemo model) => Task.CompletedTask;
    public Task OnAfterSave(HookContext<UnsupportedCounterpartMemo> context, UnsupportedCounterpartMemo model) => Task.CompletedTask;
    public Task OnBeforeDelete(HookContext<UnsupportedCounterpartMemo> context, UnsupportedCounterpartMemo model) => Task.CompletedTask;
    public Task OnAfterDelete(HookContext<UnsupportedCounterpartMemo> context, UnsupportedCounterpartMemo model) => Task.CompletedTask;
    public Task OnBeforePatch(HookContext<UnsupportedCounterpartMemo> context, string id, object patch) => Task.CompletedTask;
    public Task OnAfterPatch(HookContext<UnsupportedCounterpartMemo> context, UnsupportedCounterpartMemo model) => Task.CompletedTask;
}
