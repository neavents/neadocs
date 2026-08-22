namespace Neadocs.Engine.Infrastructure.Storage;

using System;
using Neadocs.Engine.Infrastructure.Configuration;

public sealed class SchemaTables
{
    public const string EmbeddingTablePrefix = "chunk_embeddings__";

    public SchemaTables(DocumentEngineOptions options)
        : this(options.Schema)
    {
    }

    public SchemaTables(string schema)
    {
        if (!SqlIdentifier.IsValid(schema))
        {
            throw new InvalidOperationException(
                $"DocumentEngine:Schema must be a bare lowercase SQL identifier matching "
                + $"[a-z_][a-z0-9_]{{0,{SqlIdentifier.MaxLength - 1}}}; got '{schema}'. "
                + "It is interpolated into DDL, so it is validated rather than escaped.");
        }

        Name = schema;
        Collections = $"{schema}.collections";
        Documents = $"{schema}.documents";
        DocumentRevisions = $"{schema}.document_revisions";
        Chunks = $"{schema}.chunks";
        EmbeddingCache = $"{schema}.embedding_cache";
        EmbeddingBacklog = $"{schema}.embedding_backlog";
        Jobs = $"{schema}.jobs";
    }

    public string Name { get; }

    public string Collections { get; }

    public string Documents { get; }

    public string DocumentRevisions { get; }

    public string Chunks { get; }

    public string EmbeddingCache { get; }

    public string EmbeddingBacklog { get; }

    public string Jobs { get; }

    public string ChunkEmbeddings(string modelSlug)
    {
        string table = EmbeddingTableName(modelSlug);

        return $"{Name}.{table}";
    }

    public static string EmbeddingTableName(string modelSlug)
    {
        if (!ModelSlug.IsValid(modelSlug) || !SqlIdentifier.IsValid(modelSlug))
        {
            throw new InvalidOperationException(
                $"'{modelSlug}' is not a usable embedding table slug. Slugs are produced by "
                + $"{nameof(ModelSlug)}.{nameof(ModelSlug.From)} and are always safe; a value "
                + "reaching here that is not means it bypassed that path.");
        }

        return EmbeddingTablePrefix + modelSlug;
    }

    public static bool IsEmbeddingTableName(string tableName) =>
        tableName.StartsWith(EmbeddingTablePrefix, StringComparison.Ordinal)
        && tableName.Length > EmbeddingTablePrefix.Length;

    public static string SlugFromEmbeddingTableName(string tableName) =>
        IsEmbeddingTableName(tableName)
            ? tableName[EmbeddingTablePrefix.Length..]
            : throw new InvalidOperationException($"'{tableName}' is not an embedding table name.");
}
