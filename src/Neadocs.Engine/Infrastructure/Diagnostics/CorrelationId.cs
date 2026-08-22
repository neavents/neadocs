namespace Neadocs.Engine.Infrastructure.Diagnostics;

using System;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;

public static class CorrelationId
{
    public const string HeaderName = "X-Correlation-Id";

    public const string LogPropertyName = "CorrelationId";

    public const string ItemKey = "neadocs.correlation_id";

    public const int MaxLength = 128;

    public static string Of(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out object? value) && value is string id
            ? id
            : string.Empty;

    public static bool IsWellFormed(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > MaxLength)
        {
            return false;
        }

        foreach (char c in candidate)
        {
            bool allowed =
                c is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                || c is '-' or '_' or '.' or ':';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    public static string Generate()
    {
        Activity? current = Activity.Current;

        if (current is not null && current.IdFormat == ActivityIdFormat.W3C)
        {
            string traceId = current.TraceId.ToHexString();

            if (traceId.Length > 0 && traceId != "00000000000000000000000000000000")
            {
                return traceId;
            }
        }

        return Guid.NewGuid().ToString("N");
    }
}
