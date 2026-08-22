namespace Neadocs.Engine.Infrastructure.Diagnostics;

using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Serilog.Context;

public static class CorrelationIdMiddleware
{
    public static IApplicationBuilder UseNeadocsCorrelationId(this IApplicationBuilder app) =>
        app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            string id = Resolve(context);

            context.Items[CorrelationId.ItemKey] = id;

            Activity? activity = Activity.Current;

            if (activity is not null)
            {
                activity.SetTag(NeadocsTags.CorrelationId, id);
                activity.SetBaggage(NeadocsTags.CorrelationId, id);
            }

            context.Response.OnStarting(static state =>
            {
                HttpContext ctx = (HttpContext)state;
                ctx.Response.Headers[CorrelationId.HeaderName] = CorrelationId.Of(ctx);
                return Task.CompletedTask;
            }, context);

            using (LogContext.PushProperty(CorrelationId.LogPropertyName, id))
            {
                await next(context);
            }
        });

    private static string Resolve(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(CorrelationId.HeaderName, out StringValues inbound))
        {
            string? candidate = inbound.Count > 0 ? inbound[0] : null;

            if (CorrelationId.IsWellFormed(candidate))
            {
                return candidate!;
            }
        }

        return CorrelationId.Generate();
    }
}
