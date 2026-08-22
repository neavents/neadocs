namespace Neadocs.Engine.Infrastructure.Security;

using System;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

public static class RateLimitPartitionKey
{
    public const string AnonymousPartition = "anonymous";

    public static string For(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(ApiKeyValidator.HeaderName, out StringValues key)
            && key.Count > 0
            && !string.IsNullOrEmpty(key[0]))
        {
            return "k:" + Fingerprint(key[0]!);
        }

        if (context.Request.Headers.TryGetValue("Authorization", out StringValues authorization)
            && authorization.Count > 0
            && !string.IsNullOrEmpty(authorization[0]))
        {
            return "b:" + Fingerprint(authorization[0]!);
        }

        string? address = context.Connection.RemoteIpAddress?.ToString();

        return string.IsNullOrEmpty(address) ? AnonymousPartition : "i:" + address;
    }

    public static string Fingerprint(string credential)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(credential), hash);

        return Convert.ToHexStringLower(hash[..8]);
    }
}
