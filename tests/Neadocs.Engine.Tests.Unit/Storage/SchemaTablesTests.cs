namespace Neadocs.Engine.Tests.Unit.Storage;

using System;
using FluentAssertions;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Storage;

public sealed class SchemaTablesTests
{
    [Fact]
    public void QualifiesEveryFixedTableWithTheConfiguredSchema()
    {
        SchemaTables tables = new("neadocs");

        tables.Name.Should().Be("neadocs");
        tables.Collections.Should().Be("neadocs.collections");
        tables.Documents.Should().Be("neadocs.documents");
        tables.DocumentRevisions.Should().Be("neadocs.document_revisions");
        tables.Chunks.Should().Be("neadocs.chunks");
        tables.EmbeddingCache.Should().Be("neadocs.embedding_cache");
        tables.EmbeddingBacklog.Should().Be("neadocs.embedding_backlog");
        tables.Jobs.Should().Be("neadocs.jobs");
    }

    [Fact]
    public void FollowsTheSchemaWhenItIsNotTheDefault()
    {
        SchemaTables tables = new("neadocs_test_01j9");

        tables.Chunks.Should().Be("neadocs_test_01j9.chunks");
        tables.Documents.Should().Be("neadocs_test_01j9.documents");
    }

    [Fact]
    public void NoTableNameIsHardcodedToTheDefaultSchema()
    {
        SchemaTables tables = new("other");

        string[] names =
        [
            tables.Collections,
            tables.Documents,
            tables.DocumentRevisions,
            tables.Chunks,
            tables.EmbeddingCache,
            tables.EmbeddingBacklog,
            tables.Jobs,
            tables.ChunkEmbeddings("m"),
        ];

        names.Should().OnlyContain(n => n.StartsWith("other."));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Neadocs")]
    [InlineData("nea docs")]
    [InlineData("nea-docs")]
    [InlineData("1neadocs")]
    [InlineData("neadocs;DROP SCHEMA public CASCADE")]
    [InlineData("neadocs\"")]
    [InlineData("neadocs--")]
    public void RefusesASchemaNameThatCouldNotBeSafelyInterpolated(string schema)
    {
        Action act = () => _ = new SchemaTables(schema);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("DocumentEngine:Schema");
    }

    [Fact]
    public void RefusesAnOverlongSchemaName()
    {
        Action act = () => _ = new SchemaTables(new string('a', SqlIdentifier.MaxLength + 1));

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ReadsTheSchemaFromOptions()
    {
        SchemaTables tables = new(new DocumentEngineOptions { Schema = "custom" });

        tables.Name.Should().Be("custom");
    }

    [Fact]
    public void NamesAnEmbeddingTableFromItsSlug()
    {
        SchemaTables tables = new("neadocs");

        tables.ChunkEmbeddings("gemini_embedding_001")
            .Should().Be("neadocs.chunk_embeddings__gemini_embedding_001");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has-dash")]
    [InlineData("UPPER")]
    [InlineData("with space")]
    [InlineData("x;DROP TABLE y")]
    public void RefusesAnEmbeddingSlugThatBypassedTheSlugGenerator(string slug)
    {
        Action act = () => SchemaTables.EmbeddingTableName(slug);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("not a usable embedding table slug");
    }

    [Fact]
    public void EverySlugTheGeneratorProducesIsAcceptedAsATableName()
    {
        string[] models =
        [
            "gemini-embedding-001",
            "text-embedding-3-small",
            "Ünïcödé Mödèl/v2.5 (beta)",
            "MODEL",
        ];

        foreach (string model in models)
        {
            string slug = ModelSlug.From(model);

            Action act = () => SchemaTables.EmbeddingTableName(slug);

            act.Should().NotThrow($"'{model}' yields slug '{slug}'");
        }
    }

    [Theory]
    [InlineData("chunk_embeddings__gemini", true)]
    [InlineData("chunk_embeddings__x", true)]
    [InlineData("chunk_embeddings__", false)]
    [InlineData("chunks", false)]
    [InlineData("documents", false)]
    [InlineData("chunk_embeddings", false)]
    public void RecognisesAnEmbeddingTableName(string tableName, bool expected) =>
        SchemaTables.IsEmbeddingTableName(tableName).Should().Be(expected);

    [Fact]
    public void RecoversTheSlugFromAnEmbeddingTableName() =>
        SchemaTables.SlugFromEmbeddingTableName("chunk_embeddings__gemini_embedding_001")
            .Should().Be("gemini_embedding_001");

    [Fact]
    public void RefusesToRecoverASlugFromANonEmbeddingTable()
    {
        Action act = () => SchemaTables.SlugFromEmbeddingTableName("chunks");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TableNameAndSlugRoundTrip()
    {
        string slug = ModelSlug.From("text-embedding-3-small");
        string tableName = SchemaTables.EmbeddingTableName(slug);

        SchemaTables.SlugFromEmbeddingTableName(tableName).Should().Be(slug);
    }
}
