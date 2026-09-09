using AwesomeAssertions;
using Koan.Web.Auth.Hosting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Koan.Web.Auth.Tests;

public sealed class RequestHostOidcConfigurationManagerTests
{
    [Theory]
    [InlineData("http", "localhost:80", "http://localhost/.testoauth")]
    [InlineData("https", "app.example.test:443", "https://app.example.test/.testoauth")]
    [InlineData("https", "app.example.test:8443", "https://app.example.test:8443/.testoauth")]
    [InlineData("http", "[::1]:80", "http://[::1]/.testoauth")]
    public async Task Local_issuer_preserves_nondefault_ports_and_normalizes_default_ports(
        string scheme, string host, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        using var client = new HttpClient(new DiscoveryHandler());
        var manager = new RequestHostOidcConfigurationManager("/.testoauth",
            new HttpContextAccessor { HttpContext = context },
            new OpenIdConnectOptions { Backchannel = client, RequireHttpsMetadata = false },
            () => "http://localhost:8080");

        var configuration = await manager.GetConfigurationAsync(TestContext.Current.CancellationToken);
        configuration.Issuer.Should().Be(expected);
        configuration.TokenEndpoint.Should().Be("http://localhost:8080/.testoauth/token");
    }

    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"issuer":"http://localhost:8080/.testoauth",
                     "authorization_endpoint":"http://localhost:8080/.testoauth/authorize",
                     "token_endpoint":"http://localhost:8080/.testoauth/token"}
                    """, System.Text.Encoding.UTF8, "application/json")
            });
    }

    [Fact]
    public async Task Non_loopback_public_issuer_without_an_internal_binding_fails_correctively()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("app.example.test");
        var accessor = new HttpContextAccessor { HttpContext = context };
        var manager = new RequestHostOidcConfigurationManager(
            "/.testoauth",
            accessor,
            new OpenIdConnectOptions(),
            resolveBackchannelBase: () => null);

        var action = () => manager.GetConfigurationAsync(TestContext.Current.CancellationToken);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*public issuer 'https://app.example.test/.testoauth'*No internal Kestrel address*UseUrls*");
    }
}
