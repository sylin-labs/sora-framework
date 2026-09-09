using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Koan.Web.Auth.Connector.Test.Infrastructure;
using Koan.Web.Auth.Connector.Test.Options;
using System.Net;

namespace Koan.Web.Auth.Connector.Test.Controllers;

public sealed class AuthorizeController(IOptionsSnapshot<TestProviderOptions> opts, DevTokenStore store, IHostEnvironment env, ILogger<AuthorizeController> logger) : ControllerBase
{
  [HttpGet(Constants.Routes.Authorize)]
    public IActionResult Authorize([FromQuery] string response_type, [FromQuery] string client_id, [FromQuery] string redirect_uri, [FromQuery] string? scope, [FromQuery] string? state, [FromQuery] string? code_challenge, [FromQuery] string? code_challenge_method, [FromQuery] string? prompt, [FromQuery] string? nonce)
    {
        var o = opts.Value;
        if (!o.IsActive(env)) return NotFound();
        if (string.IsNullOrWhiteSpace(response_type) || !string.Equals(response_type, "code", StringComparison.OrdinalIgnoreCase)) return BadRequest("response_type must be 'code'");
        if (string.IsNullOrWhiteSpace(client_id) || string.IsNullOrWhiteSpace(redirect_uri)) return BadRequest("client_id and redirect_uri are required");
        if (!string.Equals(client_id, o.ClientId, StringComparison.Ordinal)) return Unauthorized();

    // If prompt requests a fresh login/selection, ignore any existing cookie and show the login UI.
    var forceLogin = !string.IsNullOrWhiteSpace(prompt) && (string.Equals(prompt, "login", StringComparison.OrdinalIgnoreCase) || string.Equals(prompt, "select_account", StringComparison.OrdinalIgnoreCase));
    if (forceLogin)
    {
      try { Response.Cookies.Append(Constants.CookieUser, "", new CookieOptions { Expires = DateTimeOffset.UnixEpoch, Path = "/", SameSite = SameSiteMode.Lax, HttpOnly = false, Secure = Request.IsHttps }); } catch { /* ignore */ }
    }

    // Render simple HTML form when no user cookie or when forced via prompt.
  if (forceLogin || !Request.Cookies.TryGetValue(Constants.CookieUser, out var userCookie) || string.IsNullOrWhiteSpace(userCookie))
        {
      // Serve a dedicated static HTML to keep SoC clean
            const string url = Constants.Routes.Login;
            var queryParams = new Dictionary<string, string?>();
            queryParams["client_id"] = client_id;
            queryParams["redirect_uri"] = redirect_uri;
            if (!string.IsNullOrWhiteSpace(scope)) queryParams["scope"] = scope;
            if (!string.IsNullOrWhiteSpace(state)) queryParams["state"] = state;
            if (!string.IsNullOrWhiteSpace(code_challenge)) queryParams["code_challenge"] = code_challenge;
            if (!string.IsNullOrWhiteSpace(code_challenge_method)) queryParams["code_challenge_method"] = code_challenge_method;
            if (!string.IsNullOrWhiteSpace(nonce)) queryParams["nonce"] = nonce;
            var loginUrl = QueryHelpers.AddQueryString(url, queryParams);
            return Redirect(loginUrl);
        }

        // At this point userCookie is present and non-empty.
    // Enforce PKCE method if provided: only S256 is allowed
    if (!string.IsNullOrWhiteSpace(code_challenge_method) && !string.Equals(code_challenge_method, "S256", StringComparison.Ordinal))
    {
      logger.LogWarning("TestProvider authorize: unsupported code_challenge_method '{Method}' for client_id '{ClientId}'", code_challenge_method, client_id);
      return BadRequest("unsupported_code_challenge_method");
    }

    // Validate redirect_uri against whitelist
    if (!Uri.TryCreate(redirect_uri, UriKind.Absolute, out var redirect))
    {
      logger.LogWarning("TestProvider authorize: invalid redirect_uri format for client_id '{ClientId}'", client_id);
      return BadRequest("invalid_redirect_uri");
    }

    bool isAllowed = false;
    if (o.AllowedRedirectUris is { Length: > 0 })
    {
      foreach (var u in o.AllowedRedirectUris)
      {
        if (string.IsNullOrWhiteSpace(u)) continue;
        if (Uri.TryCreate(u, UriKind.Absolute, out var allowedAbs))
        {
          if (string.Equals(redirect.AbsoluteUri, allowedAbs.AbsoluteUri, StringComparison.Ordinal)) { isAllowed = true; break; }
        }
        else if (u.StartsWith("/", StringComparison.Ordinal))
        {
          if (string.Equals(redirect.AbsolutePath, u, StringComparison.Ordinal)) { isAllowed = true; break; }
        }
      }
    }
    else
    {
      // Sane defaults (DX): if no explicit whitelist is configured, allow the standard callback used by the built-in Test provider.
      // This enables zero-config dev flows while still constraining to the expected callback shape.
      // Accepted when AllowedRedirectUris is empty:
      // - Any absolute URL whose path is exactly "/auth/test/callback" (host/port agnostic)
      // - The relative path "/auth/test/callback"
      // Accept any provider callback shape /auth/{id}/callback (covers test + test-oidc); dev-only convenience.
      var path = redirect.AbsolutePath;
      if (path.StartsWith("/auth/", StringComparison.Ordinal) && path.EndsWith("/callback", StringComparison.Ordinal))
      {
        isAllowed = true;
      }
    }
    if (!isAllowed)
    {
      logger.LogWarning("TestProvider authorize: redirect_uri not allowed for client_id '{ClientId}' host '{Host}'", client_id, redirect.Host);
      return BadRequest("unauthorized_redirect_uri");
    }

    // At this point redirect is validated; proceed to issue code and construct safe redirect
    var decoded = Uri.UnescapeDataString(userCookie ?? "");
    var parts = decoded.Split('|');
    var profile = new UserProfile(parts.ElementAtOrDefault(0) ?? "dev", parts.ElementAtOrDefault(1) ?? "dev@example.com", null);
    var (roles, perms, extraClaims) = ParseExtras(o);
    var isOpenId = !string.IsNullOrWhiteSpace(scope) && scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.OrdinalIgnoreCase);
    var issuer = isOpenId
        ? new Uri($"{Request.Scheme}://{Request.Host}").GetLeftPart(UriPartial.Authority) + Constants.Routes.Base
        : null;
    var code = store.IssueCode(profile, TimeSpan.FromMinutes(5), code_challenge, roles, perms, extraClaims, nonce, isOpenId, issuer);
    var uri = new UriBuilder(redirect);
    var existingQuery = QueryHelpers.ParseQuery(uri.Query);
    var newQuery = existingQuery.ToDictionary(kvp => kvp.Key, kvp => (string?)kvp.Value.ToString());
    newQuery["code"] = code;
    if (!string.IsNullOrWhiteSpace(state)) newQuery["state"] = state;
    uri.Query = QueryHelpers.AddQueryString("", newQuery).TrimStart('?');
    logger.LogDebug("TestProvider authorize: redirecting with authorization code (details omitted) for client_id '{ClientId}'", client_id);
    return Redirect(uri.ToString());
    }

  private (ISet<string> Roles, ISet<string> Permissions, IDictionary<string, string[]> Claims) ParseExtras(TestProviderOptions o)
  {
    var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var perms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var claims = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

    // Roles (multi-value aware)
    void addCsvValues(IEnumerable<string> values, ISet<string> set, int cap)
    {
      foreach (var v in values)
      {
        if (string.IsNullOrWhiteSpace(v)) continue;
        foreach (var iv in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
          if (set.Count >= cap) return;
          set.Add(iv);
        }
      }
    }

    if (Request.Query.TryGetValue("roles", out var qRoles)) addCsvValues(qRoles, roles, o.MaxRoles);
    else if (Request.Query.TryGetValue("Koan.roles", out var qsRoles)) addCsvValues(qsRoles, roles, o.MaxRoles);

    if (Request.Query.TryGetValue("perms", out var qPerms)) addCsvValues(qPerms, perms, o.MaxPermissions);
    else if (Request.Query.TryGetValue("permissions", out var qPerms2)) addCsvValues(qPerms2, perms, o.MaxPermissions);
    else if (Request.Query.TryGetValue("Koan.permissions", out var qPerms3)) addCsvValues(qPerms3, perms, o.MaxPermissions);

    // Custom claims (claim.{type}=a,b&claim.{type}=c)
    foreach (var key in Request.Query.Keys)
    {
      if (string.IsNullOrWhiteSpace(key)) continue;
      if (!key.StartsWith(Constants.ClaimPrefix, StringComparison.OrdinalIgnoreCase)) continue;
      var type = key.Substring(Constants.ClaimPrefix.Length);
      if (string.IsNullOrWhiteSpace(type)) continue;
      if (!claims.TryGetValue(type, out var list)) { if (claims.Count >= o.MaxCustomClaimTypes) break; claims[type] = list = new List<string>(); }

      var vals = Request.Query[key];
      foreach (var v in vals)
      {
        if (string.IsNullOrWhiteSpace(v)) continue;
        foreach (var iv in v.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
          if (list.Count >= o.MaxValuesPerClaimType) break;
          if (!list.Contains(iv)) list.Add(iv);
        }
      }
    }

    var normalized = claims.ToDictionary(
      k => k.Key,
      v => v.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
      StringComparer.OrdinalIgnoreCase);
    return (roles, perms, normalized);
  }
}

