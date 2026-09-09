using System;
using System.Reflection;
using Koan.Web.Endpoints;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;

namespace Koan.Mcp.Execution;

/// <summary>
/// AN11 (docs/assessment/09 §14 — A2) — projects the semantic state delta an entity mutation produces, from
/// the pre-mutation "before" the endpoint stashed (<see cref="EntityMutationProbe"/>) and the result "after".
/// The SAME computation serves both faces: a dry-run's prospective delta and a real run's retrospective one
/// are identical in shape (rehearse → execute → same diff).
///
/// Walled-means-silent is enforced HERE because the projection is the agent-facing edge: an
/// <c>[McpIgnore(Output)]</c> field is ABSENT from the delta (not redacted), and field names are the wire
/// names (honoring a Newtonsoft rename) — the same <see cref="McpFieldPolicy"/> the result serializer uses.
/// </summary>
internal static class MutationDeltaProjector
{
    // Mirrors the result serializer (honors [McpIgnore(Output)]) and renders enums as their member names so
    // a delta value matches the agent-facing wire representation of the field.
    private static readonly JsonSerializer ValueSerializer = JsonSerializer.Create(new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore,
        ContractResolver = McpContractResolver.Instance,
        Converters = { new StringEnumConverter() }
    });

    /// <summary>Builds the (dryRun, delta) pair from the result's stashed mutation probe. Either may be
    /// default when the operation produced no delta (e.g. a read, or a not-found short-circuit).</summary>
    public static (bool DryRun, JObject? Delta) Project(Type entityType, EntityEndpointResult result)
    {
        var items = result.Context.Items;
        var dryRun = items.TryGetValue(EntityMutationProbe.DryRunKey, out var dr) && dr is true;
        if (result.IsShortCircuited) return (dryRun, null);

        // Batch mutations carry a count-level delta (no per-field diff).
        if (items.TryGetValue(EntityMutationProbe.AffectedCountKey, out var ac) && ac is int affected)
        {
            var batchOp = items.TryGetValue(EntityMutationProbe.OperationKey, out var bo) ? bo as string : null;
            return (dryRun, new JObject { ["operation"] = batchOp, ["affected"] = affected, ["dryRun"] = dryRun });
        }

        if (!items.TryGetValue(EntityMutationProbe.OperationKey, out var opObj) || opObj is not string operation)
        {
            return (dryRun, null);
        }

        items.TryGetValue(EntityMutationProbe.BeforeKey, out var before);
        var after = ExtractModel(result);

        return (dryRun, new JObject
        {
            ["operation"] = operation,
            ["dryRun"] = dryRun,
            ["changes"] = ComputeChanges(entityType, operation, before, after)
        });
    }

    private static object? ExtractModel(EntityEndpointResult result)
    {
        var t = result.GetType();
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(EntityModelResult<>))
        {
            return t.GetProperty(nameof(EntityModelResult<object>.Model))?.GetValue(result);
        }
        return null;
    }

    private static JArray ComputeChanges(Type entityType, string operation, object? before, object? after)
    {
        var changes = new JArray();

        // A delete's effect is removal — there are no per-field transitions to enumerate; the operation
        // (plus the entity id carried in the result) is the whole story.
        if (string.Equals(operation, "delete", StringComparison.Ordinal)) return changes;

        var isCreate = string.Equals(operation, "create", StringComparison.Ordinal);

        foreach (var property in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetGetMethod() is null) continue;
            if (property.GetIndexParameters().Length > 0) continue;
            // The identity key is not a data transition (it is the entity's name, already in the payload).
            if (string.Equals(property.Name, "Id", StringComparison.Ordinal)) continue;
            // Walled-means-silent: an output-excluded field is absent from the delta, never redacted.
            if (McpFieldPolicy.IsExcludedFromOutput(property)) continue;

            var fromVal = before is null ? null : property.GetValue(before);
            var toVal = after is null ? null : property.GetValue(after);

            // create: surface the values being established (skip defaults — they are implied, not transitions).
            // update: surface only fields whose value actually changed.
            var changed = isCreate ? !IsDefault(toVal) : !ValuesEqual(fromVal, toVal);
            if (!changed) continue;

            changes.Add(new JObject
            {
                ["field"] = McpFieldPolicy.ResolveWireName(property),
                ["from"] = ToToken(fromVal),
                ["to"] = ToToken(toVal)
            });
        }

        return changes;
    }

    private static bool ValuesEqual(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return a.Equals(b);
    }

    private static bool IsDefault(object? v)
    {
        if (v is null) return true;
        if (v is string s) return s.Length == 0;
        var t = v.GetType();
        return t.IsValueType && v.Equals(Activator.CreateInstance(t));
    }

    private static JToken ToToken(object? v) => v is null ? JValue.CreateNull() : JToken.FromObject(v, ValueSerializer);
}
