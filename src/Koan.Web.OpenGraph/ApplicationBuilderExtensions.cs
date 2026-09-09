using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Web.OpenGraph;

internal static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Inserts the OpenGraph head-injection middleware. Koan contributes this automatically when the
    /// package is referenced; this is the package-internal pipeline primitive. On an HTML
    /// navigation it injects the per-route card into the shell and short-circuits; everything else
    /// (assets, <c>/api</c>, non-html, disabled) passes through untouched.
    /// </summary>
    internal static IApplicationBuilder UseOpenGraphCards(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            if (ShouldHandle(context.Request))
            {
                var renderer = context.RequestServices.GetRequiredService<IOpenGraphCardRenderer>();
                var html = await renderer.RenderShellAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
                if (html is not null)
                {
                    context.Response.ContentType = "text/html; charset=utf-8";
                    await context.Response.WriteAsync(html, context.RequestAborted).ConfigureAwait(false);
                    return; // short-circuit: this navigation is served
                }
            }

            await next().ConfigureAwait(false);
        });
    }

    internal static bool ShouldHandle(HttpRequest request)
    {
        // Controllers own their status, authorization and rendering decisions. They can call
        // IOpenGraphCardRenderer after checking business visibility.
        if (request.HttpContext.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            return false;

        if (!HttpMethods.IsGet(request.Method))
        {
            return false;
        }

        // Navigations advertise text/html; asset fetches (text/css, image/*, */*) do not.
        var acceptsHtml = request.Headers.Accept
            .Any(a => a is not null && a.Contains("text/html", StringComparison.OrdinalIgnoreCase));
        if (!acceptsHtml)
        {
            return false;
        }

        var path = request.Path.Value ?? "/";
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !IsAssetPath(path);
    }

    private static bool IsAssetPath(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        var lastSegment = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
        var dot = lastSegment.LastIndexOf('.');
        if (dot < 0)
        {
            return false;
        }

        var ext = lastSegment[(dot + 1)..].ToLowerInvariant();
        return ext is "js" or "mjs" or "css" or "map" or "ico" or "png" or "jpg" or "jpeg"
            or "gif" or "svg" or "webp" or "avif" or "woff" or "woff2" or "ttf" or "otf"
            or "json" or "xml" or "txt" or "wasm";
    }
}
