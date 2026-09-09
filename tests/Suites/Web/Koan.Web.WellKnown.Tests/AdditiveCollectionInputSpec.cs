using System.Collections;
using System.Buffers;
using System.Net;
using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using Koan.Core;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Web.Authorization;
using Koan.Web.Context;
using Koan.Web.Controllers;
using Koan.Web.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace Koan.Web.WellKnown.Tests;

public sealed class AdditiveCollectionInputSpec
{
    [Fact]
    public async Task Ordinary_custom_Newtonsoft_formatter_keeps_its_reader_behavior()
    {
        using var host = await Start(customFormatter: true);
        using var client = host.GetTestClient();
        using var content = Body("{}");
        var response = await client.PostAsync("/collection-input/plain", content);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("X-Collection-Reader", out var marker).Should().BeTrue();
        marker.Should().Equal("custom");
        JObject.Parse(await response.Content.ReadAsStringAsync())["native"]!.Values<string>()
            .Should().Equal("formatter-value");
    }

    [Fact]
    public async Task Ordinary_HTTP_body_restores_dictionary_arrays_without_widening_private_setters()
    {
        using var host = await Start();
        using var client = host.GetTestClient();
        using var content = Body("""{"buckets":{"tags":["supplied"],"empty":[]},"privateSetter":"forged","native":["native"]}""");
        var response = await client.PostAsync("/collection-input/plain", content);
        var wire = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", wire);
        var value = JObject.Parse(wire);
        value["buckets"]!["tags"]!.Values<string>().Should().Equal("supplied");
        value["buckets"]!["empty"]!.Should().BeOfType<JArray>().Which.Should().BeEmpty();
        value["native"]!.Values<string>().Should().Equal("native");
        value["privateSetter"]!.Value<string>().Should().Be("constructor-owned");
        value.Properties().Should().NotContain(property => property.Name == "_type");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Governed_HTTP_replacement_restores_arrays_only_for_authorized_callers(bool put, bool admin)
    {
        using var host = await Start();
        var seed = await new CollectionInputEntity { Name = "prior" }.Save();
        using var client = host.GetTestClient();
        if (admin) client.DefaultRequestHeaders.Add("X-Collection-Role", "admin");
        var body = new JObject
        {
            ["id"] = seed.Id, ["name"] = "updated",
            ["privateBuckets"] = new JObject { ["tags"] = new JArray("private-supplied"), ["empty"] = new JArray() },
            ["privateSetter"] = "forged"
        };
        using var content = Body(body.ToString());
        var response = put
            ? await client.PutAsync("/collection-input/entities/" + seed.Id, content)
            : await client.PostAsync("/collection-input/entities", content);
        var wire = await response.Content.ReadAsStringAsync();
        if (admin) response.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", wire);
        else response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Forbidden);
        var stored = (await CollectionInputEntity.Get(seed.Id))!;
        stored.Name.Should().Be(admin ? "updated" : "prior");
        stored.PrivateSetter.Should().Be("constructor-owned");
        if (admin)
        {
            stored.PrivateBuckets["tags"].Should().Equal("private-supplied");
            stored.PrivateBuckets["empty"].Should().BeEmpty();
            JObject.Parse(wire)["privateBuckets"]!["tags"]!.Values<string>().Should().Equal("private-supplied");
        }
        else
        {
            stored.PrivateBuckets.Should().BeEmpty();
            wire.Should().NotContain("private-supplied");
        }
    }

    [Fact]
    public async Task Seeded_collection_without_clear_refuses_instead_of_appending_defaults()
    {
        using var host = await Start();
        using var client = host.GetTestClient();
        using var content = Body("""{"values":["supplied"]}""");
        var response = await client.PostAsync("/collection-input/unsupported", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Clear");
    }

    [Fact]
    public async Task Prepared_operation_serializer_restores_the_same_collection_contract()
    {
        using var host = await Start();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "admin")], "test"));
        var fields = await FieldAccess.Prepare(typeof(CollectionInputEntity), host.Services, principal);
        fields.DemandReplacement();
        var serializer = JsonSerializer.Create(fields.CreateSerializerSettings(new JsonSerializerSettings
        { ContractResolver = new CamelCasePropertyNamesContractResolver() }));
        var model = JObject.Parse("""{"privateBuckets":{"tags":["typed"]},"privateSetter":"forged"}""")
            .ToObject<CollectionInputEntity>(serializer)!;
        model.PrivateBuckets["tags"].Should().Equal("typed");
        model.PrivateSetter.Should().Be("constructor-owned");
    }

    [Fact]
    public async Task PUT_keeps_route_identity_authoritative_before_materialization()
    {
        using var host = await Start();
        var seed = await new CollectionInputEntity { Name = "prior" }.Save();
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Collection-Role", "admin");
        using var content = Body("""{"id":"different","privateBuckets":{"tags":["not-saved"]}}""");
        (await client.PutAsync("/collection-input/entities/" + seed.Id, content)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await CollectionInputEntity.Get(seed.Id))!.Name.Should().Be("prior");
        (await CollectionInputEntity.Get("different")).Should().BeNull();
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<IHost> Start(bool customFormatter = false)
    {
        var host = Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseEnvironment("Test");
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddKoan();
                services.AddKoanControllersFrom<CollectionInputController>();
                services.AddSingleton<IWebContextContributor, CollectionInputContext>();
                if (customFormatter) services.AddSingleton<IConfigureOptions<MvcOptions>, CollectionInputFormatterSetup>();
            });
            web.Configure(_ => { });
        }).Build();
        await host.StartAsync();
        return host;
    }
}

internal sealed class CollectionInputFormatterSetup(IOptions<MvcNewtonsoftJsonOptions> json,
    ObjectPoolProvider pools, ILoggerFactory logs) : IConfigureOptions<MvcOptions>
{
    public void Configure(MvcOptions options)
    {
        for (var index = 0; index < options.InputFormatters.Count; index++)
            if (options.InputFormatters[index] is NewtonsoftJsonInputFormatter and not NewtonsoftJsonPatchInputFormatter)
                options.InputFormatters[index] = new CollectionInputFormatter(
                    logs.CreateLogger<NewtonsoftJsonInputFormatter>(), json.Value.SerializerSettings,
                    ArrayPool<char>.Shared, pools, options, json.Value);
    }
}

internal sealed class CollectionInputFormatter(ILogger logger, JsonSerializerSettings settings,
    ArrayPool<char> chars, ObjectPoolProvider pools, MvcOptions mvc, MvcNewtonsoftJsonOptions json)
    : NewtonsoftJsonInputFormatter(logger, settings, chars, pools, mvc, json)
{
    public override Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
    {
        context.HttpContext.Response.Headers["X-Collection-Reader"] = "custom";
        return InputFormatterResult.SuccessAsync(new CollectionInputPlain
        { Native = new CollectionInputNative(["formatter-value"]) });
    }
}

[ApiController]
[Route("collection-input")]
public sealed class CollectionInputController : ControllerBase
{
    [HttpPost("plain")]
    public IActionResult Plain([FromBody] CollectionInputPlain value) => Ok(value);
    [HttpPost("unsupported")]
    public IActionResult Unsupported([FromBody] CollectionInputUnsupported value) => Ok(value);
}

[Route("collection-input/entities")]
public sealed class CollectionInputEntityController : EntityController<CollectionInputEntity> { }

[Access(read: Access.Anyone, write: Access.Anyone)]
public sealed class CollectionInputEntity : Entity<CollectionInputEntity>
{
    public string Name { get; set; } = "";
    [Access(read: "is:admin", write: "is:admin")]
    public Dictionary<string, CollectionInputBucket> PrivateBuckets { get; set; } = new();
    public string PrivateSetter { get; private set; } = "constructor-owned";
}

public sealed class CollectionInputPlain
{
    public Dictionary<string, CollectionInputBucket> Buckets { get; set; } = new();
    public string PrivateSetter { get; private set; } = "constructor-owned";
    public CollectionInputNative Native { get; set; } = new([]);
}

public sealed class CollectionInputBucket : IEnumerable<string>
{
    private readonly List<string> _values = ["constructor-seed"];
    public void Add(string value) => _values.Add(value);
    public void Clear() => _values.Clear();
    public IEnumerator<string> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class CollectionInputNative(IEnumerable<string> values) : IEnumerable<string>
{
    private readonly string[] _values = values.ToArray();
    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_values).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class CollectionInputUnsupported
{
    public CollectionInputUnclearable Values { get; set; } = new();
}

public sealed class CollectionInputUnclearable : IEnumerable<string>
{
    private readonly List<string> _values = ["constructor-seed"];
    public void Add(string value) => _values.Add(value);
    public IEnumerator<string> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class CollectionInputContext : IWebContextContributor
{
    public ValueTask ContributeAsync(WebContext context)
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments("/collection-input")) return ValueTask.CompletedTask;
        var role = context.HttpContext.Request.Headers["X-Collection-Role"].ToString();
        var identity = role.Length == 0 ? new ClaimsIdentity() : new ClaimsIdentity("collection-test");
        if (role.Length > 0) identity.AddClaim(new Claim(ClaimTypes.Role, role));
        context.UsePrincipal(new ClaimsPrincipal(identity));
        return ValueTask.CompletedTask;
    }
}
