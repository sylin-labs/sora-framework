using System.Buffers;
using Koan.Web.Authorization;
using Koan.Web.Endpoints;
using Koan.Web.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Koan.Web.Serialization;

/// <summary>Bind both typed MVC result paths to one operation's field decisions and native Newtonsoft buffering.</summary>
internal sealed class FieldAccessResultFilter(IOptions<MvcNewtonsoftJsonOptions> json,
    IOptions<MvcOptions> mvc) : IAsyncResultFilter, IOrderedFilter
{
    public int Order => int.MaxValue;

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var effective = context.HttpContext.RequestServices.GetRequiredService<EntityRequestContextBuilder>()
            .Build(new QueryOptions(), context.HttpContext.RequestAborted, context.HttpContext);
        if (context.Result is ObjectResult { Value: not null and not string and not JToken } objectResult)
        {
            var access = await FieldAccess.Prepare(objectResult.Value.GetType(), context.HttpContext.RequestServices,
                effective.User, context.HttpContext.RequestAborted).ConfigureAwait(false);
            if (!access.RequiresGuardedSerialization)
            {
                await next().ConfigureAwait(false);
                return;
            }
            if (objectResult.Formatters.Count != 0)
                throw new NotSupportedException("Conditional field access cannot use per-result custom output formatters. Return a typed ObjectResult with the standard JSON formatter.");
            if (mvc.Value.SuppressOutputFormatterBuffering)
                throw new NotSupportedException("Typed field access serialization requires MVC output buffering.");
            objectResult.Formatters.Add(new NewtonsoftJsonOutputFormatter(
                access.CreateSerializerSettings(json.Value.SerializerSettings), ArrayPool<char>.Shared, mvc.Value, json.Value));
        }
        if (context.Result is JsonResult { Value: not null and not string and not JToken } result)
        {
            var access = await FieldAccess.Prepare(result.Value.GetType(), context.HttpContext.RequestServices,
                effective.User, context.HttpContext.RequestAborted).ConfigureAwait(false);
            if (!access.RequiresGuardedSerialization)
            {
                await next().ConfigureAwait(false);
                return;
            }
            if (mvc.Value.SuppressOutputFormatterBuffering)
                throw new NotSupportedException("Conditional field access requires MVC output buffering.");
            if (result.SerializerSettings is not null and not JsonSerializerSettings)
                throw new NotSupportedException("Conditional field access requires Newtonsoft settings for typed JsonResult responses.");
            else
            {
                result.SerializerSettings = access.CreateSerializerSettings(
                    (JsonSerializerSettings?)result.SerializerSettings ?? json.Value.SerializerSettings);
            }
        }
        await next().ConfigureAwait(false);
    }
}
