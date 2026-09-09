using System;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Koan.Mcp.Execution;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Koan.Mcp.CustomTools;

/// <summary>
/// Binds a custom tool's parameters (call arguments + injected <see cref="IServiceProvider"/> /
/// <see cref="CancellationToken"/>), invokes the static method, awaits a <see cref="Task"/>/
/// <see cref="ValueTask"/> result, and serializes the return value to a JSON token.
/// </summary>
public sealed class McpCustomToolInvoker
{
    public async Task<JToken> Invoke(McpCustomTool tool, JObject? arguments, IServiceProvider services, ClaimsPrincipal? principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(services);

        var args = new object?[tool.Parameters.Count];
        for (var i = 0; i < tool.Parameters.Count; i++)
        {
            var parameter = tool.Parameters[i];
            args[i] = parameter.Source switch
            {
                McpCustomToolParameterSource.CancellationToken => cancellationToken,
                McpCustomToolParameterSource.ServiceProvider => services,
                McpCustomToolParameterSource.Principal => principal,
                _ => await BindArgument(parameter, arguments, services, principal, cancellationToken).ConfigureAwait(false)
            };
        }

        // ARCH-0092 §H: static verbs invoke with a null target; an instance verb on a Toolset is invoked on a
        // DI-created instance (constructor dependencies resolved from the request scope).
        var target = tool.Method.IsStatic
            ? null
            : ActivatorUtilities.CreateInstance(services, tool.Method.DeclaringType!);

        object? result;
        try
        {
            result = tool.Method.Invoke(target, args);
        }
        catch (TargetInvocationException tie) when (tie.InnerException is not null)
        {
            // Surface the real exception (the RPC layer maps it to a JSON-RPC error).
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(tie.InnerException);
            throw; // unreachable
        }

        switch (result)
        {
            case Task task:
                await task.ConfigureAwait(false);
                result = GetTaskResult(task);
                break;
            case ValueTask valueTask:
                await valueTask.ConfigureAwait(false);
                result = null;
                break;
            case { } genericValueTask when genericValueTask.GetType().IsGenericType
                && genericValueTask.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>):
                var asTask = (Task)genericValueTask.GetType().GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(genericValueTask, null)!;
                await asTask.ConfigureAwait(false);
                result = GetTaskResult(asTask);
                break;
        }

        return await McpJson.FromApplicationObject(result, services, principal, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> BindArgument(McpCustomToolParameter parameter, JObject? arguments,
        IServiceProvider services, ClaimsPrincipal? principal, CancellationToken cancellationToken)
    {
        if (arguments is not null
            && arguments.TryGetValue(parameter.Name, StringComparison.OrdinalIgnoreCase, out var node)
            && node.Type != JTokenType.Null)
        {
            var fields = await McpJson.Prepare(parameter.Type, services, principal, cancellationToken).ConfigureAwait(false);
            fields.DemandReplacement();
            try
            {
                return node.ToObject(parameter.Type, McpJson.CreateApplicationSerializer(fields));
            }
            catch (Exception ex) when (!fields.HasRestrictions &&
                (ex is JsonException or FormatException or InvalidCastException or ArgumentException or OverflowException))
            {
                // A supplied value that cannot bind to the target type falls through to the default (lenient
                // binding, by design). F2 burn-down: the catch is narrowed to conversion failures so an UNEXPECTED
                // exception is no longer swallowed under the same fallthrough.
            }
        }

        if (parameter.DefaultValue is not null)
        {
            return parameter.DefaultValue;
        }

        return parameter.Type.IsValueType && Nullable.GetUnderlyingType(parameter.Type) is null
            ? Activator.CreateInstance(parameter.Type)
            : null;
    }

    private static object? GetTaskResult(Task task)
    {
        var type = task.GetType();
        if (!type.IsGenericType) return null; // non-generic Task → void

        var value = type.GetProperty("Result")?.GetValue(task);
        // Task (non-generic) surfaces as Task<VoidTaskResult> internally — treat as void.
        return value is not null && value.GetType().Name == "VoidTaskResult" ? null : value;
    }
}
