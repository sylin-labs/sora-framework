using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Koan.Mcp.Execution;

namespace Koan.Mcp.CodeMode.Json;

internal sealed class NewtonsoftJsonFacade : IJsonFacade
{
    private static readonly JsonSerializer Serializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = McpContractResolver.Instance,
        NullValueHandling = NullValueHandling.Ignore,
        DateParseHandling = DateParseHandling.DateTimeOffset,
        FloatParseHandling = FloatParseHandling.Decimal,
        Converters = { McpJson.EnumNames }
    });

    public JToken Parse(string json) => JToken.Parse(json);

    public string Stringify(JToken token, bool indented = false)
        => token.ToString(indented ? Formatting.Indented : Formatting.None);

    public JToken FromObject(object? value)
        => value == null ? JValue.CreateNull() : JToken.FromObject(value, Serializer);

    public T? ToObject<T>(JToken token)
        => token.ToObject<T>(Serializer);

    public object? ToDynamic(JToken token)
        => ConvertToken(token);

    private static object? ConvertToken(JToken token)
    {
        return token switch
        {
            JObject obj => ToDynamicObject(obj),
            JArray arr => ToList(arr),
            JValue val => val.Value,
            _ => null
        };
    }

    private static object ToDynamicObject(JObject obj)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.Properties())
        {
            dict[prop.Name] = ConvertToken(prop.Value);
        }
        return dict; // Jint can treat Dictionary<string,object?> like a dynamic object via property access.
    }

    private static List<object?> ToList(JArray arr)
    {
        var list = new List<object?>(arr.Count);
        foreach (var el in arr)
        {
            list.Add(ConvertToken(el));
        }
        return list;
    }
}
