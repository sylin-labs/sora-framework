---
type: GUIDE
domain: mcp
title: "MCP over HTTP"
audience: [developers, architects, ai-agents]
status: current
last_updated: 2026-09-10
framework_version: v1.0.0
validation:
  date_last_tested: 2026-07-19
  status: verified
  scope: Streamable HTTP source contract, security posture, and focused integration evidence
related_guides:
  - entity-capabilities-howto.md
  - authorization-howto.md
  - oauth-server-howto.md
---

# MCP over HTTP

Use this surface when an MCP client has to reach the application across a network. When a local client
owns the server process and no HTTP boundary is involved, STDIO is the simpler answer and stays the
default.

What it needs is small: a Koan Web application, a reference to `Koan.Mcp`, at least one `[McpEntity]`
or `[McpTool]` surface, and `Koan:Mcp:EnableStreamableHttpTransport=true`. What it gives back is one
Streamable HTTP endpoint -- `/mcp` unless you move it -- carrying the tools and resources *that
caller* may use.

A working exchange looks like this: the client initializes, receives a session id, discovers only the
tools its identity can invoke, calls them through `POST /mcp`, and can read the same runtime facts an
operator sees. Everything that goes wrong does so nameably -- a rejected protocol version, a missing or
expired session, content negotiation the endpoint cannot satisfy, a tool that is not available, or a
caller the gate denies.

## Shortest supported path

MCP is a supported extension, and the repository verifies this path from source and from the packaged
candidate alike.

Reference Koan MCP and Web in the application closure, then expose business data or workflows:

```csharp
using Koan.Data.Core.Model;
using Koan.Mcp;

[McpEntity(
    Name = "product",
    Description = "A product in the public catalog",
    AllowMutations = false)]
public sealed class Product : Entity<Product>
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool IsDiscontinued { get; set; }
}
```

Keep the normal Koan bootstrap:

```csharp
using Koan.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddKoan();
var app = builder.Build();
await app.RunAsync();
```

Enable the network transport:

```json
{
  "Koan": {
    "Mcp": {
      "EnableStreamableHttpTransport": true,
      "Exposure": "Tools"
    }
  }
}
```

The package reference is the registration: `AddKoan()` maps the protocol routes and owns the host
handle. Application code declares entities and leaves the wiring to the module.

## Streamable HTTP sequence

The same base route handles the complete session:

| Request | Required intent | Result |
|---|---|---|
| `POST /mcp` with `initialize` | Start protocol negotiation without a session header | `200`; response includes `Mcp-Session-Id` |
| `POST /mcp` with a request | Send `tools/list`, `tools/call`, `resources/*`, or `ping` | JSON-RPC response as per-request SSE, or JSON when configured |
| `POST /mcp` with a notification | Send a message with no JSON-RPC id | `202` with no body |
| `GET /mcp` | Open the optional standalone server-push stream | Resumable SSE; one open GET stream per session |
| `DELETE /mcp` | End the current session | `200`; later use of the id returns `404` |

For `POST`, send `Accept: application/json, text/event-stream`. The first request carries the
protocol version in `initialize.params`. Later requests echo both `Mcp-Session-Id` and
`MCP-Protocol-Version`.

```bash
curl -i -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1.0"}}}'
```

Read `Mcp-Session-Id` from the response headers, then list tools:

```bash
curl -X POST http://localhost:5000/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -H "Mcp-Session-Id: <session-id>" \
  -H "MCP-Protocol-Version: 2025-06-18" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
```

A normal MCP client performs this negotiation. Application code should not implement JSON-RPC or
session handling.

## Tool authoring and discovery

`[McpEntity]` projects applicable Entity operations through the same endpoint service and access
rules as REST. `AllowMutations=false` removes generated mutation tools; it is not authorization.

Use a custom tool for a business workflow rather than recreating Entity CRUD:

```csharp
public sealed class ProductTools : Toolset
{
    [McpTool(
        Name = "product_discontinue",
        Description = "Discontinues a product that is no longer sold.",
        IsMutation = true)]
    public async Task<string> Discontinue(string id, CancellationToken ct = default)
    {
        var product = await Product.Get(id, ct);
        if (product is null) return "Product not found.";

        product.IsDiscontinued = true;
        await product.Save(ct);
        return "Product discontinued.";
    }
}
```

Give workflow mutations `IsMutation=true`. Their input schema advertises the reserved
`dry_run: boolean` control. Koan cannot inspect arbitrary imperative effects, so `dry_run=true` does
not execute a custom verb; it returns an honest partial rehearsal with
`meta.diagnostics.rehearsable=false`. Generated Entity mutations can run the framework-owned
validation path and return a prospective state delta without committing.

Ordinary Entity property names are valid schema descriptions. Add `[McpDescription]`,
`[Description]`, or `[Display(Description=...)]` when a property needs richer agent guidance; missing
optional prose is not a startup warning.

Use `[McpIgnore]` for fields that must not cross the agent surface. Directional input/output
exclusion is supported. This is a static type-level projection, not a per-caller data policy; use
Entity access declarations and constraints for caller-specific authority.

## Inspect the running surface

- `tools/list` is the authoritative caller-visible tool catalog.
- `koan://self` introduces the usable Entity and custom-workflow surface.
- `koan://entities` describes Entity tools only; it may be empty in a custom-tool-only application.
- `koan://facts` returns the same versioned, redacted runtime fact envelope as
  `/.well-known/Koan/facts`.

Advertisement and invocation use the same caller-aware projection. A disabled or unauthorized tool
is absent from discovery and cannot be reached by calling its name directly.

## Security posture

HTTP authentication defaults on in Production and containers. Development may be open for local
inspection. For a production edge:

1. serve MCP over HTTPS;
2. configure `ResourceUri` to the externally stable `/mcp` audience when behind a proxy;
3. reference `Koan.Web.Auth.Server` for Koan's OAuth 2.1 authorization-server/resource-server path,
   or register the application’s external bearer scheme;
4. declare Entity access and custom-tool scopes for business authority;
5. configure `AllowedOrigins` when browser clients are permitted;
6. keep operational toolsets disabled unless explicitly needed and grant-gated.

See [OAuth server](oauth-server-howto.md) for token acquisition and
[authorization](authorization-howto.md) for the Entity/tool authority model.

## Configuration reference

| Option | Default | Meaning |
|---|---:|---|
| `EnableStdioTransport` | `true` | Host the local process-owned transport |
| `EnableStreamableHttpTransport` | `false` | Host the primary Streamable HTTP edge |
| `EnableLegacySseTransport` | `false` | Host the earlier `/sse` + `/rpc` two-endpoint edge for clients that use it |
| `HttpRoute` | `/mcp` | Shared base route for MCP over HTTP |
| `RequireAuthentication` | environment-derived | Require a remote authenticated principal |
| `ResourceUri` | unset | Fixed OAuth resource/audience identifier |
| `MaxConcurrentSessions` | `100` | Bound active HTTP sessions |
| `SessionIdleTimeout` | `30 minutes` | Reclaim idle sessions after this interval |
| `EnableCors` | `false` | Enable the MCP CORS policy |
| `AllowedOrigins` | empty | Origins admitted when CORS is enabled |
| `Transport.StreamableJsonResponse` | `false` | Return JSON instead of per-request SSE for request POSTs |
| `Transport.StreamReplayBufferSize` | `256` | Recent events retained per stream for resume |
| `Transport.MaxRetainedStreamsPerSession` | `64` | Bound completed request streams retained per session |

## Failure interpretation

| Observation | Meaning | Action |
|---|---|---|
| `400`, error `-32000` | Non-initialize request has no session | Initialize first and echo the session id |
| `400` after negotiation | Unsupported protocol contract | Send the negotiated `MCP-Protocol-Version` |
| `401` | Remote authentication failed | Follow the protected-resource challenge or fix bearer configuration |
| `403` or a structured short-circuit | Caller lacks business authority | Correct grants; do not retry unchanged |
| `404`, error `-32001` | Session expired or was terminated | Re-initialize and replace the session id |
| `406` | POST does not accept the supported response media types | Send both JSON and event-stream in `Accept` |
| `409` on `GET /mcp` | A standalone GET stream is already open | Reuse or close the existing stream |
| Tool absent from `tools/list` | Not composed, disabled, or not permitted for this caller | Inspect `koan://self`, `koan://facts`, and access configuration |

## Verified evidence and limits

Run the current real-host Streamable HTTP evidence from a source checkout:

```powershell
dotnet test tests/Suites/Mcp/Koan.Mcp.Streamable.IntegrationTests/Koan.Mcp.Streamable.IntegrationTests.csproj -c Release
```

The suite covers negotiation, content handling, session mint/resolve/terminate, authorization,
Explorer coexistence, server push, and resumption. The broader conformance suite covers tool schema,
access projection, resources, dry-run behavior, and Entity execution.

Current evidence does not certify multi-node session sharing, every reverse proxy, every MCP client,
or exactly-once custom-tool effects. Sessions are process-owned; a distributed deployment must route
a session consistently or re-initialize after it moves.

See [AI-0037](../decisions/AI-0037-mcp-streamable-http-transport.md) for the transport decision and
the [MCP module contract](../../src/Koan.Mcp/README.md) for the shortest application-facing summary.
