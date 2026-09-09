using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Koan.Core;
using Koan.Core.Hosting.App;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Core.Relationships;
using Koan.Mcp;
using Koan.Mcp.Hosting;
using Koan.Mcp.Options;
using Koan.Web.Authorization;
using Koan.Web.Controllers;
using Koan.Web.Context;
using Koan.Web.Endpoints;
using Koan.Web.Extensions;
using Koan.Web.Hooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Koan.Data.Connector.Mongo.Tests.Specs;

/// <summary>Real AddKoan REST and registered MCP dispatch over the native counterpart provider.</summary>
public sealed class CounterpartEndpointSpec(MongoFixture mongo)
{
    [Fact]
    public async Task Rest_and_mcp_share_native_collection_keyed_and_manifest_authority()
    {
        var state = new CounterpartEndpointState();
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "visible", true, "Translated title");
        await Seed(state, "hidden", false, "Hidden title");
        using (EntityContext.Partition(state.Content))
            await new CounterpartPage { Id = "orphan", Title = "Orphan title", Published = true }.Save();

        var client = host.GetTestClient();
        var response = await client.GetAsync($"/counterpart-pages?set={state.Content}&access=true&shape=full");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        body["items"]!.Select(row => row["id"]!.Value<string>()).Should().Equal("visible");
        body["access"]!["visible"]!["can"]!.Values<string>().Should().Contain("read");
        response.Headers.GetValues("X-Total-Count").Should().Equal("1");
        (await client.GetAsync($"/counterpart-pages/hidden?set={state.Content}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/counterpart-pages/orphan?set={state.Content}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var keyed = await client.GetAsync($"/counterpart-pages/visible?set={state.Content}");
        keyed.StatusCode.Should().Be(HttpStatusCode.OK);
        keyed.Headers.GetValues("Koan-Access").Single().Should().Contain("read");

        var collection = await Call(host.Services, EntityEndpointOperationKind.Collection,
            new JObject { ["set"] = state.Content });
        collection["isError"]?.Value<bool>().Should().NotBe(true);
        var rows = JArray.Parse(collection["content"]![0]!["text"]!.Value<string>()!);
        rows.Select(row => row["id"]!.Value<string>()).Should().Equal("visible");
        collection.ToString().Should().Contain("read").And.NotContain("Hidden title").And.NotContain("Orphan title");
        var missing = await Call(host.Services, EntityEndpointOperationKind.GetById,
            new JObject { ["id"] = "hidden", ["set"] = state.Content });
        missing.ToString().Should().NotContain("Hidden title");
        missing["meta"]?["shortCircuit"].Should().NotBeNull();
        await host.StopAsync();
    }

    [Theory]
    [InlineData("add")]
    [InlineData("replace")]
    [InlineData("id")]
    [InlineData("filter")]
    [InlineData("principal")]
    [InlineData("fallback")]
    [InlineData("erased-fallback")]
    [InlineData("emit")]
    [InlineData("options-fallback")]
    [InlineData("shape")]
    [InlineData("relationships")]
    public async Task Collection_hook_cannot_reuse_evidence_for_changed_rows_or_scope(string change)
    {
        var state = new CounterpartEndpointState { Change = change };
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "visible", true, "Visible");
        using var scope = host.Services.CreateScope();
        var context = Context(scope.ServiceProvider);
        context.Items[AccessProjection.RequestKey] = true;
        var endpoint = scope.ServiceProvider.GetRequiredService<IEntityEndpointService<CounterpartPage, string>>();
        var result = await endpoint.Query(new() { Context = context, Set = state.Content });
        result.ShortCircuitResult.Should().BeOfType<BadRequestObjectResult>();
        result.Payload.Should().BeNull();
        result.Items.Should().BeEmpty();
        context.Items.Should().NotContainKey(AccessProjection.ManifestKey);
        var collection = await endpoint.GetCollection(new() { Context = Context(scope.ServiceProvider), Set = state.Content,
            Policy = Koan.Web.Attributes.PaginationPolicy.Resolve(scope.ServiceProvider, null) });
        collection.ShortCircuitResult.Should().BeOfType<BadRequestObjectResult>();
        collection.Payload.Should().BeNull();
        collection.Items.Should().BeEmpty();
        await host.StopAsync();
    }

    [Theory]
    [InlineData("model-id")]
    [InlineData("model-fallback")]
    [InlineData("model-erased-fallback")]
    [InlineData("model-emit")]
    [InlineData("options-fallback")]
    public async Task Keyed_hook_cannot_supply_an_unproved_identity_or_success_payload(string change)
    {
        var state = new CounterpartEndpointState { Change = change };
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "visible", true, "Visible");
        using var scope = host.Services.CreateScope();
        var endpoint = scope.ServiceProvider.GetRequiredService<IEntityEndpointService<CounterpartPage, string>>();
        var result = await endpoint.GetById(new() { Context = Context(scope.ServiceProvider), Id = "visible", Set = state.Content });
        result.ShortCircuitResult.Should().BeOfType<BadRequestObjectResult>();
        result.Payload.Should().BeNull();
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Default_outer_partition_preserves_rest_and_mcp_evidence(bool defaultAuthority)
    {
        var state = new CounterpartEndpointState { Content = "", Canonical = defaultAuthority ? "" : "default-authority-" + Guid.NewGuid().ToString("N") };
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        var id = "default-visible-" + Guid.NewGuid().ToString("N");
        try
        {
            await Seed(state, id, true, "Default content");
            var client = host.GetTestClient();
            var collection = await client.GetAsync("/counterpart-pages?access=true");
            collection.StatusCode.Should().Be(HttpStatusCode.OK);
            JObject.Parse(await collection.Content.ReadAsStringAsync())["items"]!
                .Select(row => row["id"]!.Value<string>()).Should().Contain(id);
            (await client.GetAsync("/counterpart-pages/" + id)).StatusCode.Should().Be(HttpStatusCode.OK);
            var mcpCollection = await Call(host.Services, EntityEndpointOperationKind.Collection, new JObject());
            mcpCollection["meta"]!["shortCircuit"]!.Type.Should().Be(JTokenType.Null);
            mcpCollection.ToString().Should().Contain(id);
            var mcpKeyed = await Call(host.Services, EntityEndpointOperationKind.GetById, new JObject { ["id"] = id });
            mcpKeyed["meta"]!["shortCircuit"]!.Type.Should().Be(JTokenType.Null);
            mcpKeyed.ToString().Should().Contain(id);
        }
        finally
        {
            using (EntityContext.Partition(state.Content)) await CounterpartPage.Remove(id);
            using (EntityContext.Partition(state.Canonical)) await CounterpartPage.Remove(id);
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Hook_denials_preserve_status_without_returning_an_unproved_error_body()
    {
        var state = new CounterpartEndpointState { Change = "denial" };
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "visible", true, "Visible");
        var client = host.GetTestClient();
        foreach (var path in new[] { "/counterpart-pages", "/counterpart-pages/visible" })
        {
            var response = await client.GetAsync(path + "?set=" + state.Content);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("unproved");
        }
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Typed_summary_projects_exact_native_page_once_for_rest_and_mcp(bool authenticated)
    {
        var state = new CounterpartEndpointState { Change = "project" };
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "a", true, "Translated B", canonicalTitle: "Canonical A");
        await Seed(state, "b", true, "Translated A", canonicalTitle: "Canonical B");
        await Seed(state, "hidden", false, "Hidden title");
        var client = host.GetTestClient();
        if (authenticated) client.DefaultRequestHeaders.Add("X-Test-Reader", "reader");
        var response = await client.GetAsync($"/counterpart-pages?set={state.Content}&access=true&shape=full&sort=Title&page=1&pageSize=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var rest = JObject.Parse(await response.Content.ReadAsStringAsync());
        rest["items"]!.Select(row => row["id"]!.Value<string>()).Should().Equal("b");
        rest["items"]!.Select(row => row["label"]!.Value<string>()).Should().Equal("Translated A");
        rest["items"]!.Select(row => row["personalized"]!.Value<bool>()).Should().OnlyContain(value => value == authenticated);
        rest["access"]!.Children<JProperty>().Select(item => item.Name).Should().Equal("b");
        response.Headers.GetValues("X-Total-Count").Should().Equal("2");
        state.MapCalls.Should().Be(1);
        state.TailCalls.Should().Be(0, "source-bound projection is terminal before any mapper output exists");

        var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticated ? [new Claim(ClaimTypes.NameIdentifier, "reader")] : [],
            authenticated ? "test" : null));
        state.MapCalls = 0;
        var mcp = await Call(host.Services, EntityEndpointOperationKind.Query,
            new JObject { ["set"] = state.Content, ["filter"] = new JObject(), ["sort"] = "Title", ["page"] = 1, ["pageSize"] = 1 }, principal);
        mcp["meta"]!["shortCircuit"]!.Type.Should().Be(JTokenType.Null);
        var views = JArray.Parse(mcp["content"]![0]!["text"]!.Value<string>()!);
        views.Select(view => view["id"]!.Value<string>()).Should().Equal("b");
        views.Select(view => view["label"]!.Value<string>()).Should().Equal("Translated A");
        mcp["meta"]!["headers"]!["X-Total-Count"]!.Value<string>().Should().Be("2");
        mcp["meta"]!["diagnostics"]!["access"]!.Children<JProperty>().Select(item => item.Name).Should().Equal("b");
        views.Select(view => view["personalized"]!.Value<bool>()).Should().OnlyContain(value => value == authenticated);
        mcp.ToString().Should().NotContain("Hidden title");
        state.MapCalls.Should().Be(1);
        state.TailCalls.Should().Be(0);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("project-foreign", 0)]
    [InlineData("project-reorder", 0)]
    [InlineData("project-subset", 0)]
    [InlineData("project-id", 2)]
    [InlineData("project-principal", 2)]
    [InlineData("project-filter", 2)]
    [InlineData("project-throws", 1)]
    public async Task Projection_rejects_changed_sources_or_mapper_failure_without_partial_payload(string change, int maps)
    {
        var state = new CounterpartEndpointState { Change = change };
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "a", true, "A");
        await Seed(state, "b", true, "B");
        using var scope = host.Services.CreateScope();
        var context = Context(scope.ServiceProvider);
        context.Items[AccessProjection.RequestKey] = true;
        var endpoint = scope.ServiceProvider.GetRequiredService<IEntityEndpointService<CounterpartPage, string>>();
        var response = await endpoint.Query(new() { Context = context, Set = state.Content });
        if (change == "project-throws")
            response.ShortCircuitResult.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
        else response.ShortCircuitResult.Should().BeOfType<BadRequestObjectResult>();
        response.Payload.Should().BeNull();
        response.Items.Should().BeEmpty();
        context.Items.Should().NotContainKey(AccessProjection.ManifestKey);
        state.MapCalls.Should().Be(maps);
        state.TailCalls.Should().Be(0);
        if (change == "project-throws")
        {
            var rest = await host.GetTestClient().GetAsync("/counterpart-pages?set=" + state.Content);
            rest.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            (await rest.Content.ReadAsStringAsync()).Should().NotContain("mapper exception data");
            var mcp = await Call(host.Services, EntityEndpointOperationKind.Query, new JObject { ["set"] = state.Content });
            mcp["meta"]!["shortCircuit"]!["statusCode"]!.Value<int>().Should().Be(500);
            JArray.Parse(mcp["content"]![0]!["text"]!.Value<string>()!).Should().BeEmpty();
            mcp["meta"]!["diagnostics"]!["access"].Should().BeNull();
        }
        await host.StopAsync();
    }

    [Fact]
    public async Task Project_owns_the_callers_source_sequence_at_contribution_time()
    {
        var state = new CounterpartEndpointState { Change = "project-owned-sources" };
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "a", true, "A");
        var response = await host.GetTestClient().GetAsync("/counterpart-pages?set=" + state.Content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JArray.Parse(await response.Content.ReadAsStringAsync()).Single()["label"]!.Value<string>().Should().Be("A");
        state.MapCalls.Should().Be(1);
        await host.StopAsync();
    }

    [Fact]
    public async Task After_fetch_reordering_cannot_replace_the_selected_native_sort_order_before_projection()
    {
        var state = new CounterpartEndpointState();
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "b", true, "B");
        await Seed(state, "a", true, "A");
        var client = host.GetTestClient();
        var route = $"/counterpart-pages?set={state.Content}&sort=Title";
        var before = await client.GetAsync(route);
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        JArray.Parse(await before.Content.ReadAsStringAsync()).Select(row => row["id"]!.Value<string>()).Should().Equal("a", "b");
        state.Change = "project-after-reorder";
        var rejected = await client.GetAsync(route);
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        state.MapCalls.Should().Be(0);
        await host.StopAsync();
    }

    [Theory]
    [InlineData("shape=map")]
    [InlineData("shape=dict")]
    [InlineData("with=all")]
    public async Task Framework_shapes_follow_emit_and_projection_shape_conflicts_refuse_before_mapping(string shape)
    {
        var state = new CounterpartEndpointState();
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "parent", true, "Parent");
        await Seed(state, "child", true, "Child", parentId: "parent");
        var client = host.GetTestClient();
        var response = await client.GetAsync($"/counterpart-pages?set={state.Content}&{shape}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        state.TailSawEntityRows.Should().BeTrue("framework wrappers are constructed only after all custom emit hooks");
        state.Change = "project";
        var rejected = await client.GetAsync($"/counterpart-pages?set={state.Content}&{shape}");
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("flat collections");
        state.MapCalls.Should().Be(0);
        state.Change = "project-unless-shaped";
        var normalized = await client.GetAsync($"/counterpart-pages?set={state.Content}&{shape}");
        normalized.StatusCode.Should().Be(HttpStatusCode.OK);
        state.MapCalls.Should().Be(0, "hooks can select Next using normalized options without inspecting HTTP");
        await host.StopAsync();
    }

    [Fact]
    public async Task Later_hook_cannot_change_captured_owner_or_allowed_ids_or_rerun_read_declaration()
    {
        var state = new CounterpartEndpointState { Capture = true, Owner = "alice", Allowed = ["allowed"] };
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "allowed", false, "Allowed", "other");
        await Seed(state, "owned", false, "Owned", "alice");
        await Seed(state, "later", false, "Later", "bob");
        state.ReadDeclarations = 0;
        var response = await host.GetTestClient().GetAsync($"/counterpart-pages?set={state.Content}&access=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JObject.Parse(await response.Content.ReadAsStringAsync());
        body["items"]!.Select(row => row["id"]!.Value<string>()).Should().BeEquivalentTo("allowed", "owned");
        body["access"]!.Children<JProperty>().SelectMany(item => item.Value["can"]!.Values<string>())
            .Should().OnlyContain(verb => verb == "read");
        state.Owner.Should().Be("bob");
        state.Allowed.Should().Equal("later");
        state.ReadDeclarations.Should().Be(1, "projection must use the executed constraint rather than rebuilding it");
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_endpoint_callers_use_normalized_relationship_options_without_a_wire_token(bool keyed)
    {
        var state = new CounterpartEndpointState { Change = "project-unless-shaped" };
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "parent", true, "Parent");
        await Seed(state, "child", true, "Child", parentId: "parent");
        using var scope = host.Services.CreateScope();
        var context = Context(scope.ServiceProvider);
        context.Options.IncludeRelationships = true;
        var endpoint = scope.ServiceProvider.GetRequiredService<IEntityEndpointService<CounterpartPage, string>>();
        if (keyed)
        {
            var result = await endpoint.GetById(new() { Context = context, Set = state.Content, Id = "parent" });
            result.ShortCircuitResult.Should().BeNull();
            result.Payload.Should().BeOfType<RelationshipGraph<CounterpartPage>>();
        }
        else
        {
            var result = await endpoint.GetCollection(new() { Context = context, Set = state.Content,
                Policy = Koan.Web.Attributes.PaginationPolicy.Resolve(scope.ServiceProvider, null) });
            result.ShortCircuitResult.Should().BeNull();
            result.Payload.Should().BeAssignableTo<IReadOnlyList<object>>().Which
                .Should().OnlyContain(row => row is RelationshipGraph<CounterpartPage>);
        }
        state.MapCalls.Should().Be(0);
        await host.StopAsync();
    }

    [Fact]
    public async Task Parent_and_child_expansion_apply_the_related_type_full_native_constraint()
    {
        var state = new CounterpartEndpointState();
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "parent", true, "Visible parent");
        await Seed(state, "child", true, "Visible child", parentId: "parent");
        await Seed(state, "hidden-child", false, "Hidden child", parentId: "parent");
        await Seed(state, "hidden-parent", false, "Hidden parent");
        await Seed(state, "another-child", true, "Another child", parentId: "hidden-parent");
        var client = host.GetTestClient();
        var parent = await client.GetAsync($"/counterpart-pages/parent?set={state.Content}&with=all");
        parent.StatusCode.Should().Be(HttpStatusCode.OK);
        (await parent.Content.ReadAsStringAsync()).Should().Contain("Visible child").And.NotContain("Hidden child");
        var child = await client.GetAsync($"/counterpart-pages/child?set={state.Content}&with=all");
        (await child.Content.ReadAsStringAsync()).Should().Contain("Visible parent");
        var hiddenParent = await client.GetAsync($"/counterpart-pages/another-child?set={state.Content}&with=all");
        (await hiddenParent.Content.ReadAsStringAsync()).Should().NotContain("Hidden parent");
        await host.StopAsync();
    }

    [Theory]
    [InlineData("upsert")]
    [InlineData("bulk")]
    [InlineData("delete")]
    [InlineData("delete-many")]
    [InlineData("delete-query")]
    [InlineData("delete-all")]
    [InlineData("patch")]
    public async Task Counterpart_mutation_declarations_refuse_before_hooks_and_storage(string operation)
    {
        var state = new CounterpartEndpointState();
        using var host = await Start(state);
        using var app = AppHost.PushScope(host.Services);
        await Seed(state, "visible", true, "Original");
        state.Mutation = true;
        state.MutationHooks = 0;
        using var scope = host.Services.CreateScope();
        var endpoint = scope.ServiceProvider.GetRequiredService<IEntityEndpointService<CounterpartPage, string>>();
        var context = Context(scope.ServiceProvider, admin: true);
        var changed = new CounterpartPage { Id = "visible", Title = "Changed" };
        Func<Task> action = operation switch
        {
            "upsert" => async () => { await endpoint.Upsert(new() { Context = context, Model = changed, Set = state.Content }); },
            "bulk" => async () => { await endpoint.UpsertMany(new() { Context = context, Models = [changed], Set = state.Content }); },
            "delete" => async () => { await endpoint.Delete(new() { Context = context, Id = "visible", Set = state.Content }); },
            "delete-many" => async () => { await endpoint.DeleteMany(new() { Context = context, Ids = ["visible"], Set = state.Content }); },
            "delete-query" => async () => { await endpoint.DeleteByQuery(new() { Context = context, Query = "{}", Set = state.Content }); },
            "delete-all" => async () => { await endpoint.DeleteAll(new() { Context = context, Set = state.Content }); },
            _ => async () => { await endpoint.Patch(new() { Context = context, Id = "visible", Patch = JObject.Parse("{\"title\":\"Changed\"}"), Set = state.Content }); }
        };
        await action.Should().ThrowAsync<NotSupportedException>().WithMessage("*counterpart*");
        state.MutationHooks.Should().Be(0);
        using (EntityContext.Partition(state.Content))
            (await CounterpartPage.Get("visible"))!.Title.Should().Be("Original");
        await host.StopAsync();
    }

    private async Task<IHost> Start(CounterpartEndpointState state)
    {
        var host = Host.CreateDefaultBuilder().ConfigureAppConfiguration(config => config.AddInMemoryCollection(mongo.SettingsForBoot()))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.UseEnvironment("Test");
                web.ConfigureServices(services =>
                {
                    services.AddKoan();
                    services.AddKoanControllersFrom<CounterpartPagesController>();
                    services.AddSingleton(state);
                    services.AddSingleton<ICollectionHook<CounterpartPage>, CounterpartPageHook>();
                    services.AddSingleton<IModelHook<CounterpartPage>, CounterpartPageHook>();
                    services.AddSingleton<IRequestOptionsHook<CounterpartPage>, CounterpartPageHook>();
                    services.AddSingleton<IEmitHook<CounterpartPage>, CounterpartPageHook>();
                    services.AddSingleton<IEmitHook<CounterpartPage>, CounterpartTailHook>();
                    services.AddScoped<IWebContextContributor, CounterpartPrincipalContributor>();
                    var stdio = services.FirstOrDefault(item => item.ServiceType == typeof(IHostedService)
                        && item.ImplementationType == typeof(StdioTransport));
                    if (stdio is not null) services.Remove(stdio);
                    services.Configure<McpServerOptions>(options =>
                    {
                        options.EnableStdioTransport = false;
                        options.Exposure = McpExposureMode.Tools;
                        options.RequireAuthentication = false;
                    });
                });
                web.Configure(_ => { });
            }).Build();
        await host.StartAsync();
        return host;
    }

    private static EntityRequestContext Context(IServiceProvider services, bool admin = false)
        => new(services, new QueryOptions(), default, user: new ClaimsPrincipal(new ClaimsIdentity(
            admin ? [new Claim(ClaimTypes.Role, "admin")] : [], "Test")));

    private static async Task Seed(CounterpartEndpointState state, string id, bool published,
        string title, string owner = "", string? parentId = null, string? canonicalTitle = null)
    {
        using (EntityContext.Partition(state.Canonical))
            await new CounterpartPage { Id = id, Published = published, Owner = owner, Title = canonicalTitle ?? "" }.Save();
        using (EntityContext.Partition(state.Content))
            await new CounterpartPage { Id = id, Published = state.Content == state.Canonical ? published : !published, Title = title, Owner = "replica", ParentId = parentId }.Save();
    }

    private static async Task<JToken> Call(IServiceProvider services, EntityEndpointOperationKind operation, JObject arguments, ClaimsPrincipal? principal = null)
    {
        using var scope = services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<McpEntityRegistry>();
        var registration = registry.Registrations.Single(item => item.DisplayName == "counterpart-page");
        var tool = registration.Tools.Single(item => item.Operation == operation);
        var handler = scope.ServiceProvider.GetRequiredService<McpServer>().CreateHandler();
        var result = await handler.CallToolFor(new McpRpcHandler.ToolsCallParams { Name = tool.Name, Arguments = arguments },
            principal ?? new ClaimsPrincipal(new ClaimsIdentity()), default);
        return JToken.Parse(JsonConvert.SerializeObject(result));
    }
}

[Access(read: Access.Anyone, write: "is:admin", remove: "is:admin")]
[McpEntity(Name = "counterpart-page")]
public sealed class CounterpartPage : Entity<CounterpartPage>
{
    public string Title { get; set; } = "";
    public bool Published { get; set; }
    public string Owner { get; set; } = "";
    [Parent(typeof(CounterpartPage))]
    public string? ParentId { get; set; }
}

[Route("counterpart-pages")]
public sealed class CounterpartPagesController : EntityController<CounterpartPage>;

public sealed class CounterpartEndpointState
{
    public string Canonical { get; init; } = "authority-" + Guid.NewGuid().ToString("N");
    public string Content { get; init; } = "content-" + Guid.NewGuid().ToString("N");
    public string? Change { get; set; }
    public bool Capture { get; set; }
    public string Owner { get; set; } = "";
    public List<string> Allowed { get; set; } = [];
    public int ReadDeclarations { get; set; }
    public bool Mutation { get; set; }
    public int MutationHooks { get; set; }
    public int MapCalls { get; set; }
    public int TailCalls { get; set; }
    public bool TailSawEntityRows { get; set; }
}

public sealed class CounterpartPageAccess : EntityAccess<CounterpartPage>
{
    public override IAccessFilter<CounterpartPage> Constrain(IAccessFilter<CounterpartPage> query, AccessAction action)
    {
        var state = Services?.GetService<CounterpartEndpointState>();
        if (state is null) return query;
        if (action != AccessAction.Read)
            return state.Mutation ? query.Where(page => page.Published, state.Canonical) : query;
        state.ReadDeclarations++;
        return state.Capture
            ? query.Where(page => page.Owner == state.Owner || state.Allowed.Contains(page.Id), state.Canonical)
            : query.Where(page => page.Published, state.Canonical);
    }
}

internal sealed class CounterpartPageHook(CounterpartEndpointState state) : ICollectionHook<CounterpartPage>, IModelHook<CounterpartPage>, IRequestOptionsHook<CounterpartPage>, IEmitHook<CounterpartPage>
{
    public int Order => 0;
    public Task OnBuildingOptions(HookContext<CounterpartPage> context, QueryOptions options)
    {
        if (state.Change == "options-fallback") context.ShortCircuit(new OkObjectResult(new CounterpartPage { Id = "unproved" }));
        return Task.CompletedTask;
    }
    public Task<EmitDecision> OnEmitCollection(HookContext<CounterpartPage> context, object payload)
    {
        if (state.Change?.StartsWith("project", StringComparison.Ordinal) == true)
        {
            if (state.Change == "project-unless-shaped" && (context.Options.IncludeRelationships
                || context.Options.Shape is "map" or "dict")) return Task.FromResult<EmitDecision>(EmitDecision.Next());
            var sources = ((IEnumerable<CounterpartPage>)payload).ToList();
            switch (state.Change)
            {
                case "project-foreign": sources[0] = new CounterpartPage { Id = sources[0].Id }; break;
                case "project-reorder": sources.Reverse(); break;
                case "project-subset": sources.RemoveAt(0); break;
            }
            var decision = EmitDecision.Project(sources, row =>
            {
                state.MapCalls++;
                switch (state.Change)
                {
                    case "project-id": row.Id = "unproved"; break;
                    case "project-principal": context.User.AddIdentity(new ClaimsIdentity("changed")); break;
                    case "project-filter": context.Options.Filter = null; break;
                    case "project-throws": throw new InvalidOperationException("Must not serialize mapper exception data");
                }
                return CounterpartPageSummary.From(row) with { Personalized = context.User.Identity?.IsAuthenticated == true };
            });
            if (state.Change == "project-owned-sources") sources.Clear();
            return Task.FromResult(decision);
        }
        return Task.FromResult<EmitDecision>(state.Change == "emit"
            ? EmitDecision.With(new[] { new CounterpartPage { Id = "unproved" } }) : EmitDecision.Next());
    }
    public Task<EmitDecision> OnEmitModel(HookContext<CounterpartPage> context, object payload)
        => Task.FromResult<EmitDecision>(state.Change == "model-emit"
            ? EmitDecision.With(new CounterpartPage { Id = "unproved" }) : EmitDecision.Next());
    public Task OnBeforeFetch(HookContext<CounterpartPage> context, QueryOptions options)
    {
        if (state.Capture)
        {
            state.Owner = "bob";
            state.Allowed.Clear();
            state.Allowed.Add("later");
        }
        return Task.CompletedTask;
    }
    public Task OnAfterFetch(HookContext<CounterpartPage> context, List<CounterpartPage> items)
    {
        if (items.Count == 0) return Task.CompletedTask;
        switch (state.Change)
        {
            case "add": items.Add(new CounterpartPage { Id = "unproved" }); break;
            case "replace": items[0] = new CounterpartPage { Id = items[0].Id }; break;
            case "id": items[0].Id = "unproved"; break;
            case "filter": context.Options.Filter = Filter.Eq("Id", "unproved"); break;
            case "shape": context.Options.Shape = "map"; break;
            case "relationships": context.Options.IncludeRelationships = true; break;
            case "project-after-reorder": items.Reverse(); break;
            case "principal": ((ClaimsIdentity)context.User.Identity!).AddClaim(new Claim(ClaimTypes.Role, "admin")); break;
            case "fallback": context.ShortCircuit(new OkObjectResult(new CounterpartPage { Id = "unproved" })); break;
            case "erased-fallback": context.Options.Filter = null; context.ShortCircuit(new OkObjectResult(new CounterpartPage { Id = "unproved" })); break;
            case "denial": context.ShortCircuit(new ObjectResult(new CounterpartPage { Id = "unproved" }) { StatusCode = 403 }); break;
        }
        return Task.CompletedTask;
    }
    public Task OnBeforeFetch(HookContext<CounterpartPage> context, string id) => Task.CompletedTask;
    public Task OnAfterFetch(HookContext<CounterpartPage> context, CounterpartPage? model)
    {
        if (model is not null && state.Change == "model-id") model.Id = "unproved";
        if (state.Change == "model-fallback") context.ShortCircuit(new OkObjectResult(new CounterpartPage { Id = "unproved" }));
        if (state.Change == "model-erased-fallback") { context.Options.Filter = null; context.ShortCircuit(new OkObjectResult(new CounterpartPage { Id = "unproved" })); }
        if (state.Change == "denial") context.ShortCircuit(new ObjectResult(new CounterpartPage { Id = "unproved" }) { StatusCode = 403 });
        return Task.CompletedTask;
    }
    private Task Mutation() { state.MutationHooks++; return Task.CompletedTask; }
    public Task OnBeforeSave(HookContext<CounterpartPage> context, CounterpartPage model) => Mutation();
    public Task OnAfterSave(HookContext<CounterpartPage> context, CounterpartPage model) => Task.CompletedTask;
    public Task OnBeforeDelete(HookContext<CounterpartPage> context, CounterpartPage model) => Mutation();
    public Task OnAfterDelete(HookContext<CounterpartPage> context, CounterpartPage model) => Task.CompletedTask;
    public Task OnBeforePatch(HookContext<CounterpartPage> context, string id, object patch) => Mutation();
    public Task OnAfterPatch(HookContext<CounterpartPage> context, CounterpartPage model) => Task.CompletedTask;
}

public sealed record CounterpartPageSummary(string Id, string Label, bool Personalized = false)
    : IProjectionOf<CounterpartPage, CounterpartPageSummary>
{
    public static CounterpartPageSummary From(CounterpartPage page) => new(page.Id, page.Title);
}

internal sealed class CounterpartTailHook(CounterpartEndpointState state) : IEmitHook<CounterpartPage>
{
    public int Order => 100;
    public Task<EmitDecision> OnEmitCollection(HookContext<CounterpartPage> context, object payload)
    {
        state.TailCalls++;
        state.TailSawEntityRows = payload is IEnumerable<CounterpartPage>;
        return Task.FromResult<EmitDecision>(EmitDecision.Next());
    }
    public Task<EmitDecision> OnEmitModel(HookContext<CounterpartPage> context, object payload)
        => Task.FromResult<EmitDecision>(EmitDecision.Next());
}

public sealed class CounterpartPrincipalContributor : IWebContextContributor
{
    public ValueTask ContributeAsync(WebContext context)
    {
        if (context.HttpContext.Request.Path.StartsWithSegments("/counterpart-pages")
            && context.HttpContext.Request.Headers["X-Test-Reader"] == "reader")
            context.UsePrincipal(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "reader")], "test")));
        return ValueTask.CompletedTask;
    }
}
