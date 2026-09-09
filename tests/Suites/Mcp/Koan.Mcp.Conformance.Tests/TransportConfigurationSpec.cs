using Koan.Core;
using Koan.Mcp.Options;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Koan.Mcp.Conformance.Tests;

public sealed class TransportConfigurationSpec
{
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task Retired_transport_intent_fails_with_migration_guidance(string value)
    {
        var builder = KoanIntegrationHost.Configure()
            .WithSetting("Koan:BackgroundServices:Enabled", "false")
            .WithSetting("Koan:Mcp:EnableStdioTransport", "false")
            .WithSetting("Koan:Mcp:EnableHttpSseTransport", value)
            .ConfigureServices(services => services.AddKoan());
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => builder.StartAsync());
        Assert.Contains("EnableStreamableHttpTransport", error.Message);
        Assert.Contains("EnableLegacySseTransport", error.Message);
    }

    [Fact]
    public async Task Explicit_current_transport_options_remain_effective()
    {
        await using var host = await KoanIntegrationHost.Configure()
            .WithSetting("Koan:BackgroundServices:Enabled", "false")
            .WithSetting("Koan:Mcp:EnableStdioTransport", "false")
            .WithSetting("Koan:Mcp:EnableStreamableHttpTransport", "true")
            .WithSetting("Koan:Mcp:EnableLegacySseTransport", "true")
            .ConfigureServices(services => services.AddKoan()).StartAsync();
        var options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        Assert.True(options.EnableStreamableHttpTransport);
        Assert.True(options.EnableLegacySseTransport);
    }
}
