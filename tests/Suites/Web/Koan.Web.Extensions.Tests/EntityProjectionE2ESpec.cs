using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Koan.Web.Extensions.Tests;

/// <summary>
/// SEC-0004 Slice C — the per-row capability projection end-to-end. <see cref="Sprocket"/> has PUBLIC read but
/// owner-only write/remove, so a collection mixes the principal's own rows with others' and the per-row <c>can</c>
/// diverges. The bare array stays the default; <c>?access=true</c> opts into the <c>{ items, access }</c> sidecar;
/// a single fetch row-refines the <c>Koan-Access</c> header; and a custom verb (<see cref="Order"/>'s "fulfill")
/// surfaces in <c>can</c> exactly when permitted. Each test uses a unique marker to isolate its rows in the shared
/// open-read store.
/// </summary>
[Collection(RestEntityCollection.Name)]
public sealed class EntityProjectionE2ESpec
{
    private readonly RestEntityWebFactory _fx;

    public EntityProjectionE2ESpec(RestEntityWebFactory fx) => _fx = fx;

    // ── the bare array stays the default (opt-in only) ───────────────────────────────────────────────────────────
    [Fact]
    public async Task Without_the_toggle_a_collection_is_a_bare_array()
    {
        var tag = Tag();
        await CreateSprocket("plain-alice", tag);

        var body = JToken.Parse(await (await Send(HttpMethod.Get, SprocketPath(tag), "plain-alice")).Content.ReadAsStringAsync());
        body.Type.Should().Be(JTokenType.Array, "the projection sidecar is opt-in — the default response is unchanged");
    }

    [Fact]
    public async Task The_access_toggle_wraps_the_collection_in_an_items_plus_access_envelope()
    {
        var tag = Tag();
        await CreateSprocket("env-alice", tag);

        var body = JToken.Parse(await (await Send(HttpMethod.Get, SprocketPath(tag, access: true), "env-alice")).Content.ReadAsStringAsync());
        body.Type.Should().Be(JTokenType.Object);
        body["items"].Should().NotBeNull("the bare array moves under `items`");
        body["access"].Should().NotBeNull("the per-row manifest rides alongside under `access`");
    }

    // ── the headline: per-row can diverges by ownership ──────────────────────────────────────────────────────────
    [Fact]
    public async Task The_owner_can_do_everything_to_their_row_others_can_only_read()
    {
        const string alice = "div-alice", bob = "div-bob";
        var tag = Tag();
        var aliceId = await CreateSprocket(alice, tag);
        var bobId = await CreateSprocket(bob, tag);

        // alice lists the (public-read) collection with the projection on
        var body = JToken.Parse(await (await Send(HttpMethod.Get, SprocketPath(tag, access: true), alice)).Content.ReadAsStringAsync());
        var access = (JObject)body["access"]!;

        Can(access, aliceId).Should().BeEquivalentTo(new[] { "read", "write", "remove" }, "alice owns this row");
        Can(access, bobId).Should().BeEquivalentTo(new[] { "read" }, "alice can see bob's public row but not mutate it");
    }

    // ── a single fetch row-refines the Koan-Access header ────────────────────────────────────────────────────────
    [Fact]
    public async Task Get_by_id_row_refines_the_single_item_header()
    {
        const string alice = "hdr-alice", bob = "hdr-bob";
        var tag = Tag();
        var aliceId = await CreateSprocket(alice, tag);
        var bobId = await CreateSprocket(bob, tag);

        // alice fetches her own row → full authority
        var own = await Send(HttpMethod.Get, $"/api/sprocket/{aliceId}", alice);
        Header(own).Should().Be("read, write, remove");

        // alice fetches bob's public row → read only (honest: the coarse header would have over-advertised write/remove)
        var others = await Send(HttpMethod.Get, $"/api/sprocket/{bobId}", alice);
        others.StatusCode.Should().Be(HttpStatusCode.OK, "read is public");
        Header(others).Should().Be("read");
    }

    [Fact]
    public async Task Invalid_bearer_does_not_fall_back_to_anonymous_on_an_anyone_read()
    {
        var tag = Tag();
        var id = await CreateSprocket("invalid-by-id-owner", tag);

        var response = await SendInvalidBearer($"/api/sprocket/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a credential rejected by the selected handler must not inherit anonymous read authority");
    }

    [Fact]
    public async Task Invalid_bearer_does_not_fall_back_to_anonymous_on_an_anyone_collection_read()
    {
        var tag = Tag();
        await CreateSprocket("invalid-collection-owner", tag);

        var response = await SendInvalidBearer(SprocketPath(tag));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "collection reads use the same fail-closed authentication boundary as keyed reads");
    }

    [Fact]
    public async Task No_credentials_still_allow_anonymous_anyone_reads()
    {
        var tag = Tag();
        var id = await CreateSprocket("anonymous-control-owner", tag);

        var byId = await _fx.Client.GetAsync($"/api/sprocket/{id}");
        var collection = await _fx.Client.GetAsync(SprocketPath(tag));

        byId.StatusCode.Should().Be(HttpStatusCode.OK);
        collection.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── a custom verb participates in the manifest ───────────────────────────────────────────────────────────────
    [Fact]
    public async Task A_custom_verb_appears_in_can_only_for_a_permitted_principal()
    {
        var tag = Tag();
        await CreateOrder(tag, "ord-admin");

        var asAdmin = JToken.Parse(await (await SendRoles(HttpMethod.Get, OrderPath(tag, access: true), roles: "admin")).Content.ReadAsStringAsync());
        var adminAccess = (JObject)asAdmin["access"]!;
        adminAccess.Properties().Select(p => p.Name).Should().NotBeEmpty();
        adminAccess.Properties().Should().OnlyContain(p => ((JArray)p.Value["can"]!).Values<string>().Contains("fulfill"),
            "an admin sees the custom fulfill verb on every order");

        var asClerk = JToken.Parse(await (await SendRoles(HttpMethod.Get, OrderPath(tag, access: true), roles: "clerk")).Content.ReadAsStringAsync());
        var clerkAccess = (JObject)asClerk["access"]!;
        clerkAccess.Properties().Should().OnlyContain(p => !((JArray)p.Value["can"]!).Values<string>().Contains("fulfill"),
            "a non-admin never sees the custom fulfill verb");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────
    private static string Tag() => "t" + Guid.NewGuid().ToString("N");

    private static string SprocketPath(string tag, bool access = false)
    {
        var path = "/api/sprocket?filter=" + Uri.EscapeDataString($"{{\"spec\":\"{tag}\"}}");
        return access ? path + "&access=true" : path;
    }

    private static string OrderPath(string tag, bool access = false)
    {
        var path = "/api/order?filter=" + Uri.EscapeDataString($"{{\"item\":\"{tag}\"}}");
        return access ? path + "&access=true" : path;
    }

    private async Task<string> CreateSprocket(string user, string tag)
    {
        var res = await Send(HttpMethod.Post, "/api/sprocket", user, $"{{\"spec\":\"{tag}\"}}");
        res.IsSuccessStatusCode.Should().BeTrue($"create for {user} should succeed");
        return Id(JObject.Parse(await res.Content.ReadAsStringAsync()));
    }

    private async Task<string> CreateOrder(string tag, string user)
    {
        var res = await Send(HttpMethod.Post, "/api/order", user, $"{{\"item\":\"{tag}\"}}");
        res.IsSuccessStatusCode.Should().BeTrue("create order should succeed");
        return Id(JObject.Parse(await res.Content.ReadAsStringAsync()));
    }

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string user, string? json = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("X-Test-User", user);
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return _fx.Client.SendAsync(req);
    }

    private Task<HttpResponseMessage> SendRoles(HttpMethod method, string path, string roles)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Add("X-Test-Roles", roles);
        return _fx.Client.SendAsync(req);
    }

    private Task<HttpResponseMessage> SendInvalidBearer(string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "invalid");
        return _fx.Client.SendAsync(req);
    }

    private static string[] Can(JObject access, string id)
        => ((JArray)access[id]!["can"]!).Values<string>().ToArray()!;

    private static string Header(HttpResponseMessage res)
        => res.Headers.TryGetValues("Koan-Access", out var v) ? string.Join(", ", v) : "<absent>";

    private static string Id(JObject o) => (string)(o["id"] ?? o["Id"])!;
}
