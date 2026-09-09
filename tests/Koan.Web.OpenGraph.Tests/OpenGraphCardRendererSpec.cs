using Microsoft.AspNetCore.Builder;
using System;
using System.IO;
using System.Threading.Tasks;
using AwesomeAssertions;
using Koan.Core;
using Koan.Core.Hosting.App;
using Koan.Data.Core;
using Koan.Testing.Integration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Koan.Web.OpenGraph.Tests;

/// <summary>
/// ARCH-0079 integration spec: boots through real <c>AddKoan()</c> reflective discovery with the
/// InMemory data connector, then exercises register -> request -> injected-and-encoded head end to
/// end. No fakes: the snapshot is persisted and read back through the real data layer, which also
/// proves the <c>[Cacheable]</c> entity degrades to a plain persisted read with no cache adapter.
/// </summary>
public sealed class OpenGraphCardRendererSpec : IAsyncLifetime
{
    private IntegrationHost _host = default!;
    private IServiceProvider? _previousAppHost;
    private string _shellPath = default!;

    public async ValueTask InitializeAsync()
    {
        _shellPath = Path.Combine(Path.GetTempPath(), $"koan-og-shell-{Guid.NewGuid():N}.html");
        await File.WriteAllTextAsync(_shellPath, "<html><head><!--KOAN_OPENGRAPH--></head><body>app</body></html>");

        _host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "inmemory")
            .WithSetting("Koan:Web:OpenGraph:ShellPath", _shellPath)
            .WithSetting("Koan:Web:OpenGraph:SiteName", "Test Site")
            .WithSetting("Koan:Web:OpenGraph:DefaultImage", "/img/default.png")
            .ConfigureServices(s => s.AddKoan(RegisterWorkCard))
            .StartAsync();

        _previousAppHost = AppHost.Current;
        AppHost.Current = _host.Services;
    }

    public async ValueTask DisposeAsync()
    {
        AppHost.Current = _previousAppHost!;
        await _host.DisposeAsync();
        try { File.Delete(_shellPath); } catch { /* best-effort */ }
    }

    private IOpenGraphCardRenderer Renderer => _host.Services.GetRequiredService<IOpenGraphCardRenderer>();

    private static HttpRequest Navigation(string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Scheme = "https";
        ctx.Request.Host = new HostString("example.org");
        ctx.Request.Path = path;
        ctx.Request.Headers.Accept = "text/html";
        return ctx.Request;
    }

    private static void RegisterWorkCard()
    {
        SocialCards.For<TestWork>("/work/{id}", id => TestWork.Get(id))
            .Title(w => w.Name)
            .Description(w => w.Summary)
            .Image(w => CardImage.Recipe("share-card", w.CoverMediaId))
            .Url(w => $"/work/{w.Id}")
            .Type("article");
    }

    [Fact]
    public async Task Composed_middleware_preserves_controller_not_found()
    {
        var app = new Microsoft.AspNetCore.Builder.ApplicationBuilder(_host.Services);
        app.UseOpenGraphCards();
        app.Run(context => { context.Response.StatusCode = 404; return Task.CompletedTask; });
        var request = Navigation("/work/hidden");
        request.HttpContext.RequestServices = _host.Services;
        request.HttpContext.Response.Body = new MemoryStream();
        request.HttpContext.SetEndpoint(new Endpoint(null,
            new EndpointMetadataCollection(new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()),
            "Resource controller"));
        await app.Build()(request.HttpContext);
        request.HttpContext.Response.StatusCode.Should().Be(404);
        request.HttpContext.Response.Body.Length.Should().Be(0);
    }

    [Fact]
    public async Task Matched_route_emits_full_card_for_the_entity()
    {
        var work = new TestWork { Name = "Parukita Preset", Summary = "A warm cinematic preset.", CoverMediaId = "cover123" };
        await work.Save();

        var html = await Renderer.RenderShellAsync(Navigation($"/work/{work.Id}/parukita-reshade-preset"));

        html.Should().NotBeNull();
        html!.Should().NotContain("<!--KOAN_OPENGRAPH-->", "the marker must be replaced");
        html.Should().Contain("<title>Parukita Preset</title>");
        html.Should().Contain("<link rel=\"canonical\" href=\"https://example.org/work/" + work.Id + "\" />");
        html.Should().Contain("<meta property=\"og:title\" content=\"Parukita Preset\" />");
        html.Should().Contain("<meta property=\"og:description\" content=\"A warm cinematic preset.\" />");
        html.Should().Contain("<meta property=\"og:image\" content=\"https://example.org/media/cover123/share-card\" />");
        html.Should().Contain("<meta property=\"og:url\" content=\"https://example.org/work/" + work.Id + "\" />");
        html.Should().Contain("<meta property=\"og:type\" content=\"article\" />");
        html.Should().Contain("<meta property=\"og:site_name\" content=\"Test Site\" />");
        html.Should().Contain("<meta name=\"twitter:card\" content=\"summary_large_image\" />");
        html.Should().Contain("<meta name=\"twitter:image\" content=\"https://example.org/media/cover123/share-card\" />");
    }

    [Fact]
    public async Task Injected_values_are_html_encoded()
    {
        var work = new TestWork { Name = "<script>alert('xss')</script>", Summary = "safe", CoverMediaId = "c1" };
        await work.Save();

        var html = await Renderer.RenderShellAsync(Navigation($"/work/{work.Id}"));

        html.Should().NotBeNull();
        html!.Should().NotContain("<script>alert", "the payload must not survive as live markup");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public async Task Description_truncates_at_the_configured_maximum()
    {
        var work = new TestWork { Name = "ok", Summary = new string('d', 300), CoverMediaId = "c1" };
        await work.Save();

        var html = await Renderer.RenderShellAsync(Navigation($"/work/{work.Id}"));

        html.Should().NotBeNull();
        // HtmlEncoder.Default emits the ellipsis as its numeric entity, which is correct output.
        html!.Should().Contain("&#x2026;", "an over-long description is truncated with an ellipsis");
        html.Should().NotContain(new string('d', 200), "the default max is 200 characters");
    }

    [Fact]
    public async Task Unknown_id_falls_to_the_default_card_without_error()
    {
        var html = await Renderer.RenderShellAsync(Navigation("/work/does-not-exist"));

        html.Should().NotBeNull();
        html!.Should().Contain("<meta property=\"og:title\" content=\"Test Site\" />", "the site name is the default title");
        html.Should().Contain("<meta property=\"og:image\" content=\"https://example.org/img/default.png\" />");
    }

    [Fact]
    public async Task Unmatched_route_falls_to_the_default_card()
    {
        var html = await Renderer.RenderShellAsync(Navigation("/about/contact"));

        html.Should().NotBeNull();
        html!.Should().Contain("<meta property=\"og:title\" content=\"Test Site\" />");
    }

    [Fact]
    public async Task Snapshot_is_warmed_on_upsert_and_removed_on_delete()
    {
        var work = new TestWork { Name = "First", Summary = "s", CoverMediaId = "c1" };
        await work.Save();

        var key = "TestWork:" + work.Id;
        (await SocialCardSnapshot.Get(key)).Should().NotBeNull("upsert warms the snapshot");
        (await SocialCardSnapshot.Get(key))!.Title.Should().Be("First");

        work.Name = "Second";
        await work.Save();
        (await SocialCardSnapshot.Get(key))!.Title.Should().Be("Second", "re-upsert rebuilds the snapshot");

        await TestWork.Remove(work.Id);
        (await SocialCardSnapshot.Get(key)).Should().BeNull("delete evicts the snapshot");
    }

    [Fact]
    public async Task Cold_snapshot_is_lazily_filled_on_first_request()
    {
        // Remove the eager snapshot to exercise the request-time lazy fill path.
        var work = new TestWork { Name = "Cold Start", Summary = "s", CoverMediaId = "c1" };
        await work.Save();

        var key = "TestWork:" + work.Id;
        await SocialCardSnapshot.Remove(key);
        (await SocialCardSnapshot.Get(key)).Should().BeNull("not warmed");

        var html = await Renderer.RenderShellAsync(Navigation($"/work/{work.Id}"));

        html.Should().NotBeNull();
        html!.Should().Contain("<meta property=\"og:title\" content=\"Cold Start\" />");
        (await SocialCardSnapshot.Get(key)).Should().NotBeNull("the request lazily filled the snapshot");
    }

    [Fact]
    public async Task Toggles_off_suppress_their_tags()
    {
        // A fresh host with the head toggles disabled; operate entirely against its own store.
        await using var host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:Environment", "Test")
            .WithSetting("Koan:Data:Sources:Default:Adapter", "inmemory")
            .WithSetting("Koan:Web:OpenGraph:ShellPath", _shellPath)
            .WithSetting("Koan:Web:OpenGraph:SiteName", "Test Site")
            .WithSetting("Koan:Web:OpenGraph:EmitTitleElement", "false")
            .WithSetting("Koan:Web:OpenGraph:EmitCanonical", "false")
            .WithSetting("Koan:Web:OpenGraph:EmitTwitterTags", "false")
            .ConfigureServices(s => s.AddKoan(RegisterWorkCard))
            .StartAsync();

        var previous = AppHost.Current;
        AppHost.Current = host.Services;
        try
        {
            var work = new TestWork { Name = "Toggle", Summary = "s", CoverMediaId = "c1" };
            await work.Save();

            var renderer = host.Services.GetRequiredService<IOpenGraphCardRenderer>();
            var html = await renderer.RenderShellAsync(Navigation($"/work/{work.Id}"));

            html.Should().NotBeNull();
            html!.Should().NotContain("<title>");
            html.Should().NotContain("rel=\"canonical\"");
            html.Should().NotContain("twitter:card");
            html.Should().Contain("<meta property=\"og:title\" content=\"Toggle\" />", "og: tags are still emitted");
        }
        finally
        {
            AppHost.Current = previous;
        }
    }
}
