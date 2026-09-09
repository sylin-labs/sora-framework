using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Koan.Web.Endpoints;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Koan.Mcp.Execution;

public sealed class ResponseTranslator
{
    public async Task<McpToolExecutionResult> Translate(McpEntityRegistration registration, McpToolDefinition tool, EntityEndpointResult result)
    {
        if (registration is null) throw new ArgumentNullException(nameof(registration));
        if (tool is null) throw new ArgumentNullException(nameof(tool));
        if (result is null) throw new ArgumentNullException(nameof(result));

        var payload = await SerializePayload(result).ConfigureAwait(false);
        var shortCircuit = await SerializeShortCircuit(result).ConfigureAwait(false);
        var headers = result.Headers.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
        var warnings = result.Warnings.ToArray();

        var diagnostics = new JObject
        {
            ["entity"] = registration.DisplayName,
            ["operation"] = tool.Operation.ToString(),
            ["shortCircuited"] = result.IsShortCircuited
        };

        // AN9 — echo the pin so the agent can stitch its trajectory across calls. Authority-free: it is
        // diagnostics only, never a credential.
        if (result.Context.Items.TryGetValue(McpCorrelation.ItemsKey, out var correlation) && correlation is string pin)
        {
            diagnostics["correlationId"] = pin;
        }

        // AN11 — project the dry-run posture + the semantic state delta (prospective on a rehearsal,
        // retrospective on a real run; identical shape). Walled-means-silent is enforced inside the projector.
        var (dryRun, delta) = await MutationDeltaProjector.Project(registration.EntityType, result).ConfigureAwait(false);
        if (dryRun) diagnostics["dryRun"] = true;
        if (delta is not null) diagnostics["delta"] = delta;

        if (shortCircuit is JObject scObj && scObj.TryGetValue("statusCode", out var statusToken) && statusToken.Type == JTokenType.Integer)
        {
            diagnostics["shortCircuitStatusCode"] = statusToken;
        }

        if (shortCircuit is JObject scObj2 && scObj2.TryGetValue("type", out var typeToken) && typeToken.Type == JTokenType.String && !string.IsNullOrWhiteSpace(typeToken.ToString()))
        {
            diagnostics["shortCircuitType"] = typeToken.ToString();
        }

        if (!result.IsShortCircuited && result.GetType().IsGenericType
            && result.GetType().GetGenericTypeDefinition() == typeof(EntityCollectionResult<>))
        {
            if (result.GetType().GetProperty(nameof(EntityCollectionResult<object>.TotalCount))?.GetValue(result) is long totalCount)
            {
                diagnostics["totalCount"] = totalCount;
            }
        }

        // SEC-0004 (§C) — the per-row capability manifest (id → { can }), default-on for agents. Computed once in
        // the shared endpoint and read off the context here, rendered into the tool-result metadata. A single-item
        // operation carries its verbs in the Koan-Access header (already in `headers`) instead.
        if (result.Context.Items.TryGetValue(Koan.Web.Authorization.AccessProjection.ManifestKey, out var manifest) && manifest is not null)
        {
            diagnostics["access"] = await SerializeObject(manifest, result.Context).ConfigureAwait(false);
        }

        return McpToolExecutionResult.SuccessResult(payload, shortCircuit, headers, warnings, diagnostics);
    }

    private static async Task<JToken?> SerializeShortCircuit(EntityEndpointResult result)
    {
        if (!result.IsShortCircuited) return null;
        if (result.ShortCircuitResult is IActionResult actionResult) return await SerializeActionResult(actionResult, result.Context).ConfigureAwait(false);
        return await SerializeObject(result.ShortCircuitPayload, result.Context).ConfigureAwait(false);
    }

    private static async Task<JObject> SerializeActionResult(IActionResult actionResult, EntityRequestContext context)
    {
        var obj = new JObject { ["type"] = actionResult.GetType().Name };
        switch (actionResult)
        {
            case ObjectResult objectResult:
                if (objectResult.StatusCode.HasValue) obj["statusCode"] = objectResult.StatusCode.Value;
                if (objectResult.Value is not null) obj["payload"] = await SerializeObject(objectResult.Value, context).ConfigureAwait(false);
                if (objectResult.DeclaredType is not null) obj["declaredType"] = objectResult.DeclaredType.Name;
                break;
            case JsonResult jsonResult:
                if (jsonResult.StatusCode.HasValue) obj["statusCode"] = jsonResult.StatusCode.Value;
                if (jsonResult.Value is not null) obj["payload"] = await SerializeObject(jsonResult.Value, context).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(jsonResult.ContentType)) obj["contentType"] = jsonResult.ContentType;
                break;
            case ContentResult contentResult:
                obj["statusCode"] = contentResult.StatusCode ?? 200;
                obj["contentType"] = contentResult.ContentType ?? "text/plain";
                obj["content"] = contentResult.Content ?? "";
                break;
            case UnauthorizedResult:
                obj["statusCode"] = 401; break;
            case EmptyResult:
                obj["statusCode"] = 204; break;
            case StatusCodeResult statusCodeResult:
                obj["statusCode"] = statusCodeResult.StatusCode; break;
            case RedirectResult redirectResult:
                obj["statusCode"] = redirectResult.Permanent ? 301 : 302;
                obj["location"] = redirectResult.Url ?? "";
                obj["permanent"] = redirectResult.Permanent; break;
            case RedirectToRouteResult routeResult:
                obj["statusCode"] = routeResult.Permanent ? 301 : 302;
                obj["routeName"] = routeResult.RouteName ?? "";
                if (routeResult.RouteValues is not null) obj["routeValues"] = await SerializeObject(routeResult.RouteValues, context).ConfigureAwait(false);
                break;
            case ChallengeResult challengeResult:
                obj["statusCode"] = 401;
                obj["schemes"] = JArray.FromObject(challengeResult.AuthenticationSchemes ?? []);
                if (challengeResult.Properties is not null) obj["properties"] = await SerializeObject(challengeResult.Properties, context).ConfigureAwait(false);
                break;
            case ForbidResult forbidResult:
                obj["statusCode"] = 403;
                obj["schemes"] = JArray.FromObject(forbidResult.AuthenticationSchemes ?? []);
                if (forbidResult.Properties is not null) obj["properties"] = await SerializeObject(forbidResult.Properties, context).ConfigureAwait(false);
                break;
            default:
                if (actionResult is FileResult fileResult)
                {
                    obj["statusCode"] = 200;
                    obj["contentType"] = fileResult.ContentType ?? "";
                    if (!string.IsNullOrEmpty(fileResult.FileDownloadName)) obj["fileName"] = fileResult.FileDownloadName;
                }
                break;
        }
        return obj;
    }

    private static async Task<JToken?> SerializePayload(EntityEndpointResult result)
    {
        if (result.Payload is not null) return await SerializeObject(result.Payload, result.Context).ConfigureAwait(false);
        var resultType = result.GetType();
        if (resultType.IsGenericType)
        {
            var definition = resultType.GetGenericTypeDefinition();
            if (definition == typeof(EntityCollectionResult<>))
            {
                var items = resultType.GetProperty(nameof(EntityCollectionResult<object>.Items))?.GetValue(result);
                return await SerializeObject(items, result.Context).ConfigureAwait(false);
            }
            if (definition == typeof(EntityModelResult<>))
            {
                var model = resultType.GetProperty(nameof(EntityModelResult<object>.Model))?.GetValue(result);
                return await SerializeObject(model, result.Context).ConfigureAwait(false);
            }
        }
        return null;
    }

    private static async Task<JToken?> SerializeObject(object? value, EntityRequestContext context)
    {
        if (value is null) return null;
        if (value is JToken token) return token;
        try { return await McpJson.FromApplicationObject(value, context.Services, context.User, context.CancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // F2 burn-down: a tool result that cannot be serialised is an ERROR, not a null result. Mirror
            // RequestTranslator's rethrow-as-JsonException-with-context pattern so the dispatch boundary logs it and
            // maps it to a JSON-RPC error — instead of silently handing the client a null payload.
            throw new JsonException($"Unable to serialise MCP tool result of type {value.GetType().Name}: {ex.Message}", ex);
        }
    }
}
