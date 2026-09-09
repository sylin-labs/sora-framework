using System.Buffers;
using Koan.Web.Authorization;
using Koan.Web.Endpoints;
using Koan.Web.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;

namespace Koan.Web.Serialization;

/// <summary>Retains MVC's Newtonsoft formatters and buffering, replacing only their per-operation settings.</summary>
internal sealed class FieldAccessMvcOptions(IOptions<MvcNewtonsoftJsonOptions> json,
    ObjectPoolProvider pools, ILoggerFactory logs) : IPostConfigureOptions<MvcOptions>
{
    public void PostConfigure(string? name, MvcOptions options)
    {
        for (var i = 0; i < options.InputFormatters.Count; i++)
            if (options.InputFormatters[i] is NewtonsoftJsonInputFormatter input
                && input is not NewtonsoftJsonPatchInputFormatter)
                options.InputFormatters[i] = new Input(input, options, json.Value, pools, logs);
    }

    private sealed class Input(IInputFormatter original, MvcOptions options,
        MvcNewtonsoftJsonOptions json, ObjectPoolProvider pools, ILoggerFactory logs) : IInputFormatter
    {
        public bool CanRead(InputFormatterContext context) => original.CanRead(context);

        public async Task<InputFormatterResult> ReadAsync(InputFormatterContext context)
        {
            var effective = context.HttpContext.RequestServices.GetRequiredService<EntityRequestContextBuilder>()
                .Build(new QueryOptions(), context.HttpContext.RequestAborted, context.HttpContext);
            var access = await FieldAccess.Prepare(context.ModelType, context.HttpContext.RequestServices,
                effective.User, context.HttpContext.RequestAborted).ConfigureAwait(false);
            if (!access.RequiresGuardedSerialization)
                return await original.ReadAsync(context).ConfigureAwait(false);
            try
            {
                // A typed body is a complete value, including constructor-bound and omitted properties.
                access.DemandReplacement();
            }
            catch (UnauthorizedAccessException ex)
            {
                context.ModelState.TryAddModelError(context.ModelName, ex.Message);
                return await InputFormatterResult.FailureAsync().ConfigureAwait(false);
            }
            var formatter = new NewtonsoftJsonInputFormatter(logs.CreateLogger<NewtonsoftJsonInputFormatter>(),
                access.CreateSerializerSettings(json.SerializerSettings), ArrayPool<char>.Shared, pools, options, json);
            return await formatter.ReadAsync(context).ConfigureAwait(false);
        }
    }
}
