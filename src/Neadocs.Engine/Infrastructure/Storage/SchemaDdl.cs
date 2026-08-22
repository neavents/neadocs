namespace Neadocs.Engine.Infrastructure.Storage;

using System.Collections.Generic;

public static class SchemaDdl
{
    public static IReadOnlyList<string> FixedTables(SchemaTables t) =>
    [
        $$"""
        CREATE TABLE IF NOT EXISTS {{t.Collections}} (
            id              uuid PRIMARY KEY,
            tenant_id       text        NOT NULL,
            key             text        NOT NULL,
            name            text        NOT NULL,
            config          jsonb       NOT NULL DEFAULT '{}'::jsonb,
            created_at      timestamptz NOT NULL DEFAULT now(),
            updated_at      timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT uq_collections_tenant_key UNIQUE (tenant_id, key)
        )
        """,

        $$"""
        CREATE TABLE IF NOT EXISTS {{t.Documents}} (
            id                  uuid PRIMARY KEY,
            collection_id       uuid        NOT NULL REFERENCES {{t.Collections}}(id) ON DELETE CASCADE,
            external_key        text        NOT NULL,
            locale              text        NOT NULL,
            title               text        NOT NULL,
            source_uri          text        NULL,
            metadata            jsonb       NOT NULL DEFAULT '{}'::jsonb,
            current_revision    integer     NOT NULL DEFAULT 0,
            content_hash        text        NOT NULL,
            source_locale       text        NULL,
            source_content_hash text        NULL,
            created_at          timestamptz NOT NULL DEFAULT now(),
            updated_at          timestamptz NOT NULL DEFAULT now(),
            deleted_at          timestamptz NULL,
            CONSTRAINT uq_documents_natural_key UNIQUE (collection_id, external_key, locale)
        )
        """,

        $"""
        CREATE TABLE IF NOT EXISTS {t.DocumentRevisions} (
            id            uuid PRIMARY KEY,
            document_id   uuid        NOT NULL REFERENCES {t.Documents}(id) ON DELETE CASCADE,
            revision      integer     NOT NULL,
            title         text        NOT NULL,
            content       text        NOT NULL,
            content_hash  text        NOT NULL,
            created_at    timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT uq_revisions UNIQUE (document_id, revision)
        )
        """,

        $"""
        CREATE TABLE IF NOT EXISTS {t.Chunks} (
            id              uuid PRIMARY KEY,
            document_id     uuid        NOT NULL REFERENCES {t.Documents}(id) ON DELETE CASCADE,
            revision        integer     NOT NULL,
            ordinal         integer     NOT NULL,
            heading_path    jsonb       NOT NULL DEFAULT '[]'::jsonb,
            content         text        NOT NULL,
            content_hash    text        NOT NULL,
            token_count     integer     NOT NULL,
            tsv_folded      tsvector    NOT NULL,
            normalizer_tag  text        NOT NULL,
            normalizer_hash text        NOT NULL,
            created_at      timestamptz NOT NULL DEFAULT now(),
            CONSTRAINT uq_chunks UNIQUE (document_id, revision, ordinal)
        )
        """,

        $"""
        CREATE TABLE IF NOT EXISTS {t.EmbeddingCache} (
            content_hash  text        NOT NULL,
            model_slug    text        NOT NULL,
            embedding     bytea       NOT NULL,
            created_at    timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (content_hash, model_slug)
        )
        """,

        $"""
        CREATE TABLE IF NOT EXISTS {t.EmbeddingBacklog} (
            chunk_id        uuid        NOT NULL,
            model_slug      text        NOT NULL,
            attempts        integer     NOT NULL DEFAULT 0,
            last_error      text        NULL,
            next_attempt_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (chunk_id, model_slug)
        )
        """,

        $"""
        CREATE TABLE IF NOT EXISTS {t.Jobs} (
            id          uuid PRIMARY KEY,
            tenant_id   text        NOT NULL,
            kind        text        NOT NULL,
            state       text        NOT NULL,
            processed   integer     NOT NULL DEFAULT 0,
            total       integer     NOT NULL DEFAULT 0,
            errors      jsonb       NOT NULL DEFAULT '[]'::jsonb,
            created_at  timestamptz NOT NULL DEFAULT now(),
            updated_at  timestamptz NOT NULL DEFAULT now()
        )
        """,
    ];

    public static IReadOnlyList<string> Indexes(SchemaTables t, string trigramSchema) =>
    [
        $"CREATE INDEX IF NOT EXISTS ix_documents_collection_live ON {t.Documents} (collection_id) WHERE deleted_at IS NULL",
        $"CREATE INDEX IF NOT EXISTS ix_documents_metadata ON {t.Documents} USING gin (metadata jsonb_path_ops)",
        $"CREATE INDEX IF NOT EXISTS ix_documents_external_key ON {t.Documents} (external_key, locale)",
        $"CREATE INDEX IF NOT EXISTS ix_chunks_tsv ON {t.Chunks} USING gin (tsv_folded)",
        $"CREATE INDEX IF NOT EXISTS ix_chunks_doc ON {t.Chunks} (document_id, revision)",
        $"CREATE INDEX IF NOT EXISTS ix_chunks_hash ON {t.Chunks} (content_hash)",
        $"CREATE INDEX IF NOT EXISTS ix_chunks_trgm ON {t.Chunks} USING gin (content {trigramSchema}.gin_trgm_ops)",
        $"CREATE INDEX IF NOT EXISTS ix_chunks_norm ON {t.Chunks} (normalizer_tag, normalizer_hash)",
        $"CREATE INDEX IF NOT EXISTS ix_backlog_due ON {t.EmbeddingBacklog} (next_attempt_at)",
        $"CREATE INDEX IF NOT EXISTS ix_jobs_tenant ON {t.Jobs} (tenant_id, created_at DESC)",
    ];

    public static long AdvisoryLockKey(string schema)
    {
        unchecked
        {
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            ulong hash = offsetBasis;

            foreach (char c in "neadocs.migrator:" + schema)
            {
                hash ^= c;
                hash *= prime;
            }

            return (long)hash;
        }
    }
}
