using Koan.Web.Endpoints;
using Koan.Web.Hooks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Koan.Web.WellKnown.Tests;

public sealed class EmitHookPipelineSpec
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacements_flow_to_later_hooks_in_order(bool collection)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new HookContext<object>(new EntityRequestContext(services, new QueryOptions(), default));
        var observed = new List<string>();
        var runner = new HookRunner<object>([], [], [], [
            new Hook(20, (_, value) => { observed.Add((string)value); return EmitDecision.With(value + "-second"); }),
            new Hook(10, (_, value) => EmitDecision.With(value + "-first")),
            new Hook(15, (_, value) => { observed.Add((string)value); return EmitDecision.Next(); })]);
        var result = collection ? await runner.EmitCollection(context, "initial") : await runner.EmitModel(context, "initial");
        Assert.True(result.replaced);
        Assert.Equal("initial-first-second", result.payload);
        Assert.Equal(["initial-first", "initial-first"], observed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Explicit_stop_wins_over_replacement_and_preserves_payload(bool collection, bool actionResult)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new HookContext<object>(new EntityRequestContext(services, new QueryOptions(), default));
        object stopped = actionResult ? new NotFoundResult() : new { error = "hidden" };
        var calls = 0;
        var runner = new HookRunner<object>([], [], [], [
            new Hook(10, (ctx, _) => { ctx.ShortCircuit(stopped); return EmitDecision.With("must not escape"); }),
            new Hook(20, (_, _) => { calls++; return EmitDecision.Next(); })]);
        var result = collection ? await runner.EmitCollection(context, "initial") : await runner.EmitModel(context, "initial");
        Assert.True(result.replaced);
        Assert.Same(stopped, result.payload);
        Assert.Equal(0, calls);
    }

    private sealed class Hook(int order, Func<HookContext<object>, object, EmitDecision> emit) : IEmitHook<object>
    {
        public int Order => order;
        public Task<EmitDecision> OnEmitCollection(HookContext<object> context, object payload) => Task.FromResult(emit(context, payload));
        public Task<EmitDecision> OnEmitModel(HookContext<object> context, object payload) => Task.FromResult(emit(context, payload));
    }

    [Fact]
    public async Task Model_projection_refuses_before_mapper_or_later_hook()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var context = new HookContext<object>(new EntityRequestContext(services, new QueryOptions(), default));
        var row = new object();
        var maps = 0;
        var later = 0;
        var runner = new HookRunner<object>([], [], [], [
            new Hook(0, (_, _) => EmitDecision.Project(new[] { row }, _ => { maps++; return "view"; })),
            new Hook(1, (_, _) => { later++; return EmitDecision.Next(); })]);
        await Assert.ThrowsAsync<NotSupportedException>(() => runner.EmitModel(context, row));
        Assert.Equal(0, maps);
        Assert.Equal(0, later);
    }
}
