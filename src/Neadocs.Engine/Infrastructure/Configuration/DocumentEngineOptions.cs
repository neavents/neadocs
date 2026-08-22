namespace Neadocs.Engine.Infrastructure.Configuration;

using System.Collections.Generic;

public sealed class DocumentEngineOptions
{
    public List<EmbeddingModelOptions> EmbeddingModels { get; set; } = [];

    public string PostgresConnectionString { get; set; } =
        "Host=127.0.0.1;Port=5432;Database=neadocs;Username=neadocs;Password=";

    public string Schema { get; set; } = "neadocs";

    public string DefaultSearchMode { get; set; } = "hybrid";

    public int RrfK { get; set; } = 60;

    public int CandidateMultiplier { get; set; } = 5;

    public int MinCandidates { get; set; } = 50;

    public int HnswEfSearch { get; set; } = 100;

    /// <summary>
    /// Cosine similarity a vector neighbour must reach to be treated as a candidate at all.
    /// </summary>
    /// <remarks>
    /// <b>Without a floor, a vector search can never return nothing.</b> It returns the nearest
    /// neighbours whatever the distance, so enabling embeddings quietly makes the zero-result state
    /// unreachable — and that state is the most valuable screen a help centre has: the one that
    /// admits there is no answer, offers a contact route, and logs the question for whoever writes
    /// the documentation. Ten irrelevant results look like an answer and produce no such record.
    /// <para>
    /// Unlike an RRF score, cosine similarity is comparable across queries, so a fixed threshold is
    /// meaningful. Measured against this corpus, keyboard mash and off-topic questions land at
    /// 0.53–0.54 while genuine questions reach 0.66–0.78; 0.6 sits in the gap. It is worth
    /// re-measuring after an embedding model change, since the absolute scale is the model's.
    /// </para>
    /// </remarks>
    public double VectorMinSimilarity { get; set; } = 0.6;

    public ChunkingOptions Chunking { get; set; } = new();

    public TextOptions Text { get; set; } = new();

    public Dictionary<string, ProviderOptions> Providers { get; set; } = [];

    public ResilienceOptions Resilience { get; set; } = new();

    public BacklogWorkerOptions BacklogWorker { get; set; } = new();

    public double MinRecallAt3 { get; set; } = 0.90;

    public string JwtSymmetricKey { get; set; } = string.Empty;

    public int JwtClockSkewSeconds { get; set; } = 30;

    public string AllowedProjectKeys { get; set; } = string.Empty;

    public string CorsAllowedOrigins { get; set; } = string.Empty;

    public int MaxRequestBodyBytes { get; set; } = 4_194_304;

    public int MaxQueryLength { get; set; } = 512;

    public int MaxSearchLimit { get; set; } = 100;

    public int MaxBulkDocuments { get; set; } = 500;

    public int RateLimitPermitCount { get; set; } = 600;

    public int RateLimitWindowSeconds { get; set; } = 10;

    public int RateLimitQueueSize { get; set; } = 200;

    public bool EnablePrometheusScrape { get; set; } = true;

    public int DatabaseCommandTimeoutSeconds { get; set; } = 30;
}
