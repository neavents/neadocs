namespace Neadocs.Engine.Infrastructure.Http;

using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Neadocs.Engine.Infrastructure.Serialization;

public static class Problem
{
    public const string ContentType = "application/problem+json";

    public static Task WriteAsync(
        HttpContext context,
        int status,
        string title,
        string? detail = null,
        string type = "about:blank")
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;

        ProblemResponse payload = new()
        {
            Type = type,
            Title = title,
            Status = status,
            Detail = detail,
            CorrelationId = NullIfEmpty(CorrelationId.Of(context)),
        };

        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            payload,
            NeadocsJsonContext.Default.ProblemResponse,
            context.RequestAborted);
    }

    public static IResult Result(
        HttpContext context,
        int status,
        string title,
        string? detail = null,
        string type = "about:blank")
    {
        ProblemResponse payload = new()
        {
            Type = type,
            Title = title,
            Status = status,
            Detail = detail,
            CorrelationId = NullIfEmpty(CorrelationId.Of(context)),
        };

        return Results.Json(
            payload,
            NeadocsJsonContext.Default.ProblemResponse,
            ContentType,
            status);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
