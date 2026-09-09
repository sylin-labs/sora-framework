using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Koan.Core.Hosting.App;
using Koan.Data.Core;
using Xunit;

namespace Koan.Web.AdapterSurface.InMemory.Tests.PredicateHook;

/// <summary>
/// WEB-0068 — end-to-end coverage of <c>QueryOptions.Filter</c> contributed via
/// <c>IRequestOptionsHook&lt;VisibilityWidget&gt;</c>. The hook narrows the result set by role
/// + owner; tests prove the composition AND-chains with the user's <c>?filter=</c> and that
/// pagination headers reflect the post-predicate population.
/// </summary>
public sealed class RequestOptionsHookPredicateSpecs : IClassFixture<InMemoryAdapterFactory>, IAsyncLifetime
{
    private readonly InMemoryAdapterFactory _factory;
    private IDisposable? _scope;

    public RequestOptionsHookPredicateSpecs(InMemoryAdapterFactory factory)
    {
        _factory = factory;
    }

    public async ValueTask InitializeAsync()
    {
        AggregateConfigs.Reset();
        _scope = AppHost.PushScope(_factory.Services);
        await _factory.ResetAsync();
        await VisibilityWidget.RemoveAll();
    }

    public ValueTask DisposeAsync()
    {
        _scope?.Dispose();
        _scope = null;
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Anonymous_caller_sees_only_published_rows()
    {
        await SeedMatrix();

        var client = _factory.Client;
        var response = await client.GetAsync("/api/visibility-widgets");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await ReadItems(response);
        items.Select(NameOf).Should().BeEquivalentTo(new[] { "pub-alice", "pub-bob" });
    }

    [Fact]
    public async Task Authenticated_caller_sees_published_plus_own_drafts()
    {
        await SeedMatrix();

        var client = _factory.Client;
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/visibility-widgets");
        req.Headers.Add("X-Test-User", "alice");
        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await ReadItems(response);
        items.Select(NameOf).Should().BeEquivalentTo(new[] { "pub-alice", "pub-bob", "draft-alice" });
    }

    [Fact]
    public async Task Admin_caller_sees_everything_except_hidden()
    {
        await SeedMatrix();

        var client = _factory.Client;
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/visibility-widgets");
        req.Headers.Add("X-Test-Role", "admin");
        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await ReadItems(response);
        items.Select(NameOf).Should().BeEquivalentTo(new[]
        {
            "pub-alice", "pub-bob", "draft-alice", "draft-bob"
        });
        items.Select(NameOf).Should().NotContain("hidden-row");
    }

    [Fact]
    public async Task Hidden_rows_never_surface_for_admin_or_anyone()
    {
        await SeedMatrix();

        var client = _factory.Client;
        foreach (var (label, headers) in new[]
        {
            ("anon", Array.Empty<(string, string)>()),
            ("user", new[] { ("X-Test-User", "alice") }),
            ("admin", new[] { ("X-Test-Role", "admin") })
        })
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/visibility-widgets");
            foreach (var (k, v) in headers) req.Headers.Add(k, v);
            var response = await client.SendAsync(req);

            var items = await ReadItems(response);
            items.Select(NameOf).Should().NotContain("hidden-row", $"tier={label} must never see Hidden");
        }
    }

    [Fact]
    public async Task UserFilter_AndComposes_with_hook_predicate()
    {
        // Hook narrows to Published + own Drafts; user's ?filter= further narrows by Name.
        // Only "pub-alice" matches both — the AND-chain.
        await SeedMatrix();

        var client = _factory.Client;
        var filter = JsonSerializer.Serialize(new { Name = "pub-alice" });
        var encoded = Uri.EscapeDataString(filter);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/visibility-widgets?filter={encoded}");
        req.Headers.Add("X-Test-User", "alice");
        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await ReadItems(response);
        items.Should().ContainSingle();
        NameOf(items[0]).Should().Be("pub-alice");
    }

    [Fact]
    public async Task UserFilter_cannot_escape_hook_predicate()
    {
        // The hook narrows to "not Hidden"; user's ?filter= asks for Hidden specifically.
        // The AND-chain reduces to false, so the user gets an empty set even though the row exists.
        // This is the load-bearing security claim of WEB-0068.
        await SeedMatrix();

        var client = _factory.Client;
        var filter = JsonSerializer.Serialize(new { Name = "hidden-row" });
        var encoded = Uri.EscapeDataString(filter);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/visibility-widgets?filter={encoded}");
        req.Headers.Add("X-Test-Role", "admin");
        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await ReadItems(response);
        items.Should().BeEmpty();
    }

    [Fact]
    public async Task Pagination_headers_reflect_post_predicate_total()
    {
        // 4 visible rows for an authenticated alice (2 Published + her 1 Draft = 3, plus the
        // global "not Hidden" leaves the 1 hidden out). At pageSize=2 we expect
        // X-Total-Count=3, X-Total-Pages=2.
        await SeedMatrix();

        var client = _factory.Client;
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/visibility-widgets?page=1&pageSize=2");
        req.Headers.Add("X-Test-User", "alice");
        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        response.Headers.GetValues("X-Page").Should().ContainSingle().Which.Should().Be("1");
        response.Headers.GetValues("X-Page-Size").Should().ContainSingle().Which.Should().Be("2");
        response.Headers.GetValues("X-Total-Count").Should().ContainSingle().Which.Should().Be("3");
        response.Headers.GetValues("X-Total-Pages").Should().ContainSingle().Which.Should().Be("2");

        var items = await ReadItems(response);
        items.Should().HaveCount(2);
    }

    [Fact]
    public async Task BodyQuery_path_also_composes_hook_predicate()
    {
        await SeedMatrix();

        var client = _factory.Client;
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/visibility-widgets/query")
        {
            Content = JsonContent.Create(new { sort = new[] { "Name" } })
        };
        req.Headers.Add("X-Test-User", "alice");
        var response = await client.SendAsync(req);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await ReadItems(response);
        items.Select(NameOf).Should().BeEquivalentTo(new[] { "pub-alice", "pub-bob", "draft-alice" });
    }

    private static async Task SeedMatrix()
    {
        await VisibilityWidget.Upsert(new VisibilityWidget { Id = "1", Name = "pub-alice", Status = VisibilityStatus.Published, OwnerId = "alice" });
        await VisibilityWidget.Upsert(new VisibilityWidget { Id = "2", Name = "pub-bob", Status = VisibilityStatus.Published, OwnerId = "bob" });
        await VisibilityWidget.Upsert(new VisibilityWidget { Id = "3", Name = "draft-alice", Status = VisibilityStatus.Draft, OwnerId = "alice" });
        await VisibilityWidget.Upsert(new VisibilityWidget { Id = "4", Name = "draft-bob", Status = VisibilityStatus.Draft, OwnerId = "bob" });
        await VisibilityWidget.Upsert(new VisibilityWidget { Id = "5", Name = "hidden-row", Status = VisibilityStatus.Hidden, OwnerId = "alice" });
    }

    private static string? NameOf(JsonElement e)
        => e.TryGetProperty("name", out var n) ? n.GetString() : null;

    private static async Task<List<JsonElement>> ReadItems(HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(raw)) return new List<JsonElement>();

        var doc = JsonDocument.Parse(raw).RootElement.Clone();
        if (doc.ValueKind == JsonValueKind.Array)
        {
            return doc.EnumerateArray().ToList();
        }
        if (doc.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            return items.EnumerateArray().ToList();
        }
        throw new InvalidOperationException(
            $"Unexpected response shape: {raw[..Math.Min(200, raw.Length)]}");
    }
}
