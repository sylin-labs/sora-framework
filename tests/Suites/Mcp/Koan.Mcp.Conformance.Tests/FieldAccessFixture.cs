using Koan.Mcp.Options;
using Koan.Mcp.TestKit;
using Koan.Web.Hooks;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Mcp.Conformance.Tests;

public sealed class FieldAccessFixture : McpHarnessFixtureBase
{
    protected override void ConfigureServices(IServiceCollection services)
        => services.AddSingleton<IRequestOptionsHook<FieldShapeRecord>, FieldShapeDefault>();

    protected override void ConfigureKoan()
        => FieldAccessRecord.Lifecycle.BeforeUpsert(context =>
        {
            if (context.Current.Title == "rotate-server-metadata")
                context.Current.Metadata.OperatorCode = "rotated-private-code";
            return context.Proceed();
        });

    protected override void ConfigureMcp(McpServerOptions options)
    {
        options.AllowedEntities.Add("field-record");
        options.AllowedEntities.Add("field-shape-record");
    }
}

internal sealed class FieldShapeDefault : IRequestOptionsHook<FieldShapeRecord>
{
    public int Order => 0;

    public Task OnBuildingOptions(HookContext<FieldShapeRecord> context, QueryOptions options)
    {
        if (options.Extras.TryGetValue("testDefaultShape", out var shape)) options.Shape = shape;
        return Task.CompletedTask;
    }
}
