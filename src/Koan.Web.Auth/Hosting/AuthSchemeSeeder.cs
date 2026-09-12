using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Koan.Web.Auth.Domain;
using Koan.Web.Auth.Extensions;
using Koan.Web.Auth.Infrastructure;
using Koan.Web.Auth.Options;
using Koan.Web.Auth.Providers;

namespace Koan.Web.Auth.Hosting;

/// <summary>
/// WEB-0071 — registers a maintained ASP.NET <see cref="OAuthHandler{TOptions}"/> /
/// <see cref="OpenIdConnectHandler"/> scheme per effective provider at startup (post-build, full DI),
/// so id_token validation, nonce, PKCE, state, and correlation are owned by the framework rather than
/// hand-rolled. The provider set is compiled once by <see cref="AuthProviderPlan"/> from connector definitions and
/// configuration; this seeder realizes only that plan's eligible routes.
/// </summary>
/// <remarks>
/// CRITICAL: <see cref="IOptionsMonitorCache{TOptions}.TryAdd"/> caches the instance verbatim and bypasses
/// the configure/post-configure pipeline, so we run the matching <c>*PostConfigureOptions.PostConfigure</c>
/// MANUALLY before seeding — otherwise <c>StateDataFormat</c>/<c>DataProtectionProvider</c>/<c>Backchannel</c>
/// stay null and the handler NPEs at challenge. The three application-policy controls the maintained handler
/// does not own (claim mapping, external-identity link, return-url allow-list) are ported into the handler
/// events here / the challenge entry-point.
/// </remarks>
internal static class AuthSchemeSeeder
{
    private const string ExtraClaimPermission = "Koan.permission";

    /// <summary>Seed schemes from the host-owned provider plan.</summary>
    public static void Seed(IServiceProvider sp)
    {
        var plan = sp.GetRequiredService<AuthProviderPlan>();
        var schemes = sp.GetRequiredService<IAuthenticationSchemeProvider>();
        var dp = sp.GetRequiredService<IDataProtectionProvider>();
        var oauthCache = sp.GetRequiredService<IOptionsMonitorCache<OAuthOptions>>();
        var oidcCache = sp.GetRequiredService<IOptionsMonitorCache<OpenIdConnectOptions>>();
        var log = sp.GetService<ILoggerFactory>()?.CreateLogger("Koan.Web.Auth.AuthSchemeSeeder");

        var oauthPost = new OAuthPostConfigureOptions<OAuthOptions, OAuthHandler<OAuthOptions>>(dp);
        var oidcPost = new OpenIdConnectPostConfigureOptions(dp);

        foreach (var route in plan.Routes.Where(static route => route.Info.Eligible))
        {
            var id = route.Info.Id;
            var cfg = route.Options;
            var type = route.Info.Protocol;
            // Idempotent: another seeding pass (or a statically-registered scheme) already owns this id.
            if (schemes.GetSchemeAsync(id).GetAwaiter().GetResult() is not null) continue;

            if (type == AuthProviderProtocols.OAuth2)
            {
                var opts = BuildOAuthOptions(id, cfg);
                oauthPost.PostConfigure(id, opts);   // MUST precede TryAdd (see remarks)
                oauthCache.TryAdd(id, opts);
                schemes.AddScheme(new AuthenticationScheme(id, cfg.DisplayName ?? id, typeof(OAuthHandler<OAuthOptions>)));
            }
            else if (type == AuthProviderProtocols.Oidc)
            {
                var opts = BuildOidcOptions(id, cfg, sp);
                oidcPost.PostConfigure(id, opts);    // MUST precede TryAdd (see remarks)
                oidcCache.TryAdd(id, opts);
                schemes.AddScheme(new AuthenticationScheme(id, cfg.DisplayName ?? id, typeof(OpenIdConnectHandler)));
            }

            if (schemes.GetSchemeAsync(id).GetAwaiter().GetResult() is null)
            {
                throw new InvalidOperationException(
                    $"Koan Web Auth provider '{id}' is eligible for protocol '{route.Info.Protocol}', but no ASP.NET authentication scheme is registered. " +
                    $"The connector must register scheme '{id}' before host startup; reference/configure its handler or disable/remove the provider intent.");
            }

            log?.LogDebug("Koan.Web.Auth: seeded {Type} scheme for provider {Provider}", type, id);
        }
    }

    private static OAuthOptions BuildOAuthOptions(string id, ProviderOptions cfg)
    {
        var o = new OAuthOptions
        {
            SignInScheme = AuthenticationExtensions.CookieScheme,
            // Browser-facing: relative is fine (the browser resolves it). Server-facing endpoints get resolved.
            AuthorizationEndpoint = cfg.AuthorizationEndpoint ?? "",
            TokenEndpoint = ResolveServerAbsolute(cfg.TokenEndpoint) ?? "",
            UserInformationEndpoint = ResolveServerAbsolute(cfg.UserInfoEndpoint) ?? "",
            ClientId = cfg.ClientId ?? "",
            ClientSecret = cfg.ClientSecret ?? "",
            CallbackPath = $"/auth/{id}/callback",
            UsePkce = true, // parity gap: the hand-rolled OAuth2 path shipped NO PKCE
        };
        // WEB-0071: broaden the correlation cookie Path to root so it is returned on the templated
        // /auth/{id}/callback (the default narrows it to the callback path → "Correlation failed").
        o.CorrelationCookie.Path = "/";
        // Cookie storability follows the app's FRONT-CHANNEL request scheme (per request), not the provider's
        // back-channel endpoint — see RequestSchemeAdaptiveCookieBuilder.
        o.CorrelationCookie = RequestSchemeAdaptiveCookieBuilder.Wrap(o.CorrelationCookie);
        if (cfg.Scopes is { Length: > 0 })
        {
            o.Scope.Clear();
            foreach (var s in cfg.Scopes) o.Scope.Add(s);
        }
        o.Events = new OAuthEvents
        {
            OnRedirectToAuthorizationEndpoint = ctx =>
            {
                // Self-hosted dev IdP: its token/userinfo endpoints are relative and reachable only at the app's own
                // host. Resolve them via the back-channel base from the LIVE request host (forwarded-aware), set once
                // before the callback's token exchange. Absolute provider endpoints ignore BaseAddress, so this is a
                // no-op for real providers. (ASPNETCORE_URLS is unreliable in a container — the request is the truth.)
                var backchannel = ctx.Options.Backchannel;
                if (backchannel.BaseAddress is null)
                {
                    var req = ctx.HttpContext.Request;
                    try { backchannel.BaseAddress = new Uri($"{req.Scheme}://{req.Host}"); }
                    catch { /* best effort — absolute endpoints don't need it */ }
                }
                var url = ctx.RedirectUri;
                if (ctx.Properties.Items.TryGetValue("prompt", out var prompt) && !string.IsNullOrWhiteSpace(prompt))
                    url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(url, "prompt", prompt!);
                ctx.Response.Redirect(url);
                return Task.CompletedTask;
            },
            OnCreatingTicket = async ctx =>
            {
                var json = "{}";
                var user = new JObject();
                if (!string.IsNullOrEmpty(ctx.Options.UserInformationEndpoint))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ctx.AccessToken);
                    using var resp = await ctx.Backchannel.SendAsync(req, ctx.HttpContext.RequestAborted);
                    resp.EnsureSuccessStatusCode();
                    json = await resp.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted);
                    user = JObject.Parse(json);
                }
                ApplyClaims(ctx.Identity!, user);
                await LinkExternalIdentityAsync(ctx.HttpContext, ctx.Scheme.Name, user, json);
            }
        };
        return o;
    }

    private static OpenIdConnectOptions BuildOidcOptions(string id, ProviderOptions cfg, IServiceProvider sp)
    {
        // A RELATIVE authority (the self-hosted Test IdP's /.testoauth, served by this same app) is resolved
        // per-request from the live host via RequestHostOidcConfigurationManager (boot-time ASPNETCORE_URLS is
        // unreliable, and the discovery doc's browser-facing endpoints must carry the public host). Real providers
        // ship an absolute authority used directly.
        var selfHosted = !string.IsNullOrEmpty(cfg.Authority)
            && !Uri.IsWellFormedUriString(cfg.Authority, UriKind.Absolute);
        var authority = selfHosted ? "" : (ResolveServerAbsolute(cfg.Authority) ?? cfg.Authority ?? "");
        var o = new OpenIdConnectOptions
        {
            SignInScheme = AuthenticationExtensions.CookieScheme,
            Authority = authority,
            // Allow http metadata only for a non-https (dev/loopback) authority; production https stays validated.
            RequireHttpsMetadata = !selfHosted && authority.StartsWith("https", StringComparison.OrdinalIgnoreCase),
            ClientId = cfg.ClientId ?? "",
            ClientSecret = cfg.ClientSecret,
            ResponseType = "code",
            // Align with the GET authorization-response redirect (the dev Test IdP doesn't form_post).
            ResponseMode = "query",
            UsePkce = true,
            SaveTokens = false,
            CallbackPath = $"/auth/{id}/callback",
            GetClaimsFromUserInfoEndpoint = true, // roles/perms/extras come from userinfo (UserInfoMapper)
        };
        // WEB-0071: broaden the correlation + nonce cookie Path to root so they return on the templated callback.
        o.CorrelationCookie.Path = "/";
        o.NonceCookie.Path = "/";
        // Both adapt to the front-channel request scheme per request (see RequestSchemeAdaptiveCookieBuilder) — so a
        // real https provider behind a plain-http dev host still gets storable (Lax) cookies.
        o.CorrelationCookie = RequestSchemeAdaptiveCookieBuilder.Wrap(o.CorrelationCookie);
        o.NonceCookie = RequestSchemeAdaptiveCookieBuilder.Wrap(o.NonceCookie);
        if (selfHosted)
        {
            var http = sp.GetService<IHttpContextAccessor>()
                ?? throw new InvalidOperationException(
                    "Koan.Web.Auth: a self-hosted OIDC provider (relative authority) requires IHttpContextAccessor; " +
                    "ensure services.AddHttpContextAccessor() is registered.");
            var server = sp.GetRequiredService<IServer>();
            o.ConfigurationManager = new RequestHostOidcConfigurationManager(
                cfg.Authority!,
                http,
                o,
                () => ResolveSelfHostedBackchannelBase(server));
        }
        if (cfg.Scopes is { Length: > 0 })
        {
            o.Scope.Clear();
            foreach (var s in cfg.Scopes) o.Scope.Add(s);
        }
        else
        {
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.Scope.Add("email");
        }
        o.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = ctx =>
            {
                if (ctx.Properties.Items.TryGetValue("prompt", out var prompt) && !string.IsNullOrWhiteSpace(prompt))
                    ctx.ProtocolMessage.Prompt = prompt;
                return Task.CompletedTask;
            },
            // id_token signature/issuer/audience/nonce/expiry are validated by the handler before this fires.
            OnUserInformationReceived = async ctx =>
            {
                if (ctx.Principal?.Identity is not ClaimsIdentity identity) return;
                var json = ctx.User.RootElement.GetRawText();
                var user = JObject.Parse(json);
                ApplyClaims(identity, user);
                await LinkExternalIdentityAsync(ctx.HttpContext, ctx.Scheme.Name, user, json);
            }
        };
        return o;
    }

    /// <summary>
    /// Port of <see cref="UserInfoMapper"/> + the sub/name/avatar mapping the hand-rolled callback did.
    /// Fail-closed: a missing identifier does NOT mint a synthetic "user" id (that collapsed anonymous logins).
    /// </summary>
    private static void ApplyClaims(ClaimsIdentity identity, JObject user)
    {
        var sub = (string?)user["sub"] ?? (string?)user["id"];
        var name = (string?)user["name"] ?? (string?)user["username"];
        var avatar = (string?)user["avatar"] ?? (string?)user["picture"];

        if (string.IsNullOrWhiteSpace(sub))
        {
            throw new InvalidOperationException(
                "Koan Web Auth rejected the external identity because userinfo contained neither 'sub' nor 'id'.");
        }

        if (!identity.HasClaim(c => c.Type == ClaimTypes.NameIdentifier))
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
        if (!string.IsNullOrWhiteSpace(name) && !identity.HasClaim(c => c.Type == ClaimTypes.Name))
            identity.AddClaim(new Claim(ClaimTypes.Name, name));
        if (!string.IsNullOrWhiteSpace(avatar) && !identity.HasClaim(c => c.Type == "avatar"))
            identity.AddClaim(new Claim("avatar", avatar));

        var (roles, perms, extras) = UserInfoMapper.Map(user);
        foreach (var r in roles)
            if (!identity.HasClaim(ClaimTypes.Role, r)) identity.AddClaim(new Claim(ClaimTypes.Role, r));
        foreach (var p in perms)
            if (!identity.HasClaim(ExtraClaimPermission, p)) identity.AddClaim(new Claim(ExtraClaimPermission, p));
        foreach (var kv in extras)
            if (!identity.HasClaim(kv.Key, kv.Value)) identity.AddClaim(new Claim(kv.Key, kv.Value));
    }

    /// <summary>Persist the external-identity link before sign-in completes; any failure rejects the authentication flow.</summary>
    private static async Task LinkExternalIdentityAsync(HttpContext http, string provider, JObject user, string claimsJson)
    {
        var sub = (string?)user["sub"] ?? (string?)user["id"];
        if (string.IsNullOrWhiteSpace(sub))
        {
            throw new InvalidOperationException(
                $"Koan Web Auth rejected provider '{provider}' because userinfo contained neither 'sub' nor 'id'.");
        }

        var store = http.RequestServices.GetService<IExternalIdentityStore>();
        if (store is null) return;
        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sub)));
        await store.Link(new ExternalIdentity
        {
            UserId = sub,
            Provider = provider,
            ProviderKeyHash = keyHash,
            ClaimsJson = claimsJson
        }, http.RequestAborted);
    }

    /// <summary>
    /// The maintained handler's Backchannel calls token/userinfo/discovery SERVER-side, so a relative endpoint must
    /// be made in-network absolute. Delegates to <see cref="ServerAddressResolver"/>, which understands Kestrel's
    /// wildcard bind forms (<c>http://+:8080</c>) that <see cref="Uri"/> cannot parse. Absolute endpoints pass through.
    /// </summary>
    private static string? ResolveServerAbsolute(string? endpoint) => ServerAddressResolver.ToAbsolute(
        endpoint,
        Environment.GetEnvironmentVariable("ASPNETCORE_URLS"),
        Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS"),
        Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));

    private static string? ResolveSelfHostedBackchannelBase(IServer server)
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var liveAddresses = addresses is { Count: > 0 }
            ? string.Join(';', addresses)
            : null;
        return ServerAddressResolver.ResolveBase(
            liveAddresses ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS"),
            Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS"),
            Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS"));
    }
}
