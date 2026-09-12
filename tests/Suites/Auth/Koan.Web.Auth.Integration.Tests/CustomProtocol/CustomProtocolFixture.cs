using Koan.Core;
using Koan.Web.Auth.Extensions;
using Koan.Web.Auth.Flow;
using Koan.Web.Auth.Options;
using Koan.Web.Auth.Providers;
using Koan.Web.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Koan.Web.Auth.Integration.Tests.CustomProtocol;

/// <summary>Real AddKoan host with a deliberately synthetic protocol; proves composition, not external identity security.</summary>
public sealed class CustomProtocolFixture : IAsyncLifetime
{
    private IHost? _host;
    public string BaseUrl { get; private set; } = "";
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started.");

    public async ValueTask InitializeAsync()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHostDefaults(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.UseEnvironment("Development");
                web.ConfigureServices(services =>
                {
                    services.AddDataProtection().UseEphemeralDataProtectionProvider();
                    services.AddKoan();
                    services.AddKoanWeb();
                    services.AddKoanControllersFrom<WhoAmIController>();
                    // Connector-shaped contributions are local to this fixture, so existing OAuth/OIDC hosts keep
                    // their original provider set. Production connectors put these registrations in their module.
                    services.AddSingleton<IAuthProtocol, ProbeAuthProtocol>();
                    services.AddSingleton(new AuthProviderDefinition("custom", new ProviderOptions
                    {
                        Type = "synthetic", DisplayName = "Synthetic fixture", Scopes = ["fixture"], Priority = 1000
                    }, Automatic: true));
                    services.AddScoped<IKoanAuthFlowHandler, ProbeSignInPolicy>();
                    services.AddAuthentication().AddScheme<RemoteAuthenticationOptions, ProbeAuthenticationHandler>(
                        "custom", options =>
                        {
                            options.SignInScheme = AuthenticationExtensions.CookieScheme;
                            options.CallbackPath = "/auth/custom/callback";
                            options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
                        });
                });
                web.Configure(_ => { });
            }).Build();

        await _host.StartAsync();
        BaseUrl = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }

    public HttpClient NewClient()
        => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true }) { BaseAddress = new Uri(BaseUrl) };

    public async ValueTask DisposeAsync()
    {
        if (_host is null) return;
        await _host.StopAsync();
        _host.Dispose();
    }
}
