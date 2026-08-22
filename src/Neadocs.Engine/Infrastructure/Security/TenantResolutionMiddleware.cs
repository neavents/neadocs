namespace Neadocs.Engine.Infrastructure.Security;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Neadocs.Engine.Infrastructure.Http;
using Serilog.Context;

public static class TenantResolutionMiddleware
{
    public const string TenantClaim = "tenant";

    public const string ScopeClaim = "scope";

    /// <summary>
    /// Paths served without a tenant or a key, because the caller cannot present one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The kubelet carries no API key. A probe path that answers 401 fails the probe, and a failing
    /// startup probe kills the pod — so an omission here is not a security decision, it is a
    /// service that cannot be deployed.
    /// </para>
    /// <para>
    /// <c>/health/live</c> and <c>/health/ready</c> are the names the generated Kubernetes manifests
    /// probe, matching the rest of the estate; <c>/health</c> and <c>/ready</c> are the older names
    /// this service has always served and are kept for whatever already points at them. All four
    /// were needed: adding the endpoints alone left them answering 401 here, which the deployment
    /// would have experienced as exactly the same 30 failed probes as a 404.
    /// </para>
    /// <para>
    /// Matched exactly, never by prefix. A prefix match on "/health" would silently make anything
    /// added under that path anonymous later.
    /// </para>
    /// </remarks>
    public static readonly string[] AnonymousPaths =
        ["/health", "/health/live", "/ready", "/health/ready", "/metrics"];

    public static IApplicationBuilder UseNeadocsTenantResolution(this IApplicationBuilder app)
    {
        ApiKeyValidator validator = app.ApplicationServices.GetRequiredService<ApiKeyValidator>();

        return app.Use(async (HttpContext context, RequestDelegate next) =>
        {
            if (IsAnonymous(context.Request.Path))
            {
                await next(context);
                return;
            }

            RequestPrincipal? principal = ResolveFromProjectKey(context, validator)
                ?? ResolveFromJwt(context);

            if (principal is null)
            {
                await Problem.WriteAsync(
                    context,
                    StatusCodes.Status401Unauthorized,
                    "Unauthorized",
                    $"Present a valid {ApiKeyValidator.HeaderName} header or a bearer token "
                    + $"carrying a '{TenantClaim}' claim.");
                return;
            }

            context.Items[RequestPrincipal.ItemKey] = principal;

            Activity.Current?.SetTag(NeadocsTags.Tenant, principal.Tenant);

            using (LogContext.PushProperty("Tenant", principal.Tenant))
            using (LogContext.PushProperty("AuthMechanism", principal.Mechanism))
            {
                await next(context);
            }
        });
    }

    public static bool IsAnonymous(PathString path)
    {
        foreach (string anonymous in AnonymousPaths)
        {
            if (path.Equals(anonymous, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static RequestPrincipal? ResolveFromProjectKey(
        HttpContext context,
        ApiKeyValidator validator)
    {
        if (!context.Request.Headers.TryGetValue(ApiKeyValidator.HeaderName, out StringValues header))
        {
            return null;
        }

        string? presented = header.Count > 0 ? header[0] : null;

        return validator.TryResolve(presented, out string tenant, out DocumentScope scopes)
            ? new RequestPrincipal(tenant, scopes, RequestPrincipal.ProjectKeyMechanism)
            : null;
    }

    private static RequestPrincipal? ResolveFromJwt(HttpContext context)
    {
        ClaimsPrincipal? user = context.User;

        if (user?.Identity is not { IsAuthenticated: true })
        {
            return null;
        }

        string? tenant = user.FindFirst(TenantClaim)?.Value;

        if (string.IsNullOrWhiteSpace(tenant))
        {
            return null;
        }

        IEnumerable<string> scopeValues = user.FindAll(ScopeClaim).Select(c => c.Value);
        DocumentScope scopes = DocumentScopeNames.ParseMany(scopeValues);

        return new RequestPrincipal(tenant.Trim(), scopes, RequestPrincipal.JwtMechanism);
    }
}
