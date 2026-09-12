using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Koan.Web.Auth.Options;
using Koan.Web.Auth.Providers;
using Xunit;

namespace Koan.Web.Auth.Tests;

public sealed class AuthProviderPlanTests
{
    [Fact]
    public void Unconfigured_connector_is_inactive_and_automatic_local_provider_wins()
    {
        var plan = Compile(
            new AuthOptions(),
            Oidc("google"),
            LocalOidc());

        plan.Find("google")!.State.Should().Be("inactive");
        plan.Find("google")!.Eligible.Should().BeFalse();
        plan.Default!.Id.Should().Be("test-oidc");
        plan.Default.Reason.Should().Be("automatic-local-provider");
    }

    [Fact]
    public void Explicit_complete_provider_wins_over_automatic_default()
    {
        var plan = Compile(
            new AuthOptions
            {
                Providers = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["google"] = new ProviderOptions
                    {
                        ClientId = "client",
                        ClientSecret = "secret"
                    }
                }
            },
            Oidc("google"),
            LocalOidc());

        plan.Default!.Id.Should().Be("google");
        plan.Default.Explicit.Should().BeTrue();
    }

    [Fact]
    public void Explicit_incomplete_provider_fails_with_exact_correction()
    {
        var act = () => Compile(
            new AuthOptions
            {
                Providers = new(StringComparer.OrdinalIgnoreCase)
                {
                    ["google"] = new ProviderOptions { ClientId = "client" }
                }
            },
            Oidc("google"));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*google*ClientSecret*Koan:Web:Auth:Providers:google*");
    }

    [Fact]
    public void Preferred_inactive_provider_fails_instead_of_silently_falling_back()
    {
        var act = () => Compile(
            new AuthOptions { PreferredProviderId = "google" },
            Oidc("google"),
            LocalOidc());

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*PreferredProviderId 'google'*not eligible*Configure Koan:Web:Auth:Providers:google*");
    }

    [Fact]
    public void Config_only_oidc_provider_is_a_first_class_candidate()
    {
        var plan = Compile(new AuthOptions
        {
            Providers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["corporate"] = new ProviderOptions
                {
                    Type = AuthProviderProtocols.Oidc,
                    Authority = "https://identity.example.test",
                    ClientId = "client",
                    ClientSecret = "secret"
                }
            }
        });

        plan.Default!.Id.Should().Be("corporate");
        plan.Default.Explicit.Should().BeTrue();
        plan.Find("corporate")!.Protocol.Should().Be(AuthProviderProtocols.Oidc);
    }

    [Fact]
    public void Plans_are_host_owned_and_do_not_share_state()
    {
        var first = Compile(new AuthOptions(), LocalOidc());
        var second = Compile(new AuthOptions(), Oidc("google"));

        first.Default!.Id.Should().Be("test-oidc");
        second.Default.Should().BeNull();
        second.Find("test-oidc").Should().BeNull();
    }

    [Fact]
    public void Custom_protocol_validates_merged_options_once_and_joins_the_existing_plan()
    {
        ProviderOptions? observed = null;
        var calls = 0;
        var protocol = new TestProtocol("custom", (_, options) =>
        {
            calls++;
            observed = options;
            return [];
        });
        var plan = new AuthProviderPlan(Microsoft.Extensions.Options.Options.Create(new AuthOptions
        {
            Providers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = new ProviderOptions { Scopes = ["scoped"], CallbackPath = "/auth/account/callback" }
            }
        }), [new AuthProviderDefinition("account", new ProviderOptions
        {
            Type = " CUSTOM ", DisplayName = "External account", ClientId = "baseline-client", Scopes = ["baseline"]
        })], [protocol]);

        calls.Should().Be(1);
        observed!.ClientId.Should().Be("baseline-client");
        observed.Scopes.Should().Equal("scoped");
        observed.CallbackPath.Should().Be("/auth/account/callback");
        plan.Default!.Id.Should().Be("account");
        plan.Default.Protocol.Should().Be("custom");
        plan.Default.ChallengePath.Should().Be("/auth/account/challenge");
        plan.Default.Scopes.Should().Equal("scoped");
        plan.Find("account").Should().BeSameAs(plan.Default);
        calls.Should().Be(1, "reading the immutable plan must not re-run validation");
    }

    [Fact]
    public void Custom_protocol_correction_retains_its_own_configuration_path()
    {
        var protocol = new TestProtocol("custom", (_, _) => ["Set Koan:Web:Auth:Custom:KeyStore."]);
        var act = () => new AuthProviderPlan(Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            [new AuthProviderDefinition("account", new ProviderOptions { Type = "custom" }, Automatic: true)],
            [protocol]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*account*custom*Set Koan:Web:Auth:Custom:KeyStore.*");
    }

    [Fact]
    public void Explicit_unknown_protocol_fails_with_connector_correction()
    {
        var act = () => Compile(new AuthOptions
        {
            Providers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["account"] = new ProviderOptions { Type = "missing-protocol" }
            }
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*account*Type*supported:*oauth2*oidc*reference the connector*Koan:Web:Auth:Providers:account*");
    }

    [Fact]
    public void Inactive_custom_provider_does_not_validate_or_become_default()
    {
        var calls = 0;
        var protocol = new TestProtocol("custom", (_, _) => { calls++; return ["Must not run."]; });
        var plan = new AuthProviderPlan(Microsoft.Extensions.Options.Options.Create(new AuthOptions()),
            [new AuthProviderDefinition("account", new ProviderOptions { Type = "custom" })], [protocol]);

        plan.Find("account")!.State.Should().Be("inactive");
        plan.Default.Should().BeNull();
        calls.Should().Be(0);
    }

    [Fact]
    public void Duplicate_protocol_declarations_fail_instead_of_using_registration_order()
    {
        var act = () => new AuthProviderPlan(Microsoft.Extensions.Options.Options.Create(new AuthOptions()), [],
            [new TestProtocol("custom", (_, _) => []), new TestProtocol(" CUSTOM ", (_, _) => [])]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*declared more than once*");
    }

    [Theory]
    [InlineData(AuthProviderProtocols.Oidc)]
    [InlineData(AuthProviderProtocols.OAuth2)]
    public void Connector_cannot_replace_builtin_protocol_mechanics(string protocol)
    {
        var act = () => new AuthProviderPlan(Microsoft.Extensions.Options.Options.Create(new AuthOptions()), [],
            [new TestProtocol(protocol, (_, _) => [])]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*owned by Koan Web Auth*cannot be replaced*");
    }

    [Fact]
    public void Custom_protocol_availability_does_not_leak_between_hosts()
    {
        var definition = new AuthProviderDefinition("account", new ProviderOptions { Type = "custom" }, Automatic: true);
        var first = new AuthProviderPlan(Microsoft.Extensions.Options.Options.Create(new AuthOptions()), [definition],
            [new TestProtocol("custom", (_, _) => [])]);
        var second = () => new AuthProviderPlan(Microsoft.Extensions.Options.Options.Create(new AuthOptions()), [definition]);

        first.Default!.Id.Should().Be("account");
        second.Should().Throw<InvalidOperationException>().WithMessage("*reference the connector*");
    }

    private sealed class TestProtocol(string protocol, Func<string, ProviderOptions, IReadOnlyList<string>> validate) : IAuthProtocol
    {
        public string Protocol => protocol;
        public IReadOnlyList<string> Validate(string providerId, ProviderOptions options) => validate(providerId, options);
    }

    private static AuthProviderPlan Compile(AuthOptions options, params AuthProviderDefinition[] definitions)
        => new(Microsoft.Extensions.Options.Options.Create(options), definitions);

    private static AuthProviderDefinition Oidc(string id)
        => AuthProviderDefinition.Oidc(
            id,
            id,
            $"/icons/{id}.svg",
            $"https://{id}.example.test",
            ["openid", "profile"],
            priority: 200);

    private static AuthProviderDefinition LocalOidc()
        => new(
            "test-oidc",
            new ProviderOptions
            {
                Type = AuthProviderProtocols.Oidc,
                DisplayName = "Test OIDC",
                Authority = "/.testoauth",
                ClientId = "test-client",
                ClientSecret = "test-secret",
                Scopes = ["openid"],
                Priority = 1
            },
            Automatic: true);
}
