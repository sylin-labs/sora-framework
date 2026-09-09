using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AwesomeAssertions;
using Koan.Core;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Web.Authorization;
using Koan.Web.Context;
using Koan.Web.Controllers;
using Koan.Web.Extensions;
using Koan.Web.Options;
using Koan.Web.Endpoints;
using Koan.Web.Hooks;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace Koan.Web.WellKnown.Tests;

public sealed class FieldAccessSpec
{
    [Theory]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("/custom", false)]
    [InlineData("/custom", true)]
    [InlineData("/json", false)]
    [InlineData("/json", true)]
    [InlineData("/wrapped", false)]
    [InlineData("/wrapped", true)]
    public async Task Actual_MVC_typed_results_apply_conditional_members(string route, bool admin)
    {
        using var host = await Start();
        var source = await Seed();
        var response = await Client(host, admin).GetAsync($"/field-access{route}/{source.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("public-name");
        text.Contains("private-value", StringComparison.Ordinal).Should().Be(admin);
        text.Contains("nested-private", StringComparison.Ordinal).Should().Be(admin);
        text.Should().NotContain("always-hidden");
        (await FieldAccessWork.Get(source.Id))!.PrivateNote.Should().Be("private-value", "formatting must not mutate persistence");
    }

    [Theory]
    [InlineData("default-json", false)]
    [InlineData("default-json", true)]
    [InlineData("explicit-default", false)]
    [InlineData("explicit-default", true)]
    public async Task Standard_default_settings_preserve_Pascal_case_and_member_policy(string route, bool admin)
    {
        using var host = await Start();
        var source = await Seed();
        var response = await Client(host, admin).GetAsync($"/field-access/{route}/{source.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var value = JObject.Parse(await response.Content.ReadAsStringAsync());
        value["Name"]!.Value<string>().Should().Be("public-name");
        value["name"].Should().BeNull();
        (value["private_note"] is not null).Should().Be(admin);
        value["AlwaysHidden"].Should().BeNull();
    }

    [Fact]
    public async Task Ordinary_typed_response_does_not_require_output_buffering()
    {
        using var host = await Start(suppressBuffering: true);
        var response = await Client(host).GetAsync("/field-access/ordinary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("ordinary-value");
    }

    [Theory]
    [InlineData("plain-custom-closed", HttpStatusCode.OK)]
    [InlineData("plain-custom-open", HttpStatusCode.InternalServerError)]
    public async Task Custom_serializer_compatibility_distinguishes_closed_and_open_contracts(string route, HttpStatusCode expected)
    {
        using var host = await Start();
        var response = await Client(host).GetAsync("/field-access/" + route);
        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.OK)
            (await response.Content.ReadAsStringAsync()).Should().Contain("plain-value");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shared_Entity_raw_emit_replacement_refuses_before_leaking_fields(bool collection)
    {
        using var host = await Start(rawEmit: true);
        var source = await Seed();
        var response = await Client(host).GetAsync(collection ? "/field-access" : "/field-access/" + source.Id);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("private-value");
    }

    [Fact]
    public async Task List_map_and_dict_keep_the_public_contract()
    {
        using var host = await Start();
        await Seed();
        foreach (var suffix in new[] { "", "?shape=map", "?shape=dict", "?access=true", "?with=all" })
        {
            var response = await Client(host).GetAsync("/field-access" + suffix);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var text = await response.Content.ReadAsStringAsync();
            text.Should().Contain("public-name");
            text.Should().NotContain("private-value");
        }
    }

    [Fact]
    public async Task Parallel_admin_and_anonymous_results_do_not_share_decisions()
    {
        using var host = await Start();
        var source = await Seed();
        using var admin = Client(host, true);
        using var anonymous = Client(host);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(async index =>
        {
            var text = await (index % 2 == 0 ? admin : anonymous).GetStringAsync("/field-access/" + source.Id);
            return (Admin: index % 2 == 0, HasPrivate: text.Contains("private-value", StringComparison.Ordinal));
        }));
        results.Should().OnlyContain(result => result.Admin == result.HasPrivate);
    }

    [Fact]
    public async Task Public_entity_read_still_consults_resource_grants_for_a_restricted_member()
    {
        using var host = await Start();
        var source = await Seed();
        await new AgentGrant { Subject = "operator", Resource = nameof(FieldAccessWork), Capability = "is:admin" }.Save();
        using var client = Client(host);
        client.DefaultRequestHeaders.Add("X-Field-Subject", "operator");
        var text = await client.GetStringAsync("/field-access/" + source.Id);
        text.Should().Contain("private-value");
        text.Should().NotContain("nested-private", "a grant for Work must not become a grant for the nested resource type");
    }

    [Theory]
    [InlineData("filter=%7B%22PrivateNote%22%3A%22private-value%22%7D")]
    [InlineData("filter=%7B%22private_note%22%3A%22private-value%22%7D")]
    [InlineData("sort=PrivateNote")]
    [InlineData("sort=Detail.Secret")]
    public async Task Caller_field_queries_refuse_inference(string query)
    {
        using var host = await Start();
        await Seed();
        var response = await Client(host).GetAsync("/field-access?" + query);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("private-value");
    }

    [Fact]
    public async Task Replacement_omission_refuses_without_clearing_the_stored_field()
    {
        using var host = await Start();
        var source = await Seed();
        var response = await Client(host).PostAsJsonAsync("/field-access", new { id = source.Id, name = "changed" });
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.BadRequest);
        var stored = await FieldAccessWork.Get(source.Id);
        stored!.Name.Should().Be("public-name");
        stored.PrivateNote.Should().Be("private-value");
    }

    [Theory]
    [InlineData("dynamic")]
    [InlineData("dynamic-formatter")]
    [InlineData("dynamic-resolver")]
    [InlineData("polymorphic-resolver")]
    [InlineData("problem-extension")]
    [InlineData("serializable-wrapper")]
    [InlineData("converter")]
    [InlineData("formatter")]
    public async Task Unsupported_typed_outputs_fail_without_partial_content(string shape)
    {
        using var host = await Start();
        var source = await Seed();
        var response = await Client(host).GetAsync($"/field-access/{shape}/{source.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("private-value").And.NotContain("public-prefix");
    }

    [Fact]
    public async Task Unrelated_raw_custom_outputs_keep_their_existing_representation()
    {
        using var host = await Start();
        var response = await Client(host).GetAsync("/field-access/raw");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("plain custom text");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Custom_typed_body_cannot_replace_protected_values_even_when_omitted(bool supplyPrivate)
    {
        using var host = await Start();
        var source = await Seed();
        var payload = new JObject { ["id"] = source.Id, ["name"] = "custom overwrite" };
        if (supplyPrivate) payload["private_note"] = "forged-private";
        using var content = new StringContent(payload.ToString(), System.Text.Encoding.UTF8, "application/json");
        var response = await Client(host).PostAsync("/field-access/custom-input", content);
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
        var stored = await FieldAccessWork.Get(source.Id);
        stored!.Name.Should().Be("public-name");
        stored.PrivateNote.Should().Be("private-value");
    }

    [Fact]
    public async Task Delete_query_refuses_a_protected_predicate_before_any_removal()
    {
        using var host = await Start();
        var source = await Seed();
        using var scope = host.Services.CreateScope();
        var context = new EntityRequestContext(scope.ServiceProvider, new QueryOptions(), default);
        var endpoint = scope.ServiceProvider.GetRequiredService<IEntityEndpointService<FieldAccessWork, string>>();
        var result = await endpoint.DeleteByQuery(new EntityDeleteByQueryRequest
        {
            Context = context, Query = "{\"PrivateNote\":\"private-value\"}"
        });
        result.ShortCircuitResult.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        (await FieldAccessWork.Get(source.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Bulk_replacement_refuses_before_updating_any_rows()
    {
        using var host = await Start();
        var source = await Seed();
        var response = await Client(host).PostAsJsonAsync("/field-access/bulk", new[]
        {
            new { id = source.Id, name = "updated" }, new { id = "new-row", name = "created" }
        });
        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
        (await FieldAccessWork.Get(source.Id))!.Name.Should().Be("public-name");
        (await FieldAccessWork.Get("new-row")).Should().BeNull();
    }

    [Fact]
    public async Task Trusted_BuildOptions_predicate_can_use_the_hidden_field()
    {
        using var host = await Start(serverFilter: true);
        await Seed();
        await new FieldAccessWork { Name = "excluded", PrivateNote = "different" }.Save();
        var response = await Client(host).GetAsync("/field-access");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("public-name").And.NotContain("excluded").And.NotContain("private-value");
    }

    [Theory]
    [InlineData("replace", "/private_note", null)]
    [InlineData("test", "/private_note", null)]
    [InlineData("copy", "/name", "/private_note")]
    [InlineData("move", "/name", "/private_note")]
    [InlineData("replace", "/detail", null)]
    public async Task Patch_denies_protected_reads_writes_and_parent_replacement(string operation, string path, string? from)
    {
        using var host = await Start();
        var source = await Seed();
        var body = new JArray(new JObject { ["op"] = operation, ["path"] = path, ["from"] = from, ["value"] = "changed" });
        using var content = new StringContent(body.ToString(), System.Text.Encoding.UTF8, "application/json-patch+json");
        var response = await Client(host).PatchAsync("/field-access/" + source.Id, content);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await FieldAccessWork.Get(source.Id))!.PrivateNote.Should().Be("private-value");
    }

    [Fact]
    public async Task Patch_public_member_preserves_omitted_restricted_members()
    {
        using var host = await Start();
        var source = await Seed();
        using var content = new StringContent("[{\"op\":\"replace\",\"path\":\"/name\",\"value\":\"changed\"}]",
            System.Text.Encoding.UTF8, "application/json-patch+json");
        var response = await Client(host).PatchAsync("/field-access/" + source.Id, content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await FieldAccessWork.Get(source.Id);
        stored!.Name.Should().Be("changed");
        stored.PrivateNote.Should().Be("private-value");
        (await response.Content.ReadAsStringAsync()).Should().NotContain("private-value");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_Access_on_JSON_ignored_member_still_governs_shared_replacement(bool admin)
    {
        using var host = await Start();
        var source = await new FieldAccessIgnoredEntity { Name = "original", Secret = "stored-private" }.Save();
        using var scope = host.Services.CreateScope();
        var principal = admin
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "test"))
            : new ClaimsPrincipal();
        var context = new EntityRequestContext(scope.ServiceProvider, new QueryOptions(), default, user: principal);
        var endpoint = scope.ServiceProvider.GetRequiredService<IEntityEndpointService<FieldAccessIgnoredEntity, string>>();
        var result = await endpoint.Upsert(new EntityUpsertRequest<FieldAccessIgnoredEntity, string>
        {
            Context = context, Model = new FieldAccessIgnoredEntity { Id = source.Id, Name = "updated", Secret = "replacement" }
        });
        if (admin) result.ShortCircuitResult.Should().BeNull();
        else result.ShortCircuitResult.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(403);
        var stored = (await FieldAccessIgnoredEntity.Get(source.Id))!;
        stored.Name.Should().Be(admin ? "updated" : "original", "denied whole replacement must not persist even public changes");
        stored.Secret.Should().BeEmpty("the existing InMemory serializer also honors JsonIgnore; this fixture does not claim ignored-field persistence");
        var response = await Client(host, admin).GetAsync("/field-access/ignored/" + source.Id);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("stored-private").And.NotContain("replacement");
    }

    [Theory]
    [InlineData(nameof(FieldAccessInheritedBase), false, false)]
    [InlineData(nameof(FieldAccessInheritedA), true, false)]
    [InlineData(nameof(FieldAccessInheritedB), false, true)]
    public async Task Inherited_member_grants_bind_the_actual_containing_resource(string resource, bool allowA, bool allowB)
    {
        using var host = await Start();
        await new AgentGrant { Subject = "operator", Resource = resource, Capability = "is:admin" }.Save();
        using var client = Client(host);
        client.DefaultRequestHeaders.Add("X-Field-Subject", "operator");
        var response = await client.GetAsync("/field-access/inherited");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var value = JObject.Parse(await response.Content.ReadAsStringAsync());
        (value["a"]!["secret"] is not null).Should().Be(allowA);
        (value["b"]!["secret"] is not null).Should().Be(allowB);
    }

    [Theory]
    [InlineData("application/json", "{\"privateDetails\":{}}")]
    [InlineData("application/merge-patch+json", "{\"privateDetails\":{}}")]
    [InlineData("application/json", "{\"public/label\":\"forged\"}")]
    [InlineData("application/merge-patch+json", "{\"public/label\":\"forged\"}")]
    public async Task Partial_and_merge_documents_cannot_bypass_field_admission(string mediaType, string body)
    {
        using var host = await Start();
        var source = await Seed();
        using var content = new StringContent(body, System.Text.Encoding.UTF8, mediaType);
        var response = await Client(host).PatchAsync("/field-access/" + source.Id, content);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var stored = (await FieldAccessWork.Get(source.Id))!;
        stored.PrivateDetails.Should().BeNull();
        stored.AliasedSecret.Should().Be("slash-private");
    }

    [Fact]
    public async Task Authorized_partial_document_and_exact_public_JSON_Patch_remain_supported()
    {
        using var host = await Start();
        var source = await Seed();
        using var partial = new StringContent("{\"name\":\"admin-edit\"}", System.Text.Encoding.UTF8, "application/json");
        var response = await Client(host, true).PatchAsync("/field-access/" + source.Id, partial);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await FieldAccessWork.Get(source.Id))!.Name.Should().Be("admin-edit");
        using var patch = new StringContent("[{\"op\":\"replace\",\"path\":\"/name\",\"value\":\"public-edit\"}]",
            System.Text.Encoding.UTF8, "application/json-patch+json");
        (await Client(host).PatchAsync("/field-access/" + source.Id, patch)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await FieldAccessWork.Get(source.Id))!.Name.Should().Be("public-edit");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Framework_relationship_wrappers_preserve_root_parent_and_child_field_policy(bool admin)
    {
        using var host = await Start();
        var parent = await new FieldAccessRelatedParent { Secret = "parent-private" }.Save();
        var source = await Seed();
        source.ParentId = parent.Id;
        await source.Save();
        await new FieldAccessRelatedChild { WorkId = source.Id, Secret = "child-private" }.Save();
        using var client = Client(host, admin);
        foreach (var route in new[] { "/field-access?with=all", "/field-access/" + source.Id + "?with=all" })
        {
            var response = await client.GetAsync(route);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var text = await response.Content.ReadAsStringAsync();
            text.Should().Contain("public-name");
            text.Contains("private-value", StringComparison.Ordinal).Should().Be(admin);
            text.Contains("parent-private", StringComparison.Ordinal).Should().Be(admin);
            text.Contains("child-private", StringComparison.Ordinal).Should().Be(admin);
        }
    }

    [Fact]
    public async Task MVC_member_origin_uses_the_same_trusted_request_context_as_entity_authority()
    {
        using var host = await Start();
        var source = await Seed();
        foreach (var route in new[] { "/field-access/", "/field-access/custom/" })
        {
            var response = await Client(host).GetAsync(route + source.Id);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Contain("remote-member-value");
        }
    }

    [Fact]
    public async Task Summary_controller_default_reaches_emit_hook_while_full_and_keyed_remain_full()
    {
        using var host = await Start(summaryHook: true);
        var source = await new FieldAccessSummaryEntity { Name = "summary-source" }.Save();
        using var client = Client(host);
        var summary = JArray.Parse(await client.GetStringAsync("/field-summary"));
        summary[0]["marker"]!.Value<string>().Should().Be("personalized-summary");
        var full = await client.GetAsync("/field-summary?view=full");
        var fullBody = JArray.Parse(await full.Content.ReadAsStringAsync());
        fullBody[0]["name"]!.Value<string>().Should().Be("summary-source");
        fullBody[0]["marker"].Should().BeNull();
        var keyed = await client.GetAsync("/field-summary/" + source.Id);
        JObject.Parse(await keyed.Content.ReadAsStringAsync())["name"]!.Value<string>().Should().Be("summary-source");
        keyed.Headers.GetValues("Koan-View").Should().Contain("full");
    }

    private static Task<FieldAccessWork> Seed() => new FieldAccessWork
    {
        Name = "public-name", PrivateNote = "private-value", Detail = new FieldAccessDetail { Secret = "nested-private" }
    }.Save();

    private static HttpClient Client(IHost host, bool admin = false)
    {
        var client = host.GetTestClient();
        if (admin) client.DefaultRequestHeaders.Add("X-Field-Role", "admin");
        return client;
    }

    private static async Task<IHost> Start(bool serverFilter = false, bool suppressBuffering = false, bool rawEmit = false, bool summaryHook = false)
    {
        var host = Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseEnvironment("Test");
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddKoan();
                services.AddKoanControllersFrom<FieldAccessController>();
                services.AddSingleton<IWebContextContributor, FieldAccessContext>();
                services.AddExceptionHandler(options => options.ExceptionHandlingPath = "/field-access/error");
                services.Configure<WebPipelineOptions>(options => options.UseExceptionHandler = true);
                if (summaryHook) services.AddSingleton<IEmitHook<FieldAccessSummaryEntity>, FieldAccessSummaryEmit>();
                if (suppressBuffering) services.Configure<MvcOptions>(options => options.SuppressOutputFormatterBuffering = true);
                if (rawEmit) services.AddSingleton<IEmitHook<FieldAccessWork>, FieldAccessRawEmit>();
                if (serverFilter) services.AddSingleton<IRequestOptionsHook<FieldAccessWork>, FieldAccessServerFilter>();
            });
            web.Configure(_ => { });
        }).Build();
        await host.StartAsync();
        return host;
    }
}

[Access(read: Access.Anyone, write: Access.Anyone, remove: Access.Anyone)]
public sealed class FieldAccessWork : Entity<FieldAccessWork>
{
    public string Name { get; set; } = "";
    [Koan.Data.Core.Relationships.Parent(typeof(FieldAccessRelatedParent))]
    public string? ParentId { get; set; }
    [Access(read: "origin:remote")]
    public string RemoteField { get; set; } = "remote-member-value";
    [Access(read: "is:admin", write: "is:admin")]
    [JsonProperty("private_note")]
    public string PrivateNote { get; set; } = "";
    public FieldAccessDetail Detail { get; set; } = new();
    [Access(write: "is:admin")]
    public FieldAccessDetail? PrivateDetails { get; set; }
    [Access(read: "is:admin", write: "is:admin")]
    [JsonProperty("public/label")]
    public string AliasedSecret { get; set; } = "slash-private";
    public string AlwaysHidden { get; set; } = "always-hidden";
    public bool ShouldSerializeAlwaysHidden() => false;
}

public sealed class FieldAccessDetail
{
    [Access(read: "is:admin", write: "is:admin")]
    public string Secret { get; set; } = "";
}

public sealed record FieldAccessWrapper(IReadOnlyList<FieldAccessWork> Items);

[Route("field-access")]
public sealed class FieldAccessController : EntityController<FieldAccessWork>
{
    [HttpGet("plain-custom-closed")]
    public IActionResult PlainClosed() => new JsonResult(new FieldAccessPlainClosed(),
        new JsonSerializerSettings { ContractResolver = new FieldAccessCustomResolver() });

    [HttpGet("plain-custom-open")]
    public IActionResult PlainOpen() => new JsonResult(new FieldAccessPlainOpen(),
        new JsonSerializerSettings { ContractResolver = new FieldAccessCustomResolver() });

    [HttpGet("ignored/{id}")]
    public async Task<IActionResult> Ignored(string id) => Ok(await FieldAccessIgnoredEntity.Get(id));

    [HttpGet("serializable-wrapper/{id}")]
    public async Task<IActionResult> SerializableWrapper(string id) => Ok(new FieldAccessSerializableWrapper((await FieldAccessWork.Get(id))!));

    [HttpGet("inherited")]
    public IActionResult Inherited() => Ok(new { A = new FieldAccessInheritedA(), B = new FieldAccessInheritedB() });

    [HttpGet("ordinary")]
    public IActionResult Ordinary() => Ok(new { Value = "ordinary-value" });

    [HttpGet("default-json/{id}")]
    public async Task<IActionResult> DefaultJson(string id) => new JsonResult(await FieldAccessWork.Get(id), new JsonSerializerSettings());

    [HttpGet("explicit-default/{id}")]
    public async Task<IActionResult> ExplicitDefault(string id) => new JsonResult(await FieldAccessWork.Get(id),
        new JsonSerializerSettings { ContractResolver = new DefaultContractResolver() });

    [HttpGet("error")]
    public IActionResult Error() => Problem("The typed response could not be produced.");

    [HttpGet("raw")]
    public IActionResult Raw() => Ok("plain custom text");

    [HttpGet("custom/{id}")]
    public async Task<IActionResult> Custom(string id) => new ObjectResult(await FieldAccessWork.Get(id));

    [HttpGet("json/{id}")]
    public async Task<IActionResult> CustomJson(string id) => new JsonResult(await FieldAccessWork.Get(id),
        new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver(), NullValueHandling = NullValueHandling.Include });

    [HttpGet("wrapped/{id}")]
    public async Task<IActionResult> Wrapped(string id) => Ok(new FieldAccessWrapper([ (await FieldAccessWork.Get(id))! ]));

    [HttpGet("dynamic/{id}")]
    public async Task<IActionResult> Dynamic(string id) => Ok(new { First = "public-prefix", Value = (object)(await FieldAccessWork.Get(id))! });

    [HttpGet("polymorphic-resolver/{id}")]
    public IActionResult PolymorphicResolver(string id) => new JsonResult(
        new { Value = (FieldAccessPublicBase)new FieldAccessDerived { Secret = "private-value" } },
        new JsonSerializerSettings { ContractResolver = new FieldAccessCustomResolver() });

    [HttpGet("problem-extension/{id}")]
    public async Task<IActionResult> ProblemExtension(string id)
    {
        var details = new ProblemDetails { Title = "public-prefix", Status = 409 };
        details.Extensions["retained"] = await FieldAccessWork.Get(id);
        return new ObjectResult(details) { StatusCode = 409 };
    }

    [HttpGet("dynamic-formatter/{id}")]
    public async Task<IActionResult> DynamicFormatter(string id)
    {
        var result = new ObjectResult(new { First = "public-prefix", Value = (object)(await FieldAccessWork.Get(id))! });
        result.Formatters.Add(new SystemTextJsonOutputFormatter(new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
        return result;
    }

    [HttpGet("dynamic-resolver/{id}")]
    public async Task<IActionResult> DynamicResolver(string id) => new JsonResult(
        new { First = "public-prefix", Value = (object)(await FieldAccessWork.Get(id))! },
        new JsonSerializerSettings { ContractResolver = new FieldAccessCustomResolver() });

    [HttpGet("converter/{id}")]
    public async Task<IActionResult> Converted(string id) => new JsonResult(await FieldAccessWork.Get(id),
        new JsonSerializerSettings { Converters = { new FieldAccessBypassConverter() } });

    [HttpGet("formatter/{id}")]
    public async Task<IActionResult> Formatted(string id)
    {
        var result = new ObjectResult(await FieldAccessWork.Get(id));
        result.Formatters.Add(new SystemTextJsonOutputFormatter(new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }));
        return result;
    }

    [HttpPost("custom-input")]
    public async Task<IActionResult> CustomInput([FromBody] FieldAccessWork work) => Ok(await work.Save());
}

internal sealed class FieldAccessBypassConverter : JsonConverter<FieldAccessWork>
{
    public override void WriteJson(JsonWriter writer, FieldAccessWork? value, JsonSerializer serializer)
        => writer.WriteValue(value?.PrivateNote);
    public override FieldAccessWork? ReadJson(JsonReader reader, Type objectType, FieldAccessWork? existingValue,
        bool hasExistingValue, JsonSerializer serializer) => throw new NotSupportedException();
}

internal sealed class FieldAccessServerFilter : IRequestOptionsHook<FieldAccessWork>
{
    public int Order => 0;
    public Task OnBuildingOptions(HookContext<FieldAccessWork> context, QueryOptions options)
    {
        options.AddPredicate<FieldAccessWork>(work => work.PrivateNote == "private-value");
        return Task.CompletedTask;
    }
}

public sealed class FieldAccessContext : IWebContextContributor
{
    public ValueTask ContributeAsync(WebContext context)
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments("/field-access")) return ValueTask.CompletedTask;
        var role = context.HttpContext.Request.Headers["X-Field-Role"].ToString();
        var subject = context.HttpContext.Request.Headers["X-Field-Subject"].ToString();
        var identity = role.Length + subject.Length == 0 ? new ClaimsIdentity() : new ClaimsIdentity("field-test");
        if (role.Length > 0) identity.AddClaim(new Claim(ClaimTypes.Role, role));
        if (subject.Length > 0) identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
        context.UsePrincipal(new ClaimsPrincipal(identity));
        return ValueTask.CompletedTask;
    }
}


internal sealed class FieldAccessRawEmit : IEmitHook<FieldAccessWork>
{
    public int Order => 0;
    public Task<EmitDecision> OnEmitCollection(HookContext<FieldAccessWork> context, object payload)
        => Task.FromResult<EmitDecision>(EmitDecision.With(JToken.FromObject(payload)));
    public Task<EmitDecision> OnEmitModel(HookContext<FieldAccessWork> context, object payload)
        => Task.FromResult<EmitDecision>(EmitDecision.With(JToken.FromObject(payload)));
}

internal sealed class FieldAccessCustomResolver : DefaultContractResolver { }

public class FieldAccessPublicBase { }
public sealed class FieldAccessDerived : FieldAccessPublicBase
{
    [Access(read: "is:admin", write: "is:admin")]
    public string Secret { get; set; } = "";
}

public class FieldAccessInheritedBase
{
    [Access(read: "is:admin", write: "is:admin")]
    public string Secret { get; set; } = "inherited-private";
}
public sealed class FieldAccessInheritedA : FieldAccessInheritedBase { }
public sealed class FieldAccessInheritedB : FieldAccessInheritedBase { }

[Access(read: Access.Anyone, write: Access.Anyone)]
public sealed class FieldAccessSummaryEntity : Entity<FieldAccessSummaryEntity>
{
    public string Name { get; set; } = "";
}
public sealed record FieldAccessSummary(string Id, string Marker) : IProjectionOf<FieldAccessSummaryEntity, FieldAccessSummary>
{
    public static FieldAccessSummary From(FieldAccessSummaryEntity entity) => new(entity.Id, "fallback");
}
[Route("field-summary")]
public sealed class FieldAccessSummaryController : EntitySummaryController<FieldAccessSummaryEntity, FieldAccessSummary> { }
internal sealed class FieldAccessSummaryEmit : IEmitHook<FieldAccessSummaryEntity>
{
    public int Order => 0;
    public Task<EmitDecision> OnEmitCollection(HookContext<FieldAccessSummaryEntity> context, object payload)
        => Task.FromResult(context.Options.View == "full" ? EmitDecision.Next()
            : EmitDecision.Project((IEnumerable<FieldAccessSummaryEntity>)payload,
                source => new FieldAccessSummary(source.Id, "personalized-" + context.Options.View)));
    public Task<EmitDecision> OnEmitModel(HookContext<FieldAccessSummaryEntity> context, object payload)
        => Task.FromResult<EmitDecision>(EmitDecision.Next());
}

[Access(read: Access.Anyone)]
public sealed class FieldAccessRelatedParent : Entity<FieldAccessRelatedParent>
{
    [Access(read: "is:admin", write: "is:admin")]
    public string Secret { get; set; } = "";
}
[Access(read: Access.Anyone)]
public sealed class FieldAccessRelatedChild : Entity<FieldAccessRelatedChild>
{
    [Koan.Data.Core.Relationships.Parent(typeof(FieldAccessWork))]
    public string WorkId { get; set; } = "";
    [Access(read: "is:admin", write: "is:admin")]
    public string Secret { get; set; } = "";
}

[Access(read: Access.Anyone, write: Access.Anyone)]
public sealed class FieldAccessIgnoredEntity : Entity<FieldAccessIgnoredEntity>
{
    public string Name { get; set; } = "";
    [JsonIgnore]
    [Access(read: "is:admin", write: "is:admin")]
    public string Secret { get; set; } = "";
}

// Exercise the legacy serializer contract as an unsupported retained-value wrapper.
[Serializable]
public sealed class FieldAccessSerializableWrapper(FieldAccessWork value) : System.Runtime.Serialization.ISerializable
{
    public FieldAccessWork Value { get; } = value;
    public void GetObjectData(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
        => info.AddValue("value", Value);
}

public sealed class FieldAccessPlainClosed { public string Value { get; set; } = "plain-value"; }
public class FieldAccessPlainOpen { public string Value { get; set; } = "plain-value"; }
