using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Koan.Core.Diagnostics;
using Koan.Web.Auth.Providers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Koan.Web.Auth.Integration.Tests.CustomProtocol;

public sealed class CustomProtocolSpec(CustomProtocolFixture fixture) : IClassFixture<CustomProtocolFixture>
{
    [Fact]
    public async Task Custom_protocol_projects_into_existing_discovery_facts_election_and_standard_scheme()
    {
        using var client = fixture.NewClient();
        using var response = await client.GetAsync("/.well-known/auth/providers", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var provider = document.RootElement.EnumerateArray().Single(item => item.GetProperty("id").GetString() == "custom");
        provider.GetProperty("challengeUrl").GetString().Should().Be("/auth/custom/challenge");

        var catalog = fixture.Services.GetRequiredService<IAuthProviderCatalog>();
        catalog.Default!.Id.Should().Be("custom");
        catalog.Find("custom")!.Scopes.Should().Equal("fixture");
        var facts = fixture.Services.GetRequiredService<IKoanRuntimeFacts>().Current.Facts;
        facts.Should().Contain(fact => fact.Code == "koan.auth.provider.eligible" && fact.Subject == "auth:provider:custom");
        facts.Should().Contain(fact => fact.Code == "koan.auth.provider.selected" && fact.Subject == "auth:provider:default");
        var scheme = await fixture.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetSchemeAsync("custom");
        scheme!.HandlerType.Should().Be(typeof(ProbeAuthenticationHandler));
    }

    [Fact]
    public async Task Custom_challenge_keeps_return_policy_and_issues_existing_application_cookie()
    {
        using var client = fixture.NewClient();
        using var challenge = await client.GetAsync("/auth/custom/challenge?identifier=alice.example.test&return=https://untrusted.example.test/", TestContext.Current.CancellationToken);
        challenge.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var callback = await client.GetAsync(challenge.Headers.Location, TestContext.Current.CancellationToken);
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callback.Headers.Location!.OriginalString.Should().Be("/");
        callback.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies.Should().Contain(value => value.StartsWith(".AspNetCore.Koan.cookie=", StringComparison.Ordinal));

        using var who = await client.GetAsync("/e2e/whoami", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await who.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("authenticated").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("id").GetString().Should().Be("fixture-subject");
    }

    [Fact]
    public async Task Callback_query_cannot_replace_challenge_input_and_sign_in_rejection_prevents_cookie()
    {
        using var client = fixture.NewClient();
        using var challenge = await client.GetAsync("/auth/custom/challenge?identifier=reject.example.test&return=/e2e/whoami", TestContext.Current.CancellationToken);
        challenge.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var callbackUrl = QueryHelpers.AddQueryString(challenge.Headers.Location!.OriginalString, "identifier", "alice.example.test");
        using var callback = await client.GetAsync(callbackUrl, TestContext.Current.CancellationToken);
        callback.IsSuccessStatusCode.Should().BeFalse();
        if (callback.Headers.TryGetValues("Set-Cookie", out var cookies))
            cookies.Should().NotContain(value => value.StartsWith(".AspNetCore.Koan.cookie=", StringComparison.Ordinal));

        using var who = await client.GetAsync("/e2e/whoami", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await who.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        document.RootElement.GetProperty("authenticated").GetBoolean().Should().BeFalse();
    }
}
