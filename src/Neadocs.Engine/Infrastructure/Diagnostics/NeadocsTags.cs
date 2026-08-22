namespace Neadocs.Engine.Infrastructure.Diagnostics;

public static class NeadocsTags
{
    public const string Collection = "neadocs.collection";
    public const string Locale = "neadocs.locale";
    public const string Mode = "neadocs.mode";
    public const string Provider = "neadocs.provider";
    public const string Model = "neadocs.model";
    public const string ChunkCount = "neadocs.chunk_count";
    public const string CacheHit = "neadocs.cache_hit";
    public const string Degraded = "neadocs.degraded";
    public const string Changed = "neadocs.changed";
    public const string Reason = "neadocs.reason";
    public const string Tenant = "neadocs.tenant";
    public const string CorrelationId = "neadocs.correlation_id";
    public const string DocumentCount = "neadocs.document_count";
    public const string HitCount = "neadocs.hit_count";
    public const string QueryLength = "neadocs.query_length";
    public const string Schema = "neadocs.schema";

    public static readonly string[] All =
    [
        Collection,
        Locale,
        Mode,
        Provider,
        Model,
        ChunkCount,
        CacheHit,
        Degraded,
        Changed,
        Reason,
        Tenant,
        CorrelationId,
        DocumentCount,
        HitCount,
        QueryLength,
        Schema,
    ];
}
