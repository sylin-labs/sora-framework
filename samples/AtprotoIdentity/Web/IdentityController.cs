using Koan.Web.Auth.Connector.Atproto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AtprotoIdentity.Web;

[ApiController]
public sealed class IdentityController : ControllerBase
{
    [HttpGet("/")]
    public ContentResult Home() => Content("""
        <!doctype html><html lang="en"><meta charset="utf-8"><title>AT identity</title>
        <h1>Sign in with your AT identity</h1>
        <form action="/auth/atproto/challenge" method="get">
          <label>Handle or DID <input name="identifier" required autocomplete="username"></label>
          <input type="hidden" name="return" value="/sample/identity"><button>Continue</button>
        </form><p>Your account provider handles authentication. This sample requests only identity access.</p>
        </html>
        """, "text/html");

    [Authorize, HttpGet("/sample/identity")]
    public object Me() => new { did = User.FindFirst(AtprotoClaimTypes.Did)?.Value, handle = User.FindFirst(AtprotoClaimTypes.Handle)?.Value };
}
