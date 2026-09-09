using Koan.Web.Authorization;
using Newtonsoft.Json;

namespace Koan.Mcp.Conformance.Tests;

public static class FieldAccessTools
{
    public static int InputCalls;

    [McpTool(Name = "field-access-detail")]
    public static Task<FieldAccessRecord.Detail> Detail()
        => Task.FromResult(new FieldAccessRecord.Detail { Label = "public-custom", OperatorCode = "custom-operator-secret" });

    [McpTool(Name = "field-access-value-detail")]
    public static ValueTask<FieldAccessRecord.Detail> ValueDetail()
        => ValueTask.FromResult(new FieldAccessRecord.Detail { Label = "public-custom", OperatorCode = "custom-operator-secret" });

    [McpTool(Name = "field-access-input")]
    public static string Input(FieldAccessRecord.Detail detail)
    {
        Interlocked.Increment(ref InputCalls);
        return detail.Label;
    }

    [McpTool(Name = "field-access-constructor-input")]
    public static string ConstructorInput(ConstructorDetail detail)
    {
        Interlocked.Increment(ref InputCalls);
        return detail.OperatorCode ?? detail.Label;
    }

    [McpTool(Name = "field-access-field-input")]
    public static string FieldInput(FieldDetail detail)
    {
        Interlocked.Increment(ref InputCalls);
        return detail.Secret;
    }

    public sealed class FieldDetail
    {
        public string Label { get; set; } = "";

        [McpIgnore]
        public string Secret = "default-private-field";
    }

    [McpTool(Name = "field-access-polymorphic")]
    public static DynamicResult Polymorphic()
        => new() { Value = new FieldAccessRecord.Detail { OperatorCode = "unprepared-secret" } };

    public sealed class DynamicResult
    {
        public object Value { get; set; } = new();
    }

    public sealed class ConstructorDetail
    {
        [JsonConstructor]
        public ConstructorDetail(string label, string? operatorCode)
        {
            Label = label;
            OperatorCode = operatorCode;
        }

        public string Label { get; }

        [Access(read: "is:admin", write: "is:admin")]
        [JsonProperty("operator_code")]
        public string? OperatorCode { get; }
    }

    [JsonConverter(typeof(DetailContainerConverter))]
    public sealed class DetailContainer
    {
        public FieldAccessRecord.Detail Value { get; set; } = new();
    }

    public sealed class DetailContainerConverter : JsonConverter<DetailContainer>
    {
        public static int Calls;

        public override void WriteJson(JsonWriter writer, DetailContainer? value, JsonSerializer serializer)
        {
            Interlocked.Increment(ref Calls);
            writer.WriteValue(value!.Value.OperatorCode);
        }

        public override DetailContainer? ReadJson(JsonReader reader, Type objectType, DetailContainer? existingValue,
            bool hasExistingValue, JsonSerializer serializer)
        {
            Interlocked.Increment(ref Calls);
            return new() { Value = new() { OperatorCode = reader.Value?.ToString() ?? "" } };
        }
    }
}
