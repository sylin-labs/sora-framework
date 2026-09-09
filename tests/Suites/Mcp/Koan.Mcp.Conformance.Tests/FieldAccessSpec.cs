using System.Security.Claims;
using Koan.Mcp.CodeMode.Json;
using Koan.Mcp.TestKit;
using Koan.Web.Endpoints;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Koan.Mcp.Conformance.Tests;

/// <summary>Registered MCP dispatch consumes Web field policy without storing caller decisions in schemas or contracts.</summary>
public sealed class FieldAccessSpec(FieldAccessFixture fixture) : IClassFixture<FieldAccessFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_entity_output_applies_conditional_access_and_stronger_McpIgnore(bool admin)
    {
        var record = await Seed();
        var call = await Call(EntityEndpointOperationKind.GetById, new JObject { ["id"] = record.Id }, admin);

        McpHarnessFixtureBase.IsError(call).Should().BeFalse();
        var body = Payload(call);
        body["title"]!.Value<string>().Should().Be(record.Title);
        if (admin)
            body["claim_ids"].Should().BeOfType<JArray>().Subject.Values<string>().Should().Equal(record.Claimants);
        else
        {
            body["claim_ids"].Should().BeNull();
            call.ToString().Should().NotContain(record.Claimants.Single());
        }
        var operatorCode = body["metadata"]?["operator_code"]?.Value<string>();
        operatorCode.Should().Be(admin ? record.Metadata.OperatorCode : null);
        call.ToString().Should().NotContain(record.InternalSecret).And.NotContain(record.WriteOnlyToken);
        (await FieldAccessRecord.Get(record.Id))!.Claimants.Should().Equal(record.Claimants);
    }

    [Theory]
    [InlineData("filter", "Claimants", false)]
    [InlineData("filter", "claim_ids", false)]
    [InlineData("filter", "Metadata.OperatorCode", false)]
    [InlineData("filter", "metadata.operator_code", false)]
    [InlineData("filter", "WriteOnlyToken", true)]
    [InlineData("sort", "Claimants", false)]
    [InlineData("sort", "WriteOnlyToken", true)]
    public async Task Caller_filter_and_sort_cannot_infer_restricted_values(string argument, string field, bool admin)
    {
        var record = await Seed();
        var value = field.Contains("operator", StringComparison.OrdinalIgnoreCase) ? record.Metadata.OperatorCode
            : field == "WriteOnlyToken" ? record.WriteOnlyToken : record.Claimants[0];
        var args = new JObject { [argument] = argument == "sort" ? field : new JObject { [field] = value }.ToString() };
        var call = await Call(EntityEndpointOperationKind.Query, args, admin);

        McpHarnessFixtureBase.IsError(call).Should().BeTrue();
        call.ToString().Should().NotContain(record.Id).And.NotContain(record.InternalSecret);
        call["meta"]?["diagnostics"]?["totalCount"].Should().BeNull();
        call["meta"]?["diagnostics"]?["delta"].Should().BeNull();
    }

    [Fact]
    public async Task Admitted_filters_preserve_public_and_admin_query_results()
    {
        var record = await Seed();
        foreach (var (field, value, admin) in new[]
        {
            ("Title", record.Title, false),
            ("Claimants", record.Claimants[0], true),
            ("Metadata.OperatorCode", record.Metadata.OperatorCode, true)
        })
        {
            var call = await Call(EntityEndpointOperationKind.Query,
                new JObject { ["filter"] = new JObject { [field] = value }.ToString() }, admin);
            McpHarnessFixtureBase.IsError(call).Should().BeFalse("{0}: {1}", field, call.ToString());
            Payload(call).Should().BeOfType<JArray>().Subject.Select(item => item["id"]!.Value<string>()).Should().Equal(record.Id);
            call["meta"]!["diagnostics"]!["totalCount"]!.Value<int>().Should().Be(1);
        }
    }

    [Theory]
    [InlineData("map", false)]
    [InlineData("dict", false)]
    [InlineData("map", true)]
    [InlineData("dict", true)]
    public async Task Built_in_shapes_cannot_copy_an_McpIgnore_display_field(string shape, bool chosenByHook)
    {
        var record = await FieldShapeRecord.Upsert(new FieldShapeRecord
        {
            Name = "private-display-" + Guid.NewGuid().ToString("N"),
            Title = "public-shape-control"
        });
        var principal = User(admin: true);
        var flat = await fixture.CallToolAsAsync(fixture.ResolveToolName("field-shape-record", EntityEndpointOperationKind.GetById),
            new JObject { ["id"] = record.Id }, principal);
        McpHarnessFixtureBase.IsError(flat).Should().BeFalse();
        Payload(flat)["title"]!.Value<string>().Should().Be(record.Title);
        flat.ToString().Should().NotContain(record.Name);

        var arguments = new JObject { ["filter"] = new JObject { ["Id"] = record.Id }.ToString() };
        if (chosenByHook) arguments["extras"] = new JObject { ["testDefaultShape"] = shape };
        else arguments["shape"] = shape;
        var shaped = await fixture.CallToolAsAsync(fixture.ResolveToolName("field-shape-record", EntityEndpointOperationKind.Collection),
            arguments, principal);
        McpHarnessFixtureBase.IsShortCircuited(shaped).Should().BeTrue();
        shaped["meta"]!["diagnostics"]!["shortCircuitStatusCode"]!.Value<int>().Should().Be(403);
        shaped.ToString().Should().NotContain(record.Name).And.NotContain(record.Id);
        shaped["meta"]?["diagnostics"]?["totalCount"].Should().BeNull();
    }

    [Fact]
    public async Task Relationship_collection_keeps_typed_root_and_parent_field_policy()
    {
        var parent = await Seed();
        var record = await FieldShapeRecord.Upsert(new FieldShapeRecord
        {
            ParentId = parent.Id, Name = "private-related-display", Title = "public-related-control"
        });
        foreach (var admin in new[] { false, true })
        {
            var call = await fixture.CallToolAsAsync(fixture.ResolveToolName("field-shape-record", EntityEndpointOperationKind.Collection),
                new JObject { ["filter"] = new JObject { ["Id"] = record.Id }.ToString(), ["with"] = "all" }, User(admin));
            McpHarnessFixtureBase.IsError(call).Should().BeFalse("{0}", call.ToString());
            McpHarnessFixtureBase.IsShortCircuited(call).Should().BeFalse("{0}", call.ToString());
            var graph = Payload(call).Should().BeOfType<JArray>().Subject.Single();
            graph["entity"]!["id"]!.Value<string>().Should().Be(record.Id);
            graph["entity"]!["title"]!.Value<string>().Should().Be(record.Title);
            var related = graph["parents"].Should().BeOfType<JObject>().Subject.Properties().Single().Value;
            related["id"]!.Value<string>().Should().Be(parent.Id);
            related["title"]!.Value<string>().Should().Be(parent.Title);
            if (admin)
                related["claim_ids"].Should().BeOfType<JArray>().Subject.Values<string>().Should().Equal(parent.Claimants);
            else
                related["claim_ids"].Should().BeNull();
            var operatorCode = related["metadata"]?["operator_code"]?.Value<string>();
            operatorCode.Should().Be(admin ? parent.Metadata.OperatorCode : null);
            call.ToString().Should().NotContain(record.Name).And.NotContain(parent.InternalSecret).And.NotContain(parent.WriteOnlyToken);
        }
    }

    [Fact]
    public async Task Delete_query_cannot_infer_an_McpIgnore_output_field()
    {
        var record = await Seed();
        var call = await Call(EntityEndpointOperationKind.DeleteByQuery,
            new JObject { ["query"] = new JObject { ["WriteOnlyToken"] = record.WriteOnlyToken }.ToString() }, admin: true);

        McpHarnessFixtureBase.IsError(call).Should().BeTrue();
        call["meta"]?["diagnostics"]?["delta"].Should().BeNull();
        (await FieldAccessRecord.Get(record.Id)).Should().NotBeNull();
    }

    [Theory]
    [InlineData("replace", "/claim_ids", null, false)]
    [InlineData("test", "/metadata/operator_code", null, false)]
    [InlineData("copy", "/title", "/claim_ids", false)]
    [InlineData("move", "/title", "/writeOnlyToken", true)]
    [InlineData("replace", "/internalSecret", null, true)]
    [InlineData("replace", "/metadata", null, false)]
    public async Task Patch_checks_destinations_sources_and_protected_descendants(string op, string path, string? from, bool admin)
    {
        var record = await Seed();
        var operation = new JObject { ["op"] = op, ["path"] = path, ["value"] = "probe" };
        if (from is not null) operation["from"] = from;
        var call = await Call(EntityEndpointOperationKind.Patch,
            new JObject { ["id"] = record.Id, ["patch"] = new JArray(operation) }, admin);

        McpHarnessFixtureBase.IsError(call).Should().BeTrue();
        call["meta"]?["diagnostics"]?["delta"].Should().BeNull();
        var stored = await FieldAccessRecord.Get(record.Id);
        stored!.Title.Should().Be(record.Title);
        stored.InternalSecret.Should().Be(record.InternalSecret);
        stored.Claimants.Should().Equal(record.Claimants);
        stored.Metadata.OperatorCode.Should().Be(record.Metadata.OperatorCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replacement_refuses_omitted_input_protected_fields_even_for_admin(bool batch)
    {
        var record = await Seed();
        var model = new JObject { ["id"] = record.Id, ["title"] = "replacement" };
        var args = batch ? new JObject { ["models"] = new JArray(model) } : new JObject { ["model"] = model };
        var call = await Call(batch ? EntityEndpointOperationKind.UpsertMany : EntityEndpointOperationKind.Upsert, args, admin: true);

        McpHarnessFixtureBase.IsError(call).Should().BeTrue();
        call["meta"]?["diagnostics"]?["delta"].Should().BeNull();
        var stored = await FieldAccessRecord.Get(record.Id);
        stored!.Title.Should().Be(record.Title);
        stored.InternalSecret.Should().Be(record.InternalSecret);
    }

    [Fact]
    public async Task Writable_but_unreadable_patch_value_is_absent_from_payload_and_delta()
    {
        var record = await Seed();
        const string next = "next-write-only-token";
        var call = await Call(EntityEndpointOperationKind.Patch, new JObject
        {
            ["id"] = record.Id,
            ["patch"] = new JArray(new JObject { ["op"] = "replace", ["path"] = "/writeOnlyToken", ["value"] = next })
        });

        McpHarnessFixtureBase.IsError(call).Should().BeFalse();
        McpHarnessFixtureBase.IsShortCircuited(call).Should().BeFalse();
        (await FieldAccessRecord.Get(record.Id))!.WriteOnlyToken.Should().Be(next);
        call.ToString().Should().NotContain(next).And.NotContain(record.WriteOnlyToken).And.NotContain("writeOnlyToken");
        var delta = call["meta"]?["diagnostics"]?["delta"];
        delta.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_custom_output_and_input_use_the_same_field_policy(bool admin)
    {
        var principal = User(admin);
        foreach (var name in new[] { "field-access-detail", "field-access-value-detail" })
        {
            var output = await fixture.CallToolAsAsync(name, new JObject(), principal);
            McpHarnessFixtureBase.IsError(output).Should().BeFalse("{0}: {1}", name, output.ToString());
            var operatorCode = Payload(output)["operator_code"]?.Value<string>();
            operatorCode.Should().Be(admin ? "custom-operator-secret" : null);
        }
        var callsBefore = FieldAccessTools.InputCalls;
        var input = await fixture.CallToolAsAsync("field-access-input", new JObject
        {
            ["detail"] = new JObject { ["label"] = "public-input", ["operator_code"] = "caller-value" }
        }, principal);

        McpHarnessFixtureBase.IsError(input).Should().Be(!admin);
        FieldAccessTools.InputCalls.Should().Be(callsBefore + (admin ? 1 : 0), "policy denial must not invoke the tool with a default argument");
    }

    [Fact]
    public async Task A_server_changed_private_descendant_does_not_create_a_visible_parent_delta()
    {
        var record = await Seed();
        var call = await Call(EntityEndpointOperationKind.Patch, new JObject
        {
            ["id"] = record.Id,
            ["patch"] = new JArray(new JObject { ["op"] = "replace", ["path"] = "/title", ["value"] = "rotate-server-metadata" })
        });

        McpHarnessFixtureBase.IsError(call).Should().BeFalse();
        McpHarnessFixtureBase.IsShortCircuited(call).Should().BeFalse();
        (await FieldAccessRecord.Get(record.Id))!.Metadata.OperatorCode.Should().Be("rotated-private-code");
        var changesToken = call["meta"]?["diagnostics"]?["delta"]?["changes"];
        var changes = changesToken.Should().BeOfType<JArray>().Subject;
        changes.Select(change => change["field"]!.Value<string>()).Should().Equal("title");
        call.ToString().Should().NotContain("rotated-private-code").And.NotContain(record.Metadata.OperatorCode);
    }

    [Fact]
    public async Task Unprepared_polymorphic_output_refuses_without_a_partial_value()
    {
        var call = await fixture.CallToolAsAsync("field-access-polymorphic", new JObject(), User(admin: true));
        McpHarnessFixtureBase.IsError(call).Should().BeTrue();
        McpHarnessFixtureBase.ContentText(call).Should().Contain("was not prepared");
        call.ToString().Should().NotContain("unprepared-secret");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Constructor_bound_custom_input_refuses_protected_members_even_when_omitted(bool includeProtected)
    {
        var detail = new JObject { ["label"] = "public-input" };
        if (includeProtected) detail["operator_code"] = "caller-constructor-secret";
        var arguments = new JObject { ["detail"] = detail };
        var callsBefore = FieldAccessTools.InputCalls;

        var denied = await fixture.CallToolAsAsync("field-access-constructor-input", arguments, User(admin: false));
        McpHarnessFixtureBase.IsError(denied).Should().BeTrue();
        McpHarnessFixtureBase.ContentText(denied).Should().Contain("replace");
        denied.ToString().Should().NotContain("caller-constructor-secret");
        FieldAccessTools.InputCalls.Should().Be(callsBefore);

        var admitted = await fixture.CallToolAsAsync("field-access-constructor-input", arguments, User(admin: true));
        McpHarnessFixtureBase.IsError(admitted).Should().BeFalse("{0}", admitted.ToString());
        McpHarnessFixtureBase.ContentText(admitted).Should().Be(includeProtected ? "caller-constructor-secret" : "public-input");
        FieldAccessTools.InputCalls.Should().Be(callsBefore + 1);
    }

    [Fact]
    public async Task McpIgnore_on_a_CLR_field_prevents_omitted_or_explicit_custom_input_overwrite()
    {
        var callsBefore = FieldAccessTools.InputCalls;
        foreach (var includeProtected in new[] { false, true })
        {
            var detail = new JObject { ["label"] = "public-input" };
            if (includeProtected) detail["secret"] = "caller-private-field";
            var call = await fixture.CallToolAsAsync("field-access-field-input", new JObject { ["detail"] = detail }, User(admin: true));
            McpHarnessFixtureBase.IsError(call).Should().BeTrue();
            McpHarnessFixtureBase.ContentText(call).Should().Contain("replace");
            call.ToString().Should().NotContain("caller-private-field").And.NotContain("default-private-field");
            FieldAccessTools.InputCalls.Should().Be(callsBefore);
        }
    }

    [Fact]
    public async Task Concurrent_readers_do_not_share_field_decisions_or_mutate_static_schema()
    {
        var record = await Seed();
        var schema = fixture.GetToolInputSchema(Tool(EntityEndpointOperationKind.Upsert));
        var before = schema.ToString();
        var properties = (JObject)schema["properties"]!["model"]!["properties"]!;
        properties.ContainsKey("claim_ids").Should().BeTrue("conditional schema is a caller-neutral superset");
        properties.ContainsKey("internalSecret").Should().BeFalse();
        var calls = await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
            (Admin: index % 2 == 0, Result: await Call(EntityEndpointOperationKind.GetById,
                new JObject { ["id"] = record.Id }, admin: index % 2 == 0))));

        foreach (var (admin, call) in calls)
        {
            if (admin)
                Payload(call)["claim_ids"].Should().BeOfType<JArray>().Subject.Values<string>().Should().Equal(record.Claimants);
            else
                Payload(call)["claim_ids"].Should().BeNull();
        }
        schema.ToString().Should().Be(before);
    }

    [Fact]
    public void Context_free_CodeMode_conversion_refuses_governed_CLR_but_accepts_projected_JSON()
    {
        var json = fixture.Services.GetRequiredService<IJsonFacade>();
        var governed = () => json.FromObject(new FieldAccessRecord.Detail { OperatorCode = "unbound-secret" });
        governed.Should().Throw<Exception>().Where(error => error.Message.Contains("request-bound serializer"));
        var projected = new JObject { ["label"] = "ordinary-json" };
        JToken.DeepEquals(json.FromObject(projected), projected).Should().BeTrue();
    }

    [Fact]
    public void Context_free_converter_cannot_bypass_a_declared_governed_descendant()
    {
        var json = fixture.Services.GetRequiredService<IJsonFacade>();
        var callsBefore = FieldAccessTools.DetailContainerConverter.Calls;
        var container = new FieldAccessTools.DetailContainer { Value = new() { OperatorCode = "converter-secret" } };

        var write = () => json.FromObject(container);
        write.Should().Throw<Exception>().Where(error => error.Message.Contains("request-bound serializer"));
        var read = () => json.ToObject<FieldAccessTools.DetailContainer>(new JValue("caller-secret"));
        read.Should().Throw<Exception>().Where(error => error.Message.Contains("request-bound serializer"));
        FieldAccessTools.DetailContainerConverter.Calls.Should().Be(callsBefore);
    }

    private string Tool(EntityEndpointOperationKind kind) => fixture.ResolveToolName("field-record", kind);
    private Task<JToken> Call(EntityEndpointOperationKind kind, JObject args, bool admin = false)
        => fixture.CallToolAsAsync(Tool(kind), args, User(admin));
    private static ClaimsPrincipal User(bool admin) => admin ? McpHarnessFixtureBase.Principal(null, "admin") : new ClaimsPrincipal();
    private static JToken Payload(JToken call) => JToken.Parse(McpHarnessFixtureBase.ContentText(call)!);
    private static Task<FieldAccessRecord> Seed()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return FieldAccessRecord.Upsert(new FieldAccessRecord
        {
            Title = "public-" + suffix, Claimants = ["claimant-" + suffix], InternalSecret = "internal-" + suffix,
            WriteOnlyToken = "write-only-" + suffix,
            Metadata = new() { Label = "public-label", OperatorCode = "operator-" + suffix }
        });
    }
}
