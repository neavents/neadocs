namespace Neadocs.Engine.Features;

using System;
using System.Collections.Generic;
using System.Text.Json;

public sealed class UpsertCollectionRequest
{
    public string Name { get; set; } = string.Empty;

    public JsonElement? Config { get; set; }
}

public sealed class CollectionResponse
{
    public Guid Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int DocumentCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CollectionListResponse
{
    public List<CollectionResponse> Items { get; set; } = [];
}

public sealed class UpsertDocumentRequest
{
    public string Locale { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string? SourceUri { get; set; }

    public JsonElement? Metadata { get; set; }

    public string? SourceLocale { get; set; }

    public string? SourceContentHash { get; set; }
}

public sealed class BulkUpsertRequest
{
    public List<BulkUpsertItem> Documents { get; set; } = [];
}

public sealed class BulkUpsertItem
{
    public string ExternalKey { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public string? SourceUri { get; set; }

    public JsonElement? Metadata { get; set; }

    public string? SourceLocale { get; set; }

    public string? SourceContentHash { get; set; }
}

public sealed class ChunkCounts
{
    public int Total { get; set; }

    public int Created { get; set; }

    public int Reused { get; set; }

    public int Deleted { get; set; }
}

public sealed class UpsertDocumentResponse
{
    public Guid DocumentId { get; set; }

    public string ExternalKey { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public int Revision { get; set; }

    public bool Changed { get; set; }

    public ChunkCounts Chunks { get; set; } = new();
}

public sealed class BulkUpsertResponse
{
    public int Total { get; set; }

    public int Changed { get; set; }

    public List<BulkUpsertResult> Results { get; set; } = [];
}

public sealed class BulkUpsertResult
{
    public string ExternalKey { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public int Status { get; set; }

    public bool Changed { get; set; }

    public int Revision { get; set; }

    public string? Error { get; set; }
}

public sealed class DocumentResponse
{
    public Guid Id { get; set; }

    public string ExternalKey { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? SourceUri { get; set; }

    public int Revision { get; set; }

    public string ContentHash { get; set; } = string.Empty;

    public string? SourceLocale { get; set; }

    public string? SourceContentHash { get; set; }

    public bool Stale { get; set; }

    public JsonElement Metadata { get; set; }

    public string? Content { get; set; }

    public int ChunkCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class DocumentListResponse
{
    public List<DocumentResponse> Items { get; set; } = [];

    public string? NextCursor { get; set; }
}

public sealed class RevisionResponse
{
    public int Revision { get; set; }

    public string Title { get; set; } = string.Empty;

    public string ContentHash { get; set; } = string.Empty;

    public int Length { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RevisionListResponse
{
    public List<RevisionResponse> Items { get; set; } = [];
}

public sealed class SearchRequest
{
    public string Query { get; set; } = string.Empty;

    public string? Locale { get; set; }

    public string? Mode { get; set; }

    public int Limit { get; set; } = 10;

    public double MinScore { get; set; }

    public JsonElement? Filter { get; set; }

    public bool Explain { get; set; }
}

public sealed class SearchResponse
{
    public string Mode { get; set; } = string.Empty;

    public bool Degraded { get; set; }

    public long TookMs { get; set; }

    public List<SearchHit> Hits { get; set; } = [];
}

public sealed class SearchHit
{
    public Guid ChunkId { get; set; }

    public Guid DocumentId { get; set; }

    public string ExternalKey { get; set; } = string.Empty;

    public string Locale { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public List<string> HeadingPath { get; set; } = [];

    public double Score { get; set; }

    public int Ordinal { get; set; }

    public string Snippet { get; set; } = string.Empty;

    public List<Highlight> Highlights { get; set; } = [];

    public JsonElement Metadata { get; set; }

    public SearchExplain? Explain { get; set; }
}

public sealed class Highlight
{
    public int Start { get; set; }

    public int Length { get; set; }
}

public sealed class SearchExplain
{
    public int? LexicalRank { get; set; }

    public int? VectorRank { get; set; }

    public double? LexicalScore { get; set; }

    public double? VectorScore { get; set; }
}

public sealed class StatsResponse
{
    public string Schema { get; set; } = string.Empty;

    public int CollectionCount { get; set; }

    public int DocumentCount { get; set; }

    public int ChunkCount { get; set; }

    public int BacklogDepth { get; set; }

    public List<LocaleStats> Locales { get; set; } = [];

    public List<CollectionStats> Collections { get; set; } = [];
}

public sealed class LocaleStats
{
    public string Locale { get; set; } = string.Empty;

    public int DocumentCount { get; set; }

    public int ChunkCount { get; set; }

    public int StaleChunks { get; set; }
}

public sealed class CollectionStats
{
    public string Key { get; set; } = string.Empty;

    public int DocumentCount { get; set; }

    public int ChunkCount { get; set; }
}

/// <summary>
/// One locale's compiled normalisation rules, as the engine is actually running them.
/// </summary>
/// <remarks>
/// <b>Exposed because <c>staleChunks</c> is unreadable without it.</b> The stats endpoint reports
/// that a locale's chunks were built under rules that have since changed, and until now there was
/// no way to ask what the current rules are — leaving an operator told to reindex, with no way to
/// see what would change or whether the number is expected to be non-zero.
/// <para>
/// <c>Hash</c> is the value compared against each chunk's <c>normalizer_hash</c>, so the two panels
/// line up field for field.
/// </para>
/// </remarks>
public sealed class NormalizerResponse
{
    /// <summary>The locale this rule set claims, or <c>*</c> for the fallback.</summary>
    public string Tag { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Identity of everything that determines a chunk's search vector.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>The Postgres text search configuration; <c>simple</c> applies no stemming at all.</summary>
    public string SearchConfig { get; set; } = string.Empty;

    /// <summary>Length of the low-weight truncated copies, or 0 when the locale emits none.</summary>
    public int StemPrefixLength { get; set; }

    /// <summary>Ordered names of the pipeline operations, so the folding order is visible.</summary>
    public List<string> Operations { get; set; } = [];

    /// <summary>How many tokens the <c>dropTokens</c> step removes, across all such steps.</summary>
    public int DroppedTokenCount { get; set; }

    public int SelfTestCount { get; set; }

    /// <summary>True when a file on disk overrode the rule set compiled into the engine.</summary>
    public bool FromFile { get; set; }

    public string Origin { get; set; } = string.Empty;
}

public sealed class NormalizerListResponse
{
    public List<NormalizerResponse> Items { get; set; } = [];
}

public sealed class ProviderHealthResponse
{
    public bool Configured { get; set; }

    public List<ProviderHealthItem> Providers { get; set; } = [];
}

public sealed class ProviderHealthItem
{
    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public int Dimensions { get; set; }

    public bool Retired { get; set; }

    public bool Healthy { get; set; }

    public string? LastError { get; set; }

    public long BacklogDepth { get; set; }
}

public sealed class JobResponse
{
    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public int Processed { get; set; }

    public int Total { get; set; }

    public List<string> Errors { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class JobAcceptedResponse
{
    public Guid JobId { get; set; }

    public string State { get; set; } = string.Empty;
}
