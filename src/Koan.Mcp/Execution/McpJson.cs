using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Koan.Web.Authorization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Koan.Mcp.Execution;

/// <summary>Single JSON policy for application-owned MCP inputs and outputs.</summary>
internal static class McpJson
{
    private static readonly JsonSerializerSettings Template = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public static Task<FieldAccess> Prepare(Type rootType, IServiceProvider services,
        ClaimsPrincipal? principal, CancellationToken ct)
        => FieldAccess.Prepare(rootType, services, principal ?? new ClaimsPrincipal(), ct,
            McpFieldPolicy.IsExcludedFromInput, McpFieldPolicy.IsExcludedFromOutput);

    public static JsonSerializer CreateApplicationSerializer(FieldAccess access)
        => JsonSerializer.Create(access.CreateSerializerSettings(Template));

    public static async Task<JToken> FromApplicationObject(object? value, IServiceProvider services,
        ClaimsPrincipal? principal, CancellationToken ct)
    {
        if (value is null) return JValue.CreateNull();
        // Already-built JSON has no CLR member provenance. Typed results are governed before this boundary.
        if (value is JToken token) return token;
        var access = await Prepare(value.GetType(), services, principal, ct).ConfigureAwait(false);
        return JToken.FromObject(value, CreateApplicationSerializer(access));
    }
}
