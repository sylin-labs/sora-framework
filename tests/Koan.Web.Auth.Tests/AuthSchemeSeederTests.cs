using AwesomeAssertions;
using Koan.Web.Auth.Hosting;
using Koan.Web.Auth.Options;
using Koan.Web.Auth.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Koan.Web.Auth.Tests;

public sealed class AuthSchemeSeederTests
{
    [Fact]
    public void Eligible_custom_protocol_without_its_named_handler_stops_startup()
    {
        using var services = Services(CustomPlan()).BuildServiceProvider();
        var act = () => AuthSchemeSeeder.Seed(services);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*'account'*'custom'*no ASP.NET authentication scheme*register scheme 'account'*disable/remove*");
    }

    [Fact]
    public async Task Connector_registered_standard_scheme_is_preserved_on_repeated_seeding()
    {
        var registrations = Services(CustomPlan());
        registrations.AddAuthentication().AddCookie("account");
        using var services = registrations.BuildServiceProvider();
        var schemes = services.GetRequiredService<IAuthenticationSchemeProvider>();
        var original = await schemes.GetSchemeAsync("account");

        AuthSchemeSeeder.Seed(services);
        AuthSchemeSeeder.Seed(services);

        (await schemes.GetSchemeAsync("account")).Should().BeSameAs(original);
        original!.HandlerType.Should().Be(typeof(CookieAuthenticationHandler));
    }

    [Fact]
    public async Task Builtin_protocols_use_normalized_plan_and_retained_maintained_handlers()
    {
        var plan = new AuthProviderPlan(Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
        [
            new AuthProviderDefinition("openid", new ProviderOptions
            {
                Type = " OIDC ", Authority = "https://identity.example.test", ClientId = "client", ClientSecret = "secret"
            }, Automatic: true),
            new AuthProviderDefinition("oauth", new ProviderOptions
            {
                Type = " OAuth2 ", ClientId = "client", ClientSecret = "secret",
                AuthorizationEndpoint = "https://identity.example.test/authorize",
                TokenEndpoint = "https://identity.example.test/token",
                UserInfoEndpoint = "https://identity.example.test/userinfo"
            }, Automatic: true)
        ]);
        using var services = Services(plan).BuildServiceProvider();
        var schemes = services.GetRequiredService<IAuthenticationSchemeProvider>();

        AuthSchemeSeeder.Seed(services);
        var oidc = await schemes.GetSchemeAsync("openid");
        var oauth = await schemes.GetSchemeAsync("oauth");
        AuthSchemeSeeder.Seed(services);

        oidc!.HandlerType.Should().Be(typeof(OpenIdConnectHandler));
        oauth!.HandlerType.Should().Be(typeof(OAuthHandler<OAuthOptions>));
        (await schemes.GetSchemeAsync("openid")).Should().BeSameAs(oidc);
        (await schemes.GetSchemeAsync("oauth")).Should().BeSameAs(oauth);
    }

    private static ServiceCollection Services(AuthProviderPlan plan)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication();
        services.AddSingleton(plan);
        return services;
    }

    private static AuthProviderPlan CustomPlan()
        => new(Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            [new AuthProviderDefinition("account", new ProviderOptions { Type = "custom" }, Automatic: true)],
            [new TestProtocol()]);

    private sealed class TestProtocol : IAuthProtocol
    {
        public string Protocol => "custom";
        public IReadOnlyList<string> Validate(string providerId, ProviderOptions options) => [];
    }
}
