using System.Net;
using Koan.Core;
using Koan.Web.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Koan.Web.WellKnown.Tests;

public sealed class StaticSecurityHeadersSpec
{
    private const string Html = "<!doctype html><html><body>Real static response</body></html>";
    private const string Policy = "default-src 'self'; frame-ancestors 'none'";

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task Static_html_and_controller_responses_share_the_configured_header_policy(
        bool enabled, bool proxied, bool expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "koan-static-headers", Guid.NewGuid().ToString("N"));
        var webRoot = Path.Combine(root, "wwwroot");
        Directory.CreateDirectory(webRoot);
        var index = Path.Combine(webRoot, "index.html");
        await File.WriteAllTextAsync(index, Html, TestContext.Current.CancellationToken);
        try
        {
            using var host = Host.CreateDefaultBuilder()
                .ConfigureWebHost(web =>
                {
                    web.UseTestServer();
                    web.UseEnvironment("Test");
                    web.UseContentRoot(root);
                    web.UseWebRoot(webRoot);
                    web.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Koan:Environment"] = "Test",
                        ["Koan:BackgroundServices:Enabled"] = "false",
                        ["Koan:Web:EnableSecureHeaders"] = enabled.ToString(),
                        ["Koan:Web:IsProxiedApi"] = proxied.ToString(),
                        ["Koan:Web:ContentSecurityPolicy"] = Policy,
                        ["Logging:LogLevel:Default"] = "Warning"
                    }));
                    web.ConfigureServices(services => services.AddKoan());
                    web.Configure(_ => { });
                })
                .Build();
            await host.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                using var client = host.GetTestClient();
                foreach (var path in new[] { "/", "/index.html", "/health/live" })
                {
                    using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    if (path != "/health/live")
                    {
                        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
                        Assert.Equal(Html, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                    }
                    AssertHeader(response, KoanWebConstants.Headers.XFrameOptions, expected ? KoanWebConstants.Policies.Deny : null);
                    AssertHeader(response, KoanWebConstants.Headers.ReferrerPolicy, expected ? KoanWebConstants.Policies.NoReferrer : null);
                    AssertHeader(response, KoanWebConstants.Headers.XContentTypeOptions, expected ? KoanWebConstants.Policies.NoSniff : null);
                    AssertHeader(response, KoanWebConstants.Headers.ContentSecurityPolicy, expected ? Policy : null);
                }
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally
        {
            File.Delete(index);
            Directory.Delete(webRoot);
            Directory.Delete(root);
        }
    }

    private static void AssertHeader(HttpResponseMessage response, string name, string? expected)
    {
        if (expected is null) Assert.False(response.Headers.Contains(name));
        else
        {
            Assert.True(response.Headers.TryGetValues(name, out var values), $"Missing {name} on {response.RequestMessage?.RequestUri?.AbsolutePath}");
            Assert.Equal(expected, Assert.Single(values!));
        }
    }
}
