using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Web.Middleware;

/// <summary>
/// Preserves the selected default authentication handler's complete result for Entity authorization.
/// ASP.NET Core's authentication middleware places successful results on the request feature collection,
/// but a failed result otherwise becomes indistinguishable from no credential once the principal stays anonymous.
/// </summary>
internal sealed class EntityAuthenticationResultMiddleware
{
    private readonly RequestDelegate _next;

    public EntityAuthenticationResultMiddleware(RequestDelegate next)
        => _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(HttpContext context)
    {
        var captured = context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult;
        if (captured is null)
        {
            var schemes = context.RequestServices.GetService<IAuthenticationSchemeProvider>();
            var authentication = context.RequestServices.GetService<IAuthenticationService>();
            var selected = schemes is null ? null : await schemes.GetDefaultAuthenticateSchemeAsync().ConfigureAwait(false);
            if (selected is not null && authentication is not null)
            {
                // AuthenticationHandler<T> caches this result for the request, so this preserves the result
                // already computed by UseAuthentication rather than validating the credential a second time.
                captured = await authentication.AuthenticateAsync(context, selected.Name).ConfigureAwait(false);
                context.Features.Set<IAuthenticateResultFeature>(new CapturedResultFeature(captured));
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private sealed class CapturedResultFeature(AuthenticateResult result) : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; } = result;
    }
}
