using System;
using System.Security.Claims;
using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Koan.Web.Authorization;
using Koan.Web.Hooks;

namespace Koan.Web.Endpoints;

/// <summary>
/// Helper for constructing <see cref="EntityRequestContext"/> instances across protocols.
/// </summary>
public sealed class EntityRequestContextBuilder
{
    private readonly IServiceProvider _services;

    public EntityRequestContextBuilder(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public EntityRequestContext Build(QueryOptions options, CancellationToken cancellationToken, HttpContext? httpContext = null, ClaimsPrincipal? user = null)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        var provider = httpContext?.RequestServices ?? _services;
        var principal = user ?? httpContext?.User ?? new ClaimsPrincipal();

        // SEC-0004 origin: stamp the server-trusted koan:origin claim so an [Access(... "origin:x")] gate resolves.
        // REST/HTTP — the CONNECTION is authoritative: resolve the tier from the remote IP + declared internal
        // networks (and overwrite any client-forged value). A non-HTTP path (MCP) pre-stamps at its own edge
        // (STDIO=local, HTTP/SSE session=remote/internal); if one somehow didn't, fail safe to `remote`.
        if (httpContext is not null)
        {
            var opts = provider.GetService<IOptions<OriginOptions>>()?.Value ?? OriginOptions.Empty;
            principal = OriginStamp.Apply(principal, OriginResolver.FromHttpContext(httpContext, opts));
        }
        else if (!OriginStamp.IsStamped(principal))
        {
            principal = OriginStamp.Apply(principal, OriginTier.Remote);
        }

        return new EntityRequestContext(provider, options, cancellationToken, httpContext, principal)
        {
            AuthenticationRejected = httpContext?.Features
                .Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Failure is not null
        };
    }
}

