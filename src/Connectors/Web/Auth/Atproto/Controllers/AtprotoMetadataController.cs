using Koan.Web.Auth.Connector.Atproto.Infrastructure;
using Koan.Web.Auth.Connector.Atproto.Protocol;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Koan.Web.Auth.Connector.Atproto.Controllers;

[ApiController, AllowAnonymous]
public sealed class AtprotoMetadataController(AtprotoSessions sessions) : ControllerBase
{
    [HttpGet(Constants.Metadata)]
    public IActionResult Metadata()
    {
        if (!sessions.Enabled) return NotFound();
        return Ok(new
        {
            client_id = sessions.ClientId,
            application_type = "web",
            grant_types = new[] { "authorization_code", "refresh_token" },
            scope = string.Join(' ', sessions.AllowedScopes),
            response_types = new[] { "code" },
            redirect_uris = new[] { sessions.RedirectUri },
            token_endpoint_auth_method = "none",
            dpop_bound_access_tokens = true
        });
    }
}
