using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Koan.Web.OpenGraph.Tests;

public sealed class MiddlewareFilterTests
{
    private static HttpRequest Request(string method, string path, string? accept)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (accept is not null)
        {
            ctx.Request.Headers.Accept = accept;
        }

        return ctx.Request;
    }

    [Fact]
    public void Controller_routes_retain_their_own_response()
    {
        var request = Request("GET", "/work/hidden", "text/html");
        request.HttpContext.SetEndpoint(new Endpoint(null,
            new EndpointMetadataCollection(new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor()),
            "Resource controller"));
        ApplicationBuilderExtensions.ShouldHandle(request).Should().BeFalse();
    }

    [Fact]
    public void Handles_html_navigation_get()
        => ApplicationBuilderExtensions.ShouldHandle(Request("GET", "/work/abc", "text/html,application/xhtml+xml"))
            .Should().BeTrue();

    [Fact]
    public void Ignores_non_get()
        => ApplicationBuilderExtensions.ShouldHandle(Request("POST", "/work/abc", "text/html"))
            .Should().BeFalse();

    [Fact]
    public void Ignores_request_without_html_accept()
        => ApplicationBuilderExtensions.ShouldHandle(Request("GET", "/work/abc", "application/json"))
            .Should().BeFalse();

    [Fact]
    public void Ignores_api_paths()
        => ApplicationBuilderExtensions.ShouldHandle(Request("GET", "/api/works/abc", "text/html"))
            .Should().BeFalse();

    [Theory]
    [InlineData("/assets/main.js")]
    [InlineData("/styles/site.css")]
    [InlineData("/img/logo.png")]
    [InlineData("/favicon.ico")]
    [InlineData("/fonts/inter.woff2")]
    public void Ignores_asset_paths(string path)
        => ApplicationBuilderExtensions.ShouldHandle(Request("GET", path, "text/html")).Should().BeFalse();

    [Fact]
    public void Handles_a_slug_that_contains_a_dot_but_is_not_an_asset()
        => ApplicationBuilderExtensions.ShouldHandle(Request("GET", "/articles/shaders-101.intro", "text/html"))
            .Should().BeTrue();
}
