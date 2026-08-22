namespace Neadocs.Engine.Infrastructure.Security;

using Microsoft.AspNetCore.Http;

public sealed class RequestPrincipal
{
    public const string ItemKey = "neadocs.principal";

    public const string ProjectKeyMechanism = "project-key";

    public const string JwtMechanism = "jwt";

    public RequestPrincipal(string tenant, DocumentScope scopes, string mechanism)
    {
        Tenant = tenant;
        Scopes = scopes.Expand();
        Mechanism = mechanism;
    }

    public string Tenant { get; }

    public DocumentScope Scopes { get; }

    public string Mechanism { get; }

    public bool Grants(DocumentScope required) => Scopes.Grants(required);

    public static RequestPrincipal? Of(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out object? value) ? value as RequestPrincipal : null;

    public static RequestPrincipal Require(HttpContext context) =>
        Of(context) ?? throw new System.InvalidOperationException(
            "No principal is attached to this request. The tenant resolution middleware must run "
            + "before any handler that reads tenant-scoped data.");
}
