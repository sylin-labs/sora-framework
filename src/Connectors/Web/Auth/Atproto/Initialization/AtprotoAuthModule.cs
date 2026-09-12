using Koan.Core;
using Koan.Core.Hosting.Bootstrap;
using Koan.Core.Modules;
using Koan.Core.Provenance;
using Koan.Web.Auth.Connector.Atproto.Controllers;
using Koan.Web.Auth.Connector.Atproto.Hosting;
using Koan.Web.Auth.Connector.Atproto.Infrastructure;
using Koan.Web.Auth.Connector.Atproto.Options;
using Koan.Web.Auth.Connector.Atproto.Protocol;
using Koan.Web.Auth.Connector.Atproto.Storage;
using Koan.Web.Auth.Extensions;
using Koan.Web.Auth.Options;
using Koan.Web.Auth.Providers;
using Koan.Web.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Koan.Web.Auth.Connector.Atproto.Initialization;

public sealed class AtprotoAuthModule : KoanModule
{
    public override void Register(IServiceCollection services)
    {
        services.AddKoanOptions<AtprotoOptions>(Constants.Section);
        services.AddSingleton<IAuthProtocol, AtprotoProtocol>();
        services.AddSingleton(new AuthProviderDefinition(Constants.Provider, new ProviderOptions
        { Type = Constants.Protocol, DisplayName = "AT Protocol", Scopes = [Constants.BaseScope], Priority = 200 }));
        services.AddSingleton<AtprotoHttp>();
        services.AddSingleton<ProtectedAtprotoStore>();
        services.AddSingleton(sp => new AtprotoSessions(sp.GetRequiredService<ProtectedAtprotoStore>(), sp.GetRequiredService<AtprotoHttp>(),
            sp.GetRequiredService<IOptions<AtprotoOptions>>(), sp.GetRequiredService<IOptions<AuthOptions>>(),
            sp.GetRequiredService<IAuthProviderCatalog>(), sp.GetRequiredService<IHostEnvironment>()));
        services.AddKoanControllersFrom<AtprotoMetadataController>();
        services.AddAuthentication().AddScheme<AtprotoAuthenticationOptions, AtprotoAuthenticationHandler>(Constants.Provider, options =>
        {
            options.SignInScheme = AuthenticationExtensions.CookieScheme;
            options.CallbackPath = Constants.Callback;
            options.CorrelationCookie.Path = "/";
            options.CorrelationCookie.SameSite = SameSiteMode.Lax;
            options.CorrelationCookie.HttpOnly = true;
            options.Events = new RemoteAuthenticationEvents
            {
                OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return context.Response.WriteAsJsonAsync(new { error = "atproto_authentication_failed" });
                }
            };
        });
        services.AddOptions<AtprotoAuthenticationOptions>(Constants.Provider)
            .Configure<IDataProtectionProvider, IHostEnvironment>((options, protection, environment) =>
            {
                options.StateDataFormat = new PropertiesDataFormat(protection.CreateProtector(Constants.PropertiesPurpose));
                options.CorrelationCookie.SecurePolicy = KoanEnv.Gate.DevelopmentOnly(environment) ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            });
    }

    public override void Report(Koan.Core.Provenance.ProvenanceModuleWriter module, IConfiguration cfg, IHostEnvironment env)
    {
        module.Describe(Version);
        module.AddNote("Native AT Protocol public-client OAuth; verified DID identity, guarded discovery, protected single-process sessions. Web Auth owns provider eligibility and browser cookie lifecycle.");
    }
}
