using Koan.Data.Core.Model;
using Koan.Data.Core.Relationships;
using Koan.Web.Authorization;

namespace Koan.Mcp.Conformance.Tests;

[McpEntity(Name = "field-shape-record")]
[Access(read: Access.Anyone, write: Access.Anyone, remove: Access.Anyone)]
public sealed class FieldShapeRecord : Entity<FieldShapeRecord>
{
    [McpIgnore(McpFieldDirection.Output)]
    public string Name { get; set; } = "";

    public string Title { get; set; } = "";

    [Parent(typeof(FieldAccessRecord))]
    public string? ParentId { get; set; }
}
