using Koan.Data.Core.Model;
using Koan.Web.Authorization;
using Newtonsoft.Json;

namespace Koan.Mcp.Conformance.Tests;

[McpEntity(Name = "field-record")]
[Access(read: Access.Anyone, write: Access.Anyone, remove: Access.Anyone)]
public sealed class FieldAccessRecord : Entity<FieldAccessRecord>
{
    public string Title { get; set; } = "";

    [Access(read: "is:admin", write: "is:admin")]
    [JsonProperty("claim_ids")]
    public List<string> Claimants { get; set; } = [];

    [McpIgnore]
    public string InternalSecret { get; set; } = "";

    [McpIgnore(McpFieldDirection.Output)]
    public string WriteOnlyToken { get; set; } = "";

    public Detail Metadata { get; set; } = new();

    public sealed class Detail
    {
        public string Label { get; set; } = "";

        [Access(read: "is:admin", write: "is:admin")]
        [JsonProperty("operator_code")]
        public string OperatorCode { get; set; } = "";
    }
}
