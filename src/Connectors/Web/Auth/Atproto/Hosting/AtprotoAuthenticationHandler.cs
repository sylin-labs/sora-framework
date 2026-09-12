using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Koan.Web.Auth.Connector.Atproto.Infrastructure;
using Koan.Web.Auth.Connector.Atproto.Options;
using Koan.Web.Auth.Connector.Atproto.Protocol;
using Koan.Web.Auth.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koan.Web.Auth.Connector.Atproto.Hosting;

internal sealed class AtprotoAuthenticationHandler(IOptionsMonitor<AtprotoAuthenticationOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, AtprotoSessions sessions, IExternalIdentityStore? identities = null)
    : RemoteAuthenticationHandler<AtprotoAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
      try
      {
        if (!sessions.Enabled) throw new InvalidOperationException("AT provider is inactive.");
        var identifier = Request.Query["identifier"];
        if (identifier.Count > 1 || (identifier.Count == 1 && (string.IsNullOrWhiteSpace(identifier) || identifier.ToString().Length > 2048)))
            throw new InvalidOperationException("Supply one account handle or DID as identifier.");
        GenerateCorrelationId(properties);
        var protectedProperties = Options.StateDataFormat.Protect(properties);
        var authorization = identifier.Count == 0
            ? await sessions.StartServer("https://bsky.social", properties, protectedProperties, Context.RequestAborted)
            : await sessions.Start(identifier.ToString().Trim(), properties, protectedProperties, Context.RequestAborted);
        Response.Redirect(authorization);
      }
      catch (Exception exception) when (exception is not OperationCanceledException || !Context.RequestAborted.IsCancellationRequested)
      {
        // Expected discovery/PAR failures must not disclose provider bodies or escape as HTTP 500.
        Logger.LogWarning("AT sign-in could not start ({FailureType}).", exception.GetType().Name);
        Response.StatusCode = StatusCodes.Status400BadRequest;
        Response.Headers.CacheControl = "no-store";
        Response.ContentType = "text/html; charset=utf-8";
        await Response.WriteAsync("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>Sign-in unavailable</title><h1>Sign-in could not be started</h1><p>Check your account handle and try again. Your provider may be unavailable or may not support the requested connection.</p><p><a href=\"/\">Return to sign-in</a></p></html>", Context.RequestAborted);
      }
    }

    protected override async Task<HandleRequestResult> HandleRemoteAuthenticateAsync()
    {
        try
        {
            if (!sessions.Enabled || Request.Query["state"].Count != 1) return HandleRequestResult.Fail("Invalid AT callback.");
            var properties = Options.StateDataFormat.Unprotect(sessions.BrowserProperties(Request.Query["state"].ToString()));
            if (properties is null || !ValidateCorrelationId(properties)) return HandleRequestResult.Fail("AT browser correlation failed.");
            var principal = await sessions.Complete(Request.QueryString.ToString(), Context.RequestAborted);
            var did = principal.FindFirst(AtprotoClaimTypes.Did)!.Value;
            if (identities is not null)
                await identities.Link(new ExternalIdentity
                {
                    UserId = did, Provider = Constants.Provider,
                    ProviderKeyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(did))),
                    ClaimsJson = JsonSerializer.Serialize(new { did, handle = principal.FindFirst(AtprotoClaimTypes.Handle)?.Value })
                }, Context.RequestAborted);
            properties.Items.Remove(Constants.RequestedScopes);
            return HandleRequestResult.Success(new AuthenticationTicket(principal, properties, Scheme.Name));
        }
        catch (AtprotoMissingScopesException)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            Response.Headers.CacheControl = "no-store";
            Response.ContentType = "text/html; charset=utf-8";
            await Response.WriteAsync("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\"><title>More access is needed</title><h1>Your account could not connect this access</h1><p>Your account provider did not grant the access needed for this connection. Your previous sign-in has been kept.</p><p>Try an account whose provider supports the requested access, or check the connection settings.</p><p><a href=\"/\">Return to the app</a></p></html>", Context.RequestAborted);
            return HandleRequestResult.Handle();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // SDK exceptions can include provider response bodies. Never reflect them into browser output/logs.
            return HandleRequestResult.Fail("AT authentication could not be verified; start sign-in again.");
        }
    }
}
