using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Koan.Core;
using Koan.Web.Infrastructure;
using Koan.Web.Options;
using System.Diagnostics;
using System.Linq;

namespace Koan.Web.Hosting;

/// <summary>
/// Startup filter that wires Koan's default web pipeline, health endpoints, secure headers, and controller mapping.
/// Also logs dev-only data configuration with credential masking.
/// </summary>
internal sealed class KoanWebStartupFilter(IOptions<KoanWebOptions> options, IOptions<WebPipelineOptions> pipelineOptions) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Pipeline construction needs the web provider ambiently, but must not replace a newer
            // process owner. The flow scope also covers downstream startup filters and restores on exit.
            using var hostScope = Koan.Core.Hosting.App.AppHost.PushScope(app.ApplicationServices);

            var opts = options.Value;
            var pipeline = pipelineOptions.Value ?? new WebPipelineOptions();
            const string appliedKey = "Koan.Web.Applied";
            if (!app.Properties.ContainsKey(appliedKey))
            {
                app.Properties[appliedKey] = true;
                // Greenfield boot: initialize env and run the runtime against the scoped web provider.
                // KoanEnv.TryInitialize is itself best-effort and swallows its own failures (KoanEnv.cs),
                // so this guard is belt-and-suspenders against an unexpected throw during env snapshot init;
                // env not being initialized here is non-fatal — downstream callers re-try TryInitialize.
                try { Koan.Core.KoanEnv.TryInitialize(app.ApplicationServices); } catch { }
                var rt = app.ApplicationServices.GetService(typeof(Koan.Core.Hosting.Runtime.IAppRuntime)) as Koan.Core.Hosting.Runtime.IAppRuntime;
                rt?.Discover();
                rt?.Start();

                // Dev-only: reserve space for future diagnostics (SoC: data modules own their config and reporting)
                if (pipeline.UseExceptionHandler)
                {
                    app.UseExceptionHandler();
                }
                if (opts.EnableSecureHeaders && !opts.IsProxiedApi)
                {
                    app.Use((ctx, next) =>
                    {
                        // Ensure headers are normalized just before sending
                        ctx.Response.OnStarting(() =>
                        {
                            var headers = ctx.Response.Headers;
                            if (headers.ContainsKey(KoanWebConstants.Headers.XXssProtection))
                                headers.Remove(KoanWebConstants.Headers.XXssProtection);
                            if (!headers.ContainsKey(KoanWebConstants.Headers.XContentTypeOptions))
                                headers[KoanWebConstants.Headers.XContentTypeOptions] = KoanWebConstants.Policies.NoSniff;
                            if (!headers.ContainsKey(KoanWebConstants.Headers.XFrameOptions))
                                headers[KoanWebConstants.Headers.XFrameOptions] = KoanWebConstants.Policies.Deny;
                            if (!headers.ContainsKey(KoanWebConstants.Headers.ReferrerPolicy))
                                headers[KoanWebConstants.Headers.ReferrerPolicy] = KoanWebConstants.Policies.NoReferrer;
                            if (!string.IsNullOrWhiteSpace(opts.ContentSecurityPolicy) && !headers.ContainsKey(KoanWebConstants.Headers.ContentSecurityPolicy))
                                headers[KoanWebConstants.Headers.ContentSecurityPolicy] = opts.ContentSecurityPolicy;
                            return Task.CompletedTask;
                        });
                        return next();
                    });
                }
                // Static/default files can terminate the pipeline, so register response headers first.
                var webEnvironment = app.ApplicationServices.GetRequiredService<IWebHostEnvironment>();
                if (opts.EnableStaticFiles && webEnvironment.WebRootFileProvider is not NullFileProvider)
                {
                    app.UseDefaultFiles();
                    app.UseStaticFiles();
                }
                // Lightweight health alias: if configured, respond to GET {HealthPath} with { status: "ok" }
                if (!string.IsNullOrWhiteSpace(opts.HealthPath))
                {
                    app.Use(async (ctx, next) =>
                    {
                        if (HttpMethods.IsGet(ctx.Request.Method)
                            && string.Equals(ctx.Request.Path.Value, opts.HealthPath, StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.Response.Headers[KoanWebConstants.Headers.XContentTypeOptions] = KoanWebConstants.Policies.NoSniff;
                            await ctx.Response.WriteAsJsonAsync(new { status = "ok" });
                            return;
                        }
                        await next();
                    });
                }
                if (opts.AutoMapControllers)
                {
                    RunStage(app, KoanWebPipelineStage.BeforeRouting);
                    app.UseRouting();
                    // Ensure auth middleware is in the correct position (after routing, before endpoints)
                    // Safe even if no authentication schemes are registered.
                    try
                    {
                        app.UseAuthentication();
                    }
                    catch (InvalidOperationException)
                    {
                        // No authentication services registered; ignore.
                    }
                    // WEB-0069: modules contribute middleware between authentication and authorization here
                    // (e.g. the zero-config dev identity), via the supported stage seam — no startup-filter ordering.
                    RunStage(app, KoanWebPipelineStage.AfterAuthentication);
                    try
                    {
                        app.UseAuthorization();
                    }
                    catch (InvalidOperationException)
                    {
                        // No authorization services registered; ignore.
                    }
                    RunStage(app, KoanWebPipelineStage.AfterAuthorization);
                    // Add minimal tracing header for correlation (always safe)
                    app.Use(async (ctx, next) =>
                    {
                        var traceId = Activity.Current?.TraceId.ToString();
                        if (!string.IsNullOrEmpty(traceId))
                        {
                            ctx.Response.Headers[KoanWebConstants.Headers.KoanTraceId] = traceId;
                        }
                        await next();
                    });
                    app.UseEndpoints(endpoints =>
                    {
                        // MVC controllers handle all endpoints including health
                        endpoints.MapControllers();

                        // WEB-0069: modules map their own endpoints via IKoanEndpointContributor (e.g. MCP),
                        // replacing reflection into module assemblies.
                        foreach (var contributor in app.ApplicationServices
                                     .GetServices<IKoanEndpointContributor>()
                                     .OrderBy(c => c.Order))
                        {
                            contributor.Map(endpoints);
                        }
                    });
                }
                if (pipeline.UseRateLimiter)
                {
                    try
                    {
                        app.UseRateLimiter();
                    }
                    catch (InvalidOperationException)
                    {
                        // No limiter registered via AddRateLimiter(...); treat as a no-op per ADR-0012
                    }
                }
            }
            next(app);
        };
    }

    // WEB-0069: run the middleware contributors registered for a given pipeline stage, ordered.
    private static void RunStage(IApplicationBuilder app, KoanWebPipelineStage stage)
    {
        foreach (var contributor in app.ApplicationServices
                     .GetServices<IKoanWebPipelineContributor>()
                     .Where(c => c.Stage == stage)
                     .OrderBy(c => c.Order))
        {
            contributor.Configure(app);
        }
    }
}

// WebPipelineOptions now lives in root namespace file `WebPipelineOptions.cs`
