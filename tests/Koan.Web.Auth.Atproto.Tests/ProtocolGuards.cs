using System.Net;
using CarpaNet.OAuth;
using CarpaNet.OAuth.Storage;
using Koan.Web.Auth.Connector.Atproto.Hosting;
using Koan.Web.Auth.Connector.Atproto.Options;
using Koan.Web.Auth.Connector.Atproto.Protocol;
using Koan.Web.Auth.Connector.Atproto.Storage;
using Koan.Web.Auth.Options;
using Koan.Web.Auth.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Koan.Web.Auth.Atproto.Tests;

public sealed class ProtocolGuards
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Server_first_PAR_omits_login_hint_and_retains_protected_state_through_nonce_retry(bool hasResourceMetadata)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var provider = builder.Build();
        string origin = "";
        IFormCollection? pushed = null;
        var attempts = 0;
        provider.MapGet("/.well-known/oauth-protected-resource", () => hasResourceMetadata
            ? Results.Json(new { resource = origin, authorization_servers = new[] { origin } }) : Results.NotFound());
        provider.MapGet("/.well-known/oauth-authorization-server", () => Results.Json(new
        {
            issuer = origin, authorization_endpoint = origin + "/authorize", token_endpoint = origin + "/token",
            pushed_authorization_request_endpoint = origin + "/par", response_types_supported = new[] { "code" },
            code_challenge_methods_supported = new[] { "S256" }, dpop_signing_alg_values_supported = new[] { "ES256" }
        }));
        provider.MapPost("/par", async (HttpContext context) =>
        {
            pushed = await context.Request.ReadFormAsync();
            Assert.True(context.Request.Headers.ContainsKey("DPoP"));
            if (++attempts == 1)
            {
                context.Response.Headers["DPoP-Nonce"] = "fixture-nonce";
                return Results.Json(new { error = "use_dpop_nonce" }, statusCode: 400);
            }
            return Results.Json(new { request_uri = "urn:fixture:request", expires_in = 60 }, statusCode: 201);
        });
        await provider.StartAsync();
        origin = provider.Urls.Single();
        var directory = Path.Combine(Path.GetTempPath(), "koan-atproto-" + Guid.NewGuid().ToString("N"));
        var settings = Microsoft.Extensions.Options.Options.Create(new AtprotoOptions { SessionDirectory = directory, DevelopmentAllowedOrigins = [origin] });
        var environment = new EnvironmentStub("Development");
        using var store = new ProtectedAtprotoStore(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys"))), settings, environment);
        using var network = new AtprotoHttp(settings, environment);
        var auth = new AuthOptions();
        auth.Providers["atproto"] = new ProviderOptions { ClientId = "http://localhost?redirect_uri=http%3A%2F%2F127.0.0.1%3A5220%2Fauth%2Fatproto%2Fcallback" };
        var sessions = new AtprotoSessions(store, network, settings, Microsoft.Extensions.Options.Options.Create(auth), new ServerCatalog(), environment);
        var redirect = await sessions.StartServer(origin, new AuthenticationProperties(), "protected-browser-correlation", CancellationToken.None);
        Assert.Equal(2, attempts);
        Assert.NotNull(pushed);
        Assert.False(pushed.ContainsKey("login_hint"));
        Assert.Equal("S256", pushed["code_challenge_method"].ToString());
        Assert.Equal("atproto", pushed["scope"].ToString());
        var pending = store.Peek(pushed["state"].ToString());
        Assert.NotNull(pending);
        Assert.Equal(origin, pending.Issuer);
        Assert.Contains("protected-browser-correlation", pending.AppState);
        AtprotoSessions.RequirePar(redirect, auth.Providers["atproto"].ClientId!);
        await provider.StopAsync();
    }

    [Fact]
    public void Server_first_callback_requires_the_subjects_resolved_issuer_and_pds()
    {
        AtprotoSessions.RequireSubjectBinding(null, null, "https://entry.example", "did:plc:alice", "https://pds.example", "https://entry.example", "https://pds.example/");
        Assert.Throws<InvalidOperationException>(() => AtprotoSessions.RequireSubjectBinding(null, null,
            "https://entry.example", "did:plc:alice", "https://pds.example", "https://other.example", "https://pds.example"));
        Assert.Throws<InvalidOperationException>(() => AtprotoSessions.RequireSubjectBinding(null, null,
            "https://entry.example", "did:plc:alice", "https://pds.example", "https://entry.example", "https://entry.example"));
        Assert.Throws<InvalidOperationException>(() => AtprotoSessions.RequireSubjectBinding("did:plc:bob", "https://pds.example",
            "https://entry.example", "did:plc:alice", "https://pds.example", "https://entry.example", "https://pds.example"));
    }

    [Theory]
    [InlineData("127.0.0.1")][InlineData("10.1.1.1")][InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")][InlineData("::1")][InlineData("::ffff:127.0.0.1")]
    [InlineData("fe80::1")][InlineData("fc00::1")][InlineData("2001:db8::1")][InlineData("2002:7f00:1::")]
    public void Private_and_translation_addresses_are_not_public(string address) => Assert.False(AtprotoHttp.PublicAddress(IPAddress.Parse(address)));

    [Theory]
    [InlineData("1.1.1.1")][InlineData("8.8.8.8")][InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_eligible(string address) => Assert.True(AtprotoHttp.PublicAddress(IPAddress.Parse(address)));

    [Fact]
    public void Development_exceptions_are_rejected_in_production()
    {
        var options = new AtprotoOptions { DevelopmentAllowedOrigins = ["http://localhost:2582"] };
        var validator = new AtprotoProtocol(Microsoft.Extensions.Options.Options.Create(options), new EnvironmentStub("Production"));
        Assert.Contains(validator.Validate("atproto", Provider()), x => x.Contains("require Development"));
    }

    [Fact]
    public void Unsupported_provider_overlay_is_corrective()
    {
        var validator = new AtprotoProtocol(Microsoft.Extensions.Options.Options.Create(new AtprotoOptions()), new EnvironmentStub("Production"));
        var errors = validator.Validate("atproto", new ProviderOptions { ClientId = "https://app.example/auth/atproto/client-metadata.json", CallbackPath = "/different", ClientSecret = "never-in-error" });
        Assert.Contains(errors, x => x.Contains("CallbackPath"));
        Assert.DoesNotContain(errors, x => x.Contains("never-in-error"));
    }

    [Fact]
    public void Public_configuration_accepts_no_secret_or_fixed_issuer()
    {
        var validator = new AtprotoProtocol(Microsoft.Extensions.Options.Options.Create(new AtprotoOptions()), new EnvironmentStub("Production"));
        Assert.Empty(validator.Validate("atproto", Provider()));
    }

    [Fact]
    public void Removed_permission_ceiling_rejects_an_older_stored_grant()
    {
        Assert.Throws<InvalidOperationException>(() => AtprotoSessions.RequireScopes("atproto transition:generic.write", ["atproto"]));
        AtprotoSessions.RequireScopes("atproto", ["atproto", "transition:generic.write"]);
    }

    [Theory]
    [InlineData("atproto")]
    [InlineData("atproto space:read")]
    public void Callback_rejects_a_partial_requested_grant(string granted)
    {
        var error = Assert.Throws<AtprotoMissingScopesException>(() =>
            AtprotoSessions.RequireRequestedScopes(granted, ["atproto", "space:read", "space:write"]));
        Assert.DoesNotContain("space:", error.Message);
    }

    [Fact]
    public void Callback_accepts_all_requested_scopes_regardless_of_order_or_duplicates()
        => AtprotoSessions.RequireRequestedScopes("space:write atproto space:read atproto",
            ["atproto", "space:read", "space:write", "atproto"]);

    [Fact]
    public void Callback_still_rejects_unrequested_permissions()
        => Assert.Throws<InvalidOperationException>(() =>
            AtprotoSessions.RequireRequestedScopes("atproto space:write", ["atproto"]));

    [Fact]
    public void Ordinary_sign_in_need_not_receive_every_optional_declared_scope()
    {
        AtprotoSessions.RequireScopes("atproto", ["atproto", "space:read", "space:write"]);
        AtprotoSessions.RequireRequestedScopes("atproto", ["atproto"]);
    }

    [Fact]
    public void A_default_sign_in_preserves_a_valid_broader_grant()
    {
        var scopes = AtprotoSessions.ScopesForSignIn(false, ["atproto"], "atproto space:read space:write",
            ["atproto", "space:read", "space:write"]);
        Assert.Equal(["atproto", "space:read", "space:write"], scopes);
    }

    [Fact]
    public void An_explicit_connection_scope_is_not_relaxed_to_a_previous_grant()
    {
        var scopes = AtprotoSessions.ScopesForSignIn(true, ["atproto", "space:read"], "atproto space:read space:write",
            ["atproto", "space:read", "space:write"]);
        Assert.Equal(["atproto", "space:read"], scopes);
    }

    [Fact]
    public void An_old_broader_grant_cannot_survive_a_reduced_declared_ceiling()
        => Assert.Throws<InvalidOperationException>(() =>
            AtprotoSessions.ScopesForSignIn(false, ["atproto"], "atproto space:read", ["atproto"]));

    [Fact]
    public async Task Rejected_downscope_keeps_the_previous_durable_session()
    {
        var directory = Path.Combine(Path.GetTempPath(), "koan-atproto-" + Guid.NewGuid().ToString("N"));
        var options = Microsoft.Extensions.Options.Options.Create(new AtprotoOptions { SessionDirectory = directory });
        var protector = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")));
        var environment = new EnvironmentStub("Development");
        const string did = "did:plc:test";
        using (var durable = new ProtectedAtprotoStore(protector, options, environment))
        {
            await durable.StoreAsync(did, new OAuthSessionData { TokenSet = new TokenSet
                { Sub = did, Scope = "atproto space:read space:write", AccessToken = "previous-test-token" } });
            var staged = new VerifiedSessionStore(durable);
            await staged.StoreAsync(did, new OAuthSessionData { TokenSet = new TokenSet
                { Sub = did, Scope = "atproto", AccessToken = "downscoped-test-token" } });
            await Assert.ThrowsAsync<AtprotoMissingScopesException>(async () =>
            {
                AtprotoSessions.RequireRequestedScopes(staged.GrantedScope, ["atproto", "space:read", "space:write"]);
                await staged.Commit(did, default);
            });
            // The rejected SDK client's cleanup must not delete a previously working session either.
            await staged.DeleteAsync(did);
            Assert.Equal("previous-test-token", (await durable.GetAsync(did))!.TokenSet.AccessToken);
        }
        using var restored = new ProtectedAtprotoStore(protector, options, environment);
        Assert.Equal("previous-test-token", (await restored.GetAsync(did))!.TokenSet.AccessToken);
        Assert.Equal("atproto space:read space:write", (await restored.GetAsync(did))!.TokenSet.Scope);
    }

    [Fact]
    public void Failed_PAR_cannot_downgrade_to_direct_authorization()
    {
        Assert.Throws<InvalidOperationException>(() => AtprotoSessions.RequirePar("https://issuer.example/auth?client_id=client&response_type=code&state=state", "client"));
        AtprotoSessions.RequirePar("https://issuer.example/auth?client_id=client&request_uri=urn%3Atest%3Apushed", "client");
    }

    [Fact]
    public async Task State_is_single_use_after_storage_restart_and_sessions_stage_before_verification()
    {
        var directory = Path.Combine(Path.GetTempPath(), "koan-atproto-" + Guid.NewGuid().ToString("N"));
        var options = Microsoft.Extensions.Options.Options.Create(new AtprotoOptions { SessionDirectory = directory });
        var protector = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")));
        var environment = new EnvironmentStub("Development");
        using (var first = new ProtectedAtprotoStore(protector, options, environment))
            await first.StoreAsync("state", new OAuthStateData { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1), AppState = "browser" });
        using var restored = new ProtectedAtprotoStore(protector, options, environment);
        Assert.Equal("browser", (await restored.ConsumeAsync("state"))!.AppState);
        Assert.Null(await restored.ConsumeAsync("state"));
        var staged = new VerifiedSessionStore(restored);
        await staged.StoreAsync("did:plc:test", new OAuthSessionData { TokenSet = new TokenSet { Sub = "did:plc:test", AccessToken = "not-public" } });
        Assert.Null(await restored.GetAsync("did:plc:test"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => staged.Commit("did:plc:other", default));
        await staged.Commit("did:plc:test", default);
        Assert.NotNull(await restored.GetAsync("did:plc:test"));
        Assert.DoesNotContain("not-public", await File.ReadAllTextAsync(Path.Combine(directory, "protocol.protected")));
    }

    private static ProviderOptions Provider() => new() { ClientId = "https://app.example/auth/atproto/client-metadata.json", Scopes = ["atproto"] };

    [Fact]
    public void Fixture_PLC_selection_never_captures_public_or_unknown_identities()
    {
        var options = new AtprotoOptions { DevelopmentPlcDirectory = "http://localhost:2582",
            DevelopmentHandles = new() { ["owner.test"] = "did:plc:aaaaaaaaaaaaaaaaaaaaaaaa" } };
        Assert.Equal("http://localhost:2582", options.DirectoryFor("owner.test", true));
        Assert.Equal("http://localhost:2582", options.DirectoryFor("did:plc:aaaaaaaaaaaaaaaaaaaaaaaa", true));
        Assert.Equal("https://plc.directory", options.DirectoryFor("leo.sylin.org", true));
        Assert.Equal("https://plc.directory", options.DirectoryFor("did:plc:bbbbbbbbbbbbbbbbbbbbbbbb", true));
        Assert.Equal("https://plc.directory", options.DirectoryFor("owner.test", false));
    }

    [Fact]
    public void Container_connection_host_applies_only_to_exact_development_origins()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new AtprotoOptions {
            DevelopmentAllowedOrigins = ["http://localhost:2583"], DevelopmentConnectHost = "host.docker.internal" });
        using var dev = new AtprotoHttp(options, new EnvironmentStub("Development"));
        using var prod = new AtprotoHttp(options, new EnvironmentStub("Production"));
        Assert.Equal("host.docker.internal", dev.ConnectionHost(new Uri("http://localhost:2583/xrpc/test")));
        Assert.Equal("public.example", dev.ConnectionHost(new Uri("https://public.example/xrpc/test")));
        Assert.Equal("localhost", dev.ConnectionHost(new Uri("http://localhost:2584/xrpc/test")));
        Assert.Equal("localhost", prod.ConnectionHost(new Uri("http://localhost:2583/xrpc/test")));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Production_rejects_each_development_routing_option(bool directory, bool connection)
    {
        var options = new AtprotoOptions { DevelopmentPlcDirectory = directory ? "http://localhost:2582" : null,
            DevelopmentConnectHost = connection ? "host.docker.internal" : null };
        var validator = new AtprotoProtocol(Microsoft.Extensions.Options.Options.Create(options), new EnvironmentStub("Production"));
        Assert.Contains(validator.Validate("atproto", Provider()), x => x.Contains("require Development"));
    }

    [Fact]
    public void Invalid_development_routes_are_corrective_configuration_errors()
    {
        var options = new AtprotoOptions { DevelopmentPlcDirectory = "http://unlisted.example", DevelopmentConnectHost = "http://proxy.example/path" };
        var validator = new AtprotoProtocol(Microsoft.Extensions.Options.Options.Create(options), new EnvironmentStub("Development"));
        var errors = validator.Validate("atproto", Provider());
        Assert.Contains(errors, x => x.Contains("DevelopmentPlcDirectory"));
        Assert.Contains(errors, x => x.Contains("DevelopmentConnectHost"));
    }
    private sealed class EnvironmentStub(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "AtprotoTests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class ServerCatalog : IAuthProviderCatalog
    {
        public IReadOnlyList<AuthProviderInfo> Providers { get; } = [new("atproto", "AT Protocol", "atproto", "enabled", true, true, false, 200, "Local proof", null, null, ["atproto"], "/auth/atproto/challenge")];
        public AuthProviderInfo? Default => Providers[0];
        public AuthProviderInfo? Find(string? id) => id == "atproto" ? Default : null;
    }
}
