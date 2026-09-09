using System;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Koan.Mcp;

/// <summary>
/// Unconditional MCP exclusions and wire names. Schema generation consumes these static facts;
/// request-bound serialization and path admission pass the exclusions to Web's field access owner.
/// Context-free Code Mode conversion uses the same exclusions.
/// </summary>
internal static class McpFieldPolicy
{
    private static readonly CamelCaseNamingStrategy CamelCase = new();

    /// <summary>True when the member must not be serialized into MCP results.</summary>
    public static bool IsExcludedFromOutput(MemberInfo member)
    {
        var attribute = member.GetCustomAttribute<McpIgnoreAttribute>(inherit: true);
        return attribute is not null && (attribute.Direction & McpFieldDirection.Output) != 0;
    }

    /// <summary>True when the member must not appear in input schemas or be set from caller payloads.</summary>
    public static bool IsExcludedFromInput(MemberInfo member)
    {
        var attribute = member.GetCustomAttribute<McpIgnoreAttribute>(inherit: true);
        return attribute is not null && (attribute.Direction & McpFieldDirection.Input) != 0;
    }

    /// <summary>
    /// Resolves the JSON wire name for a member, honoring a Newtonsoft <c>[JsonProperty("name")]</c> rename.
    /// The wire name is the explicit rename when present, otherwise the canonical camelCase member name.
    /// Keeping the schema in sync with this prevents
    /// the input schema from advertising a property name the entity never round-trips.
    /// </summary>
    public static string ResolveWireName(MemberInfo member)
    {
        var jsonProperty = member.GetCustomAttribute<JsonPropertyAttribute>(inherit: true);
        if (jsonProperty is not null && !string.IsNullOrWhiteSpace(jsonProperty.PropertyName))
        {
            return jsonProperty.PropertyName!;
        }

        return CamelCase.GetPropertyName(member.Name, hasSpecifiedName: false);
    }
}
