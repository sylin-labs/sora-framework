using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koan.Web.Auth.Integration.Tests.CustomProtocol;

/// <summary>Test-only loopback protocol. No upstream identity assertion is made or exercised here.</summary>
public sealed class ProbeAuthenticationHandler(
    IOptionsMonitor<RemoteAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDataProtectionProvider protection) : RemoteAuthenticationHandler<RemoteAuthenticationOptions>(options, logger, encoder)
{
    private readonly PropertiesDataFormat _state = new(protection.CreateProtector("auth-seam-integration-fixture"));

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // Custom input is available on the initiating request without changing the generic controller.
        properties.Items["identifier"] = Request.Query["identifier"].ToString();
        GenerateCorrelationId(properties);
        Response.Redirect(QueryHelpers.AddQueryString(Options.CallbackPath, "state", _state.Protect(properties)));
        return Task.CompletedTask;
    }

    protected override Task<HandleRequestResult> HandleRemoteAuthenticateAsync()
    {
        var properties = _state.Unprotect(Request.Query["state"]!);
        if (properties is null || !ValidateCorrelationId(properties))
            return Task.FromResult(HandleRequestResult.Fail("Invalid fixture state or correlation."));

        // Callback query cannot replace the value bound into the protected challenge state.
        var identity = new ClaimsIdentity(Scheme.Name);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, "fixture-subject"));
        identity.AddClaim(new Claim("fixture-identifier", properties.Items["identifier"] ?? ""));
        return Task.FromResult(HandleRequestResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), properties, Scheme.Name)));
    }
}
