using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Koan.Core.Diagnostics;
using Koan.Web.Auth.Domain;
using Xunit;

namespace Koan.Web.Auth.Integration.Tests;

/// <summary>
/// WEB-0071 / E5 — the ARCH-0079 spec that would have caught OIDC-501. Drives a REAL challenge→callback
/// round-trip for BOTH the oauth2 <c>test</c> provider and the oidc <c>test-oidc</c> provider through the
/// maintained ASP.NET handlers seeded by <c>AuthSchemeSeeder</c>, and asserts: the user lands authenticated,
/// the sub→NameIdentifier mapping fired, the <c>UserInfoMapper</c> role mapping fired (parity), and the
/// external-identity link was recorded (parity). The OIDC case additionally exercises discovery + JWKS +
/// signed-id_token validation + PKCE + nonce inside the handler — none of which the old 501 callback did.
/// </summary>
public sealed class AuthEngineSwapSpec : IClassFixture<AuthSwapFixture>
{
    private readonly AuthSwapFixture _fx;
    public AuthEngineSwapSpec(AuthSwapFixture fx) => _fx = fx;

    [Fact]
    public async Task OAuth2_challenge_callback_round_trip_authenticates_with_mapped_role_and_link()
    {
        using var client = _fx.NewClient();

        await DriveLogin(client, "/auth/test/challenge?return=/e2e/whoami", injectRole: "editor");

        var who = await ReadWhoAmI(client);
        who.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        who.GetProperty("id").GetString().Should().Be("alice@example.com");
        who.GetProperty("roles").EnumerateArray().Select(x => x.GetString()).Should().Contain("editor");

        // Parity: the external-identity link must have been recorded by the OnCreatingTicket hook.
        await AssertLinked("alice@example.com", "test");
    }

    [Fact]
    public async Task OIDC_challenge_callback_round_trip_validates_id_token_and_authenticates()
    {
        using var client = _fx.NewSplitHostClient();

        // The whole OIDC machinery runs here: metadata discovery, JWKS fetch, PKCE, nonce, and signed
        // id_token validation. If any of it failed the handler would NOT sign in → authenticated=false.
        await DriveLogin(
            client,
            "/auth/test-oidc/challenge?return=/e2e/whoami",
            injectRole: "admin",
            browserBaseUrl: _fx.PublicBaseUrl);

        var who = await ReadWhoAmI(client);
        who.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        who.GetProperty("id").GetString().Should().Be("alice@example.com");
        who.GetProperty("roles").EnumerateArray().Select(x => x.GetString()).Should().Contain("admin");

        await AssertLinked("alice@example.com", "test-oidc");
    }

    [Fact]
    public async Task Development_exposes_stable_test_endpoints_without_opt_in()
    {
        // The Test connector's automatic definitions and attribute-routed protocol surface share IsActive. No
        // startup filter, conventional mapper, or TestProvider:Enabled opt-in is required in Development.
        using var client = _fx.NewClient();
        var resp = await client.GetAsync("/.testoauth/.well-known/openid-configuration");
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the Test simulator's stable routes must be available with its automatic Development definitions");
    }

    [Fact]
    public async Task OIDC_default_port_spelling_can_change_between_authorization_and_callback()
    {
        const string origin = "http://koan-browser.test";
        using var client = _fx.NewSplitHostClient(origin);
        await DriveLogin(client, "/auth/test-oidc/challenge?return=/e2e/whoami", "admin",
            browserBaseUrl: origin, callbackHost: "koan-browser.test:80");
        (await ReadWhoAmI(client)).GetProperty("authenticated").GetBoolean().Should().BeTrue();

        client.DefaultRequestHeaders.Host = "koan-browser.test:80";
        using var discovery = await client.GetAsync("/.testoauth/.well-known/openid-configuration");
        using var document = JsonDocument.Parse(await discovery.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("issuer").GetString().Should().Be(origin + "/.testoauth");
    }

    [Fact]
    public async Task Provider_discovery_and_runtime_facts_project_the_same_compiled_plan()
    {
        using var client = _fx.NewClient();
        var response = await client.GetAsync("/.well-known/auth/providers");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var providers = document.RootElement.EnumerateArray().ToArray();

        providers.Select(provider => provider.GetProperty("id").GetString())
            .Should().BeEquivalentTo("test", "test-oidc");
        providers.Should().OnlyContain(provider =>
            provider.GetProperty("challengeUrl").GetString()
            == $"/auth/{provider.GetProperty("id").GetString()}/challenge");

        var facts = _fx.Services.GetRequiredService<IKoanRuntimeFacts>().Current.Facts;
        facts.Should().Contain(fact =>
            fact.Code == "koan.auth.provider.eligible" && fact.Subject == "auth:provider:test");
        facts.Should().Contain(fact =>
            fact.Code == "koan.auth.provider.eligible" && fact.Subject == "auth:provider:test-oidc");
        facts.Should().Contain(fact =>
            fact.Code == "koan.auth.provider.selected" && fact.Subject == "auth:provider:default");
    }

    [Fact]
    public async Task TestProvider_login_page_forwards_all_authorize_params_including_nonce()
    {
        // Bug: login.html rebuilt the authorize URL from a hand-picked param list that omitted `nonce`, so the
        // re-submitted authorize had no nonce → the id_token was minted without one → OIDC nonce validation failed
        // (IDX21320). The round-trip specs above bypass login.html via the pre-seeded persona cookie, so they can't
        // catch this. Guard the static page: it must seed the re-submitted authorize from ALL original params.
        using var client = _fx.NewClient();
        var resp = await client.GetAsync("/.testoauth/login.html");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the dev login page is served in Development");
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("const params = new URLSearchParams(location.search)",
            "the login page must seed the re-submitted authorize from ALL original params (incl. nonce), " +
            "not a hand-picked subset that silently drops new ones");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Manually follows the challenge→authorize→callback→landing 302 chain (the TestServer client doesn't
    /// auto-redirect; cookies — correlation, nonce, auth — flow via the CookieContainer). On the dev Test
    /// authorize hop it appends a <c>roles</c> hint (test-only; in real OAuth custom params aren't forwarded).
    /// </summary>
    private async Task DriveLogin(HttpClient client, string challenge, string injectRole,
        string? browserBaseUrl = null, string? callbackHost = null)
    {
        var url = new Uri((browserBaseUrl ?? _fx.BaseUrl) + challenge);
        var chain = new System.Collections.Generic.List<string>();
        for (var hop = 0; hop < 12; hop++)
        {
            if (url.AbsolutePath.EndsWith("/authorize", StringComparison.Ordinal))
                url = new Uri(QueryHelpers.AddQueryString(url.ToString(), "roles", injectRole));

            HttpResponseMessage resp;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (callbackHost is not null && url.AbsolutePath.EndsWith("/callback", StringComparison.Ordinal))
                    request.Headers.Host = callbackHost;
                resp = await client.SendAsync(request, TestContext.Current.CancellationToken);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"request failed at {url}. chain: {string.Join(" -> ", chain)}{Environment.NewLine}{_fx.Diagnostics}",
                    exception);
            }
            using (resp)
            {
                chain.Add($"{(int)resp.StatusCode} {url.PathAndQuery}");
                if ((int)resp.StatusCode is < 300 or >= 400)
                {
                    var body = (int)resp.StatusCode >= 400
                        ? await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
                        : "";
                    resp.StatusCode.Should().Be(HttpStatusCode.OK,
                        $"the round-trip should land on the return URL. chain:\n  {string.Join("\n  ", chain)}\nbody: {body[..Math.Min(body.Length, 800)]}");
                    return;
                }

                var loc = resp.Headers.Location ?? throw new InvalidOperationException(
                    $"redirect with no Location at {url}. chain: {string.Join(" -> ", chain)}");
                url = loc.IsAbsoluteUri ? loc : new Uri(url, loc);
            }
        }
        throw new InvalidOperationException("redirect chain did not terminate within 12 hops: " + string.Join(" -> ", chain));
    }

    private static async Task<JsonElement> ReadWhoAmI(HttpClient client)
    {
        var resp = await client.GetAsync("/e2e/whoami");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task AssertLinked(string userId, string provider)
    {
        var store = _fx.Services.GetRequiredService<IExternalIdentityStore>();
        var links = await store.GetByUser(userId);
        links.Select(l => l.Provider).Should().Contain(provider);
    }
}
