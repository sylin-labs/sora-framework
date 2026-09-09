using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Instructions;
using Koan.Mcp;
using Koan.Web.Authorization;
using Koan.Web.Attributes;
using Koan.Web.Endpoints;
using Koan.Web.Hooks;
using Koan.Web.Infrastructure;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using JsonException = Newtonsoft.Json.JsonException;

namespace Koan.Mcp.Execution;

public sealed class RequestTranslator
{
    public async Task<RequestTranslation> Translate(
        IServiceProvider services,
        McpEntityRegistration registration,
        McpToolDefinition tool,
        JObject? arguments,
        CancellationToken cancellationToken,
        ClaimsPrincipal? user = null)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));
        if (registration is null) throw new ArgumentNullException(nameof(registration));
        if (tool is null) throw new ArgumentNullException(nameof(tool));

        var args = arguments ?? new JObject();
        var builder = services.GetRequiredService<EntityRequestContextBuilder>();
        var context = BuildContext(builder, registration.EntityType, args, cancellationToken, user);
        var fields = await McpJson.Prepare(registration.EntityType, context.Services, context.User, cancellationToken).ConfigureAwait(false);
        context.BindFieldAccess(fields);
        foreach (var sort in context.Options.Sort) fields.DemandReadPath(sort.Path.DotPath);
        if (tool.Operation is EntityEndpointOperationKind.Collection or EntityEndpointOperationKind.Query)
            fields.DemandFilter(JsonFilterParser.Parse(registration.EntityType, ReadString(args, "filter")));
        if (tool.Operation == EntityEndpointOperationKind.DeleteByQuery)
            fields.DemandFilter(JsonFilterParser.Parse(registration.EntityType, ReadString(args, "query")));

        return tool.Operation switch
        {
            EntityEndpointOperationKind.Collection => new RequestTranslation(
                nameof(IEntityEndpointService<object, object>.GetCollection),
                BuildCollectionRequest(context, args)),
            EntityEndpointOperationKind.Query => new RequestTranslation(
                nameof(IEntityEndpointService<object, object>.Query),
                BuildQueryRequest(context, args)),
            EntityEndpointOperationKind.GetNew => new RequestTranslation(
                nameof(IEntityEndpointService<object, object>.GetNew),
                new EntityGetNewRequest
                {
                    Context = context,
                    Accept = ReadString(args, "accept")
                }),
            EntityEndpointOperationKind.GetById => new RequestTranslation(
                nameof(IEntityEndpointService<object, object>.GetById),
                BuildGetByIdRequest(registration, context, args)),
            EntityEndpointOperationKind.Upsert => new RequestTranslation(
                nameof(IEntityEndpointService<object, object>.Upsert),
                BuildUpsertRequest(registration, context, args, fields)),
            EntityEndpointOperationKind.UpsertMany => new RequestTranslation(
                nameof(IEntityEndpointService<object, object>.UpsertMany),
                BuildUpsertManyRequest(registration, context, args, fields)),
            EntityEndpointOperationKind.Delete => new RequestTranslation(
                nameof(IEntityEndpointService<object, object>.Delete),
                BuildDeleteRequest(registration, context, args)),
            EntityEndpointOperationKind.DeleteMany => new RequestTranslation(
                nameof(IEntityEndpointService<object, object>.DeleteMany),
                BuildDeleteManyRequest(registration, context, args)),
            EntityEndpointOperationKind.DeleteByQuery => new RequestTranslation(
                nameof(IEntityEndpointService<object, object>.DeleteByQuery),
                BuildDeleteByQueryRequest(context, args)),
            EntityEndpointOperationKind.DeleteAll => new RequestTranslation(
                nameof(IEntityEndpointService<object, object>.DeleteAll),
                new EntityDeleteAllRequest
                {
                    Context = context,
                    Set = ReadString(args, "set"),
                    DryRun = ReadBool(args, McpDryRun.ArgumentName) ?? false
                }),
            EntityEndpointOperationKind.Patch => new RequestTranslation(
                nameof(IEntityEndpointService<object, object>.Patch),
                BuildPatchRequest(registration, context, args, fields)),
            _ => throw new JsonException($"Operation '{tool.Operation}' is not supported.")
        };
    }

    private static EntityCollectionRequest BuildCollectionRequest(EntityRequestContext context, JObject args)
    {
        var defaultPolicy = PaginationPolicy.FromAttribute(new PaginationAttribute(), PaginationSafetyBounds.Default);
        var forcePagination = ReadBool(args, "forcePagination") ?? false;
        return new EntityCollectionRequest
        {
            Context = context,
            FilterJson = ReadString(args, "filter"),
            Set = ReadString(args, "set"),
            IgnoreCase = ReadBool(args, "ignoreCase") ?? false,
            With = ReadString(args, "with"),
            Shape = ReadString(args, "shape"),
            ForcePagination = forcePagination,
            ApplyPagination = forcePagination,
            PaginationRequested = forcePagination,
            ClientRequestedAll = false,
            Policy = defaultPolicy,
            IncludeTotalCount = forcePagination && defaultPolicy.IncludeCount,
            AbsoluteMaxRecords = defaultPolicy.AbsoluteMaxRecords,
            Accept = ReadString(args, "accept"),
            BasePath = ReadString(args, "basePath"),
            QueryParameters = BuildQueryParameters(args)
        };
    }

    private static EntityQueryRequest BuildQueryRequest(EntityRequestContext context, JObject args)
    {
        return new EntityQueryRequest
        {
            Context = context,
            FilterJson = ReadString(args, "filter"),
            Set = ReadString(args, "set"),
            IgnoreCase = ReadBool(args, "ignoreCase") ?? false,
            Accept = ReadString(args, "accept")
        };
    }

    private object BuildGetByIdRequest(McpEntityRegistration registration, EntityRequestContext context, JObject args)
    {
        var requestType = typeof(EntityGetByIdRequest<>).MakeGenericType(registration.KeyType);
        var request = Activator.CreateInstance(requestType)!;
        SetProperty(request, nameof(EntityGetByIdRequest<object>.Context), context);
        var idNode = TryGet(args, "id") ?? throw new JsonException("Missing required 'id' parameter.");
        SetProperty(request, nameof(EntityGetByIdRequest<object>.Id), ConvertValue(idNode, registration.KeyType));
        SetProperty(request, nameof(EntityGetByIdRequest<object>.Set), ReadString(args, "set"));
        SetProperty(request, nameof(EntityGetByIdRequest<object>.With), ReadString(args, "with"));
        SetProperty(request, nameof(EntityGetByIdRequest<object>.Accept), ReadString(args, "accept"));
        return request;
    }

    private object BuildUpsertRequest(McpEntityRegistration registration, EntityRequestContext context, JObject args, FieldAccess fields)
    {
        var requestType = typeof(EntityUpsertRequest<,>).MakeGenericType(registration.EntityType, registration.KeyType);
        var request = Activator.CreateInstance(requestType)!;
        SetProperty(request, nameof(EntityUpsertRequest<object, object>.Context), context);
        var modelNode = TryGet(args, "model") ?? throw new JsonException("Missing required 'model' payload.");
        fields.DemandReplacement();
        SetProperty(request, nameof(EntityUpsertRequest<object, object>.Model), ConvertEntity(modelNode, registration.EntityType, fields));
        SetProperty(request, nameof(EntityUpsertRequest<object, object>.Set), ReadString(args, "set"));
        SetProperty(request, nameof(EntityUpsertRequest<object, object>.Accept), ReadString(args, "accept"));
        SetProperty(request, nameof(EntityUpsertRequest<object, object>.DryRun), ReadBool(args, McpDryRun.ArgumentName) ?? false);
        return request;
    }

    private object BuildUpsertManyRequest(McpEntityRegistration registration, EntityRequestContext context, JObject args, FieldAccess fields)
    {
        var requestType = typeof(EntityUpsertManyRequest<>).MakeGenericType(registration.EntityType);
        var request = Activator.CreateInstance(requestType)!;
        SetProperty(request, nameof(EntityUpsertManyRequest<object>.Context), context);
        var modelsNode = TryGet(args, "models") ?? throw new JsonException("Missing required 'models' payload.");
        fields.DemandReplacement();
        SetProperty(request, nameof(EntityUpsertManyRequest<object>.Models), ConvertEntityCollection(modelsNode, registration.EntityType, fields));
        SetProperty(request, nameof(EntityUpsertManyRequest<object>.Set), ReadString(args, "set"));
        SetProperty(request, nameof(EntityUpsertManyRequest<object>.DryRun), ReadBool(args, McpDryRun.ArgumentName) ?? false);
        return request;
    }

    private object BuildDeleteRequest(McpEntityRegistration registration, EntityRequestContext context, JObject args)
    {
        var requestType = typeof(EntityDeleteRequest<>).MakeGenericType(registration.KeyType);
        var request = Activator.CreateInstance(requestType)!;
        SetProperty(request, nameof(EntityDeleteRequest<object>.Context), context);
        var idNode = TryGet(args, "id") ?? throw new JsonException("Missing required 'id' parameter.");
        SetProperty(request, nameof(EntityDeleteRequest<object>.Id), ConvertValue(idNode, registration.KeyType));
        SetProperty(request, nameof(EntityDeleteRequest<object>.Set), ReadString(args, "set"));
        SetProperty(request, nameof(EntityDeleteRequest<object>.Accept), ReadString(args, "accept"));
        SetProperty(request, nameof(EntityDeleteRequest<object>.DryRun), ReadBool(args, McpDryRun.ArgumentName) ?? false);
        return request;
    }

    private object BuildDeleteManyRequest(McpEntityRegistration registration, EntityRequestContext context, JObject args)
    {
        var requestType = typeof(EntityDeleteManyRequest<>).MakeGenericType(registration.KeyType);
        var request = Activator.CreateInstance(requestType)!;
        SetProperty(request, nameof(EntityDeleteManyRequest<object>.Context), context);
        var idsNode = TryGet(args, "ids") ?? throw new JsonException("Missing required 'ids' collection.");
        SetProperty(request, nameof(EntityDeleteManyRequest<object>.Ids), ConvertKeyCollection(idsNode, registration.KeyType));
        SetProperty(request, nameof(EntityDeleteManyRequest<object>.Set), ReadString(args, "set"));
        SetProperty(request, nameof(EntityDeleteManyRequest<object>.DryRun), ReadBool(args, McpDryRun.ArgumentName) ?? false);
        return request;
    }

    private static EntityDeleteByQueryRequest BuildDeleteByQueryRequest(EntityRequestContext context, JObject args)
    {
        var query = ReadString(args, "query");
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new JsonException("Missing required 'query' parameter.");
        }

        return new EntityDeleteByQueryRequest
        {
            Context = context,
            Query = query!,
            Set = ReadString(args, "set"),
            DryRun = ReadBool(args, McpDryRun.ArgumentName) ?? false
        };
    }

    private object BuildPatchRequest(McpEntityRegistration registration, EntityRequestContext context, JObject args, FieldAccess fields)
    {
        var requestType = typeof(EntityPatchRequest<,>).MakeGenericType(registration.EntityType, registration.KeyType);
        var request = Activator.CreateInstance(requestType)!;
        SetProperty(request, nameof(EntityPatchRequest<object, object>.Context), context);
        var idNode = TryGet(args, "id") ?? throw new JsonException("Missing required 'id' parameter.");
        SetProperty(request, nameof(EntityPatchRequest<object, object>.Id), ConvertValue(idNode, registration.KeyType));
        var patchNode = TryGet(args, "patch") ?? throw new JsonException("Missing required 'patch' payload.");
        if (patchNode is not JArray) throw new JsonException("Expected a JSON Patch operations array.");
        fields.DemandPatch(patchNode.ToObject<PatchOp[]>() ?? throw new JsonException("Unable to read JSON Patch operations."));
        SetProperty(request, nameof(EntityPatchRequest<object, object>.Patch), ConvertPatchDocument(patchNode, registration.EntityType));
        SetProperty(request, nameof(EntityPatchRequest<object, object>.Set), ReadString(args, "set"));
        SetProperty(request, nameof(EntityPatchRequest<object, object>.Accept), ReadString(args, "accept"));
        SetProperty(request, nameof(EntityPatchRequest<object, object>.DryRun), ReadBool(args, McpDryRun.ArgumentName) ?? false);
        return request;
    }

    private static EntityRequestContext BuildContext(EntityRequestContextBuilder builder, Type entityType, JObject args, CancellationToken cancellationToken, ClaimsPrincipal? user)
    {
        var options = new QueryOptions();

        var q = ReadString(args, "q");
        if (!string.IsNullOrWhiteSpace(q)) options.Q = q;

        var page = ReadInt(args, "page");
        if (page.HasValue && page.Value > 0) options.Page = page.Value;

        var pageSize = ReadInt(args, "pageSize");
        if (pageSize.HasValue && pageSize.Value > 0) options.PageSize = pageSize.Value;

        var shape = ReadString(args, "shape");
        if (!string.IsNullOrWhiteSpace(shape)) options.Shape = shape!;

        var view = ReadString(args, "view");
        if (!string.IsNullOrWhiteSpace(view)) options.View = view;

        var sort = ReadString(args, "sort");
        if (!string.IsNullOrWhiteSpace(sort))
        {
            // Accept the canonical URL grammar (comma-separated, +/- prefixes) — same as ?sort= on HTTP.
            options.Sort.AddRange(Koan.Data.Core.Sorting.SortSpecParser.ParseStrict(entityType, sort));
        }

        if (args.TryGetValue("extras", StringComparison.OrdinalIgnoreCase, out var extrasNode) && extrasNode is JObject extrasObj)
        {
            foreach (var kv in extrasObj.Properties())
            {
                var val = kv.Value;
                if (val.Type == JTokenType.String)
                    options.Extras[kv.Name] = val.Value<string>() ?? "";
                else if (val.Type != JTokenType.Null)
                    options.Extras[kv.Name] = val.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        // SEC-0004 Phase 3.3: thread the MCP caller's principal (null = anonymous) into EntityRequestContext.User,
        // so the data-layer gate / constrain / projection evaluate against the real caller instead of anonymous.
        var context = builder.Build(options, cancellationToken, httpContext: null, user: user);

        // AN11 — every MCP mutation opts into the state delta (the pre-mutation read + the prospective/
        // retrospective diff). REST stays cost-free until it opts in the same way.
        context.Items[EntityMutationProbe.WantsDeltaKey] = true;

        // SEC-0004 (§C) — the MCP edge opts into the per-row capability projection BY DEFAULT: agents need the
        // `can:[]` manifest to plan, and MCP carries structured metadata natively. REST opts in per request
        // (?access=true). The endpoint computes the manifest once and stashes it for the ResponseTranslator.
        context.Items[Koan.Web.Authorization.AccessProjection.RequestKey] = true;

        // AN9 — the pin: accept a client-supplied correlation id as an opaque, untrusted, authority-free
        // label (mint a time-ordered GUIDv7 when absent). It is threaded into the request for audit
        // stitching only; it gates NOTHING (continuity ≠ authority — see McpCorrelation).
        var correlationId = ReadString(args, McpCorrelation.ArgumentName);
        context.Items[McpCorrelation.ItemsKey] = string.IsNullOrWhiteSpace(correlationId)
            ? Koan.Core.StringId.New()
            : correlationId;

        return context;
    }

    private static JToken? TryGet(JObject args, string property)
    {
        return args.TryGetValue(property, StringComparison.OrdinalIgnoreCase, out var node) ? node : null;
    }

    private static string? ReadString(JObject args, string property)
    {
        return TryGet(args, property)?.Type == JTokenType.String ? TryGet(args, property)!.Value<string>() : TryGet(args, property)?.ToString();
    }

    private static bool? ReadBool(JObject args, string property)
    {
        if (TryGet(args, property) is JValue v && v.Type == JTokenType.Boolean) return v.Value<bool>();
        if (TryGet(args, property) is JValue v2 && v2.Type == JTokenType.String && bool.TryParse(v2.Value<string>(), out var b)) return b;
        return null;
    }

    private static int? ReadInt(JObject args, string property)
    {
        var token = TryGet(args, property);
        if (token == null) return null;
        if (token.Type == JTokenType.Integer) return token.Value<int>();
        if (token.Type == JTokenType.String && int.TryParse(token.Value<string>(), out var i)) return i;
        return null;
    }

    private static IReadOnlyDictionary<string, string?> BuildQueryParameters(JObject args)
    {
        if (!args.TryGetValue("extras", StringComparison.OrdinalIgnoreCase, out var extrasNode) || extrasNode is not JObject extras)
        {
            return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        }

        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in extras.Properties())
        {
            var val = prop.Value;
            if (val.Type == JTokenType.String) dict[prop.Name] = val.Value<string>();
            else if (val.Type != JTokenType.Null) dict[prop.Name] = val.ToString(Newtonsoft.Json.Formatting.None);
        }

        return dict;
    }

    private static void SetProperty(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(propertyName);
        property?.SetValue(target, value);
    }

    private static object ConvertValue(JToken node, Type targetType)
    {
        if (targetType == typeof(string)) return node.Type == JTokenType.String ? (node.Value<string>() ?? "") : node.ToString();

        if (targetType.IsEnum)
        {
            var enumText = node.Type == JTokenType.String ? node.Value<string>() : node.ToString();
            if (string.IsNullOrWhiteSpace(enumText))
                throw new JsonException($"Enum value missing for target type {targetType.Name}.");
            try
            {
                return Enum.Parse(targetType, enumText, ignoreCase: true);
            }
            catch (Exception ex)
            {
                throw new JsonException($"Unable to parse enum value '{enumText}' for {targetType.Name}: {ex.Message}");
            }
        }

        if (targetType == typeof(Guid))
        {
            var text = node.Type == JTokenType.String ? node.Value<string>() : node.ToString();
            if (string.IsNullOrWhiteSpace(text))
                throw new JsonException("Guid value missing or empty.");
            if (!Guid.TryParse(text, out var g))
                throw new JsonException($"Invalid Guid value '{text}'.");
            return g;
        }
        try { return node.ToObject(targetType) ?? throw new JsonException($"Unable to convert value to {targetType.Name}."); }
        catch (Exception ex) { throw new JsonException($"Unable to convert value to {targetType.Name}: {ex.Message}"); }
    }

    private static object ConvertEntity(JToken node, Type entityType, FieldAccess fields)
    {
        try { return node.ToObject(entityType, McpJson.CreateApplicationSerializer(fields)) ?? throw new JsonException($"Unable to deserialize payload as {entityType.Name}."); }
        catch (Exception ex) { throw new JsonException($"Unable to deserialize payload as {entityType.Name}: {ex.Message}"); }
    }

    private static object ConvertEntityCollection(JToken node, Type entityType, FieldAccess fields)
    {
        var listType = typeof(List<>).MakeGenericType(entityType);
        try { return node.ToObject(listType, McpJson.CreateApplicationSerializer(fields)) ?? throw new JsonException($"Unable to deserialize collection payload as {entityType.Name} list."); }
        catch (Exception ex) { throw new JsonException($"Unable to deserialize collection payload as {entityType.Name} list: {ex.Message}"); }
    }

    private static object ConvertKeyCollection(JToken node, Type keyType)
    {
        if (node is not JArray array)
        {
            throw new JsonException("Expected an array of identifiers.");
        }

        var listType = typeof(List<>).MakeGenericType(keyType);
        var list = (IList)Activator.CreateInstance(listType)!;
        foreach (var item in array)
        {
            if (item is null)
            {
                throw new JsonException("Identifier array contains null entry.");
            }

            list.Add(ConvertValue(item, keyType));
        }

        return list;
    }

    private static object ConvertPatchDocument(JToken node, Type entityType)
    {
        var json = node.ToString(Newtonsoft.Json.Formatting.None);
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver()
        };

        var patchType = typeof(JsonPatchDocument<>).MakeGenericType(entityType);
        var document = JsonConvert.DeserializeObject(json, patchType, settings);
        if (document is null)
        {
            throw new JsonException("Unable to deserialize JSON Patch document.");
        }

        return document;
    }
}

public sealed record RequestTranslation(string MethodName, object Request);
