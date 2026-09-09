using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Data.Core.Relationships;
using Koan.Mcp.CodeMode.Json;
using Koan.Mcp.TestKit;
using Koan.Web.Authorization;
using Koan.Web.Endpoints;
using Koan.Web.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Koan.Mcp.Conformance.Tests;

/// <summary>Enum names belong to the existing application JSON contract, including guarded typed output.</summary>
public sealed class EnumWireContractSpec(EnumWireFixture fixture) : IClassFixture<EnumWireFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Entity_detail_and_query_emit_named_enum_values(bool query)
    {
        var row = await Seed();
        var call = await Call(query ? EntityEndpointOperationKind.Query : EntityEndpointOperationKind.GetById,
            query ? Selection(row.Id) : new JObject { ["id"] = row.Id });
        var body = Body(call);
        var model = query ? body.Single() : body;
        Named(model["state"], "Green");
        Named(model["detail"]!["state"], "Blue");
        model["detail"]!["secret"].Should().BeNull();
        model["ignored"].Should().BeNull();
    }

    [Fact]
    public async Task Terminal_summary_projection_keeps_enum_names()
    {
        var row = await Seed();
        var args = Selection(row.Id);
        args["view"] = "summary";
        var model = Body(await Call(EntityEndpointOperationKind.Query, args)).Single();
        model["projected"]!.Value<bool>().Should().BeTrue();
        Named(model["state"], "Green");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Relationship_graph_keeps_enum_names_and_current_field_authority(bool admin)
    {
        var parent = await Seed();
        var row = await Seed(parent.Id);
        var args = Selection(row.Id);
        args["with"] = "all";
        var graph = Body(await Call(EntityEndpointOperationKind.Collection, args, admin)).Single();
        graph["entity"].Should().NotBeNull("collection with=all must return the native relationship graph: {0}", graph);
        Named(graph["entity"]!["state"], "Green");
        var related = ((JObject)graph["parents"]!).Properties().Single().Value;
        related["id"]!.Value<string>().Should().Be(parent.Id);
        Named(related["detail"]!["state"], "Blue");
        if (admin) Named(related["detail"]!["secret"], "Red");
        else related["detail"]!["secret"].Should().BeNull();
        related["ignored"].Should().BeNull();
    }

    [Theory]
    [InlineData("enum-wire-task", false)]
    [InlineData("enum-wire-task", true)]
    [InlineData("enum-wire-value-task", false)]
    [InlineData("enum-wire-value-task", true)]
    public async Task Custom_async_output_keeps_enum_names_and_field_policy(string tool, bool admin)
    {
        var body = Body(await fixture.CallToolAsAsync(tool, null, User(admin)));
        Named(body["state"], "Blue");
        if (admin) Named(body["secret"], "Red");
        else body["secret"].Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutation_payload_and_delta_use_the_same_named_value(bool dryRun)
    {
        var id = "enum-wire-" + Guid.NewGuid().ToString("N");
        var call = await fixture.CallToolAsync(fixture.ResolveToolName("widget", EntityEndpointOperationKind.Upsert),
            new JObject
            {
                ["model"] = new JObject { ["id"] = id, ["title"] = "enum", ["color"] = "Green" },
                ["dry_run"] = dryRun
            });
        Named(Body(call)["color"], "Green");
        var change = call["meta"]!["diagnostics"]!["delta"]!["changes"]!
            .Single(value => value["field"]!.Value<string>() == "color");
        Named(change["to"], "Green");
        (await Widget.Get(id) is not null).Should().Be(!dryRun);
    }

    [Fact]
    public async Task Legacy_numeric_input_remains_accepted_but_output_is_named()
    {
        var body = Body(await fixture.CallToolAsync("enum-wire-echo",
            new JObject { ["value"] = new JObject { ["state"] = 1 } }));
        Named(body["state"], "Green");
        var json = fixture.Services.GetRequiredService<IJsonFacade>();
        json.ToObject<EnumWireSummary>(new JObject { ["state"] = 1 })!.State.Should().Be(WidgetColor.Green);
    }

    [Fact]
    public async Task Undefined_typed_custom_output_fails_instead_of_emitting_a_number()
    {
        var call = await fixture.CallToolAsync("enum-wire-undefined", null);
        McpHarnessFixtureBase.IsError(call).Should().BeTrue();
    }

    [Fact]
    public void CodeMode_named_output_and_undefined_refusal_preserve_conditional_guard()
    {
        var json = fixture.Services.GetRequiredService<IJsonFacade>();
        Named(json.FromObject(new EnumWireSummary { State = WidgetColor.Green })["state"], "Green");
        var undefined = () => json.FromObject(new EnumWireSummary { State = (WidgetColor)99 });
        undefined.Should().Throw<Exception>();
        var governed = () => json.FromObject(new EnumWireDetail());
        governed.Should().Throw<Exception>().Where(error => error.Message.Contains("request-bound serializer"));
    }

    private Task<JToken> Call(EntityEndpointOperationKind operation, JObject args, bool admin = false)
        => fixture.CallToolAsAsync(fixture.ResolveToolName("enum-wire-record", operation), args, User(admin));
    private static System.Security.Claims.ClaimsPrincipal User(bool admin)
        => admin ? McpHarnessFixtureBase.Principal(null, "admin") : new();
    private static JObject Selection(string id) => new() { ["filter"] = new JObject { ["Id"] = id }.ToString() };
    private static JToken Body(JToken call)
    {
        McpHarnessFixtureBase.IsError(call).Should().BeFalse("{0}", call.ToString());
        return JToken.Parse(McpHarnessFixtureBase.ContentText(call)!);
    }
    private static void Named(JToken? value, string expected)
    {
        value.Should().NotBeNull();
        value!.Type.Should().Be(JTokenType.String);
        value.Value<string>().Should().Be(expected);
    }
    private static Task<EnumWireRecord> Seed(string? parent = null)
        => new EnumWireRecord { State = WidgetColor.Green, ParentId = parent }.Save();
}

public sealed class EnumWireFixture : McpHarnessFixtureBase
{
    protected override void ConfigureServices(IServiceCollection services)
        => services.AddSingleton<IEmitHook<EnumWireRecord>, EnumWireSummaryHook>();
}

[McpEntity(Name = "enum-wire-record", Exposure = McpExposureMode.Full)]
[Access(read: Access.Anyone, write: Access.Anyone, remove: Access.Anyone)]
public sealed class EnumWireRecord : Entity<EnumWireRecord>
{
    public WidgetColor State { get; set; }
    public EnumWireDetail Detail { get; set; } = new();
    [McpIgnore(McpFieldDirection.Output)]
    public WidgetColor Ignored { get; set; } = WidgetColor.Red;
    [Parent(typeof(EnumWireRecord))]
    public string? ParentId { get; set; }
}

public sealed class EnumWireDetail
{
    public WidgetColor State { get; set; } = WidgetColor.Blue;
    [Access(read: "is:admin", write: "is:admin")]
    public WidgetColor Secret { get; set; } = WidgetColor.Red;
}

public sealed class EnumWireSummary
{
    public WidgetColor State { get; set; }
    public bool Projected { get; set; }
}

internal sealed class EnumWireSummaryHook : IEmitHook<EnumWireRecord>
{
    public int Order => 0;
    public Task<EmitDecision> OnEmitCollection(HookContext<EnumWireRecord> context, object payload)
        => Task.FromResult(context.Options.View == "summary"
            ? EmitDecision.Project((IEnumerable<EnumWireRecord>)payload,
                row => new EnumWireSummary { State = row.State, Projected = true })
            : EmitDecision.Next());
    public Task<EmitDecision> OnEmitModel(HookContext<EnumWireRecord> context, object payload)
        => Task.FromResult<EmitDecision>(EmitDecision.Next());
}

public static class EnumWireTools
{
    [McpTool(Name = "enum-wire-task")]
    public static Task<EnumWireDetail> Detail() => Task.FromResult(new EnumWireDetail());
    [McpTool(Name = "enum-wire-value-task")]
    public static ValueTask<EnumWireDetail> ValueDetail() => ValueTask.FromResult(new EnumWireDetail());
    [McpTool(Name = "enum-wire-echo")]
    public static EnumWireSummary Echo(EnumWireSummary value) => value;
    [McpTool(Name = "enum-wire-undefined")]
    public static EnumWireSummary Undefined() => new() { State = (WidgetColor)99 };
}
