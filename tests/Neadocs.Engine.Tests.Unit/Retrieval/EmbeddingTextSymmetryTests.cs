namespace Neadocs.Engine.Tests.Unit.Retrieval;

using FluentAssertions;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Providers;
using Neadocs.Engine.Infrastructure.Storage;
using Neadocs.Engine.Infrastructure.Text;
using Xunit;

/// <summary>
/// A query and a chunk must be embedded from text in the same form.
/// </summary>
/// <remarks>
/// <para>
/// The query side has always normalised — <c>QueryHash</c> documents it — and the indexing side
/// did not: it passed the chunk's raw text straight to the provider. So the two vectors being
/// compared were computed from text in two different forms, and no test noticed because nothing
/// compared them.
/// </para>
/// <para>
/// Measured with the deterministic provider against a Turkish document, EVERY query scored 0.0000
/// — including a query that was the document's own title. Normalising both sides took that same
/// pair to 0.6708. A real model degrades more quietly, which is worse: nothing fails, recall is
/// simply lower than anyone can see.
/// </para>
/// </remarks>
public sealed class EmbeddingTextSymmetryTests
{
    private const string Document =
        "# Menüyü yayınlama\n\n## Adımlar\n\nMenüyü yayınlamak için düzenle ekranına gidin.";

    private static NormalizerRegistry Normalizers() => new(RuleSetLoader.Load(null));

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
        }

        return dot;
    }

    private static double Similarity(string queryText, string documentText)
    {
        NormalizerRegistry normalizers = Normalizers();

        float[] query = DeterministicEmbeddingProvider.Embed(normalizers.Normalize("tr", queryText), 128);
        float[] document = DeterministicEmbeddingProvider.Embed(normalizers.Normalize("tr", documentText), 128);

        return Cosine(query, document);
    }

    [Fact]
    public void A_query_matching_the_title_is_not_orthogonal_to_the_document()
    {
        // The regression, stated as the absurdity it was: a document could not match its own title.
        double similarity = Similarity("Menüyü yayınlama", Document);

        similarity.Should().BeGreaterThan(0.6,
            "a query that is the document's title must be among the strongest possible matches");
    }

    [Fact]
    public void Diacritics_typed_or_omitted_reach_the_same_vector()
    {
        // What the folding is FOR: people type Turkish without diacritics. Both spellings must
        // produce the same embedding, or half the readers get different results from the other
        // half for the same question.
        NormalizerRegistry normalizers = Normalizers();

        float[] withDiacritics = DeterministicEmbeddingProvider.Embed(normalizers.Normalize("tr", "yayınlama"), 128);
        float[] without = DeterministicEmbeddingProvider.Embed(normalizers.Normalize("tr", "yayinlama"), 128);

        Cosine(withDiacritics, without).Should().BeApproximately(1.0, 1e-6);
    }

    [Fact]
    public void An_unrelated_query_stays_orthogonal()
    {
        // Normalising both sides must not make everything match everything: the zero-result state
        // is the most valuable screen a help centre has, and it has to remain reachable.
        Similarity("şifremi unuttum", Document).Should().BeLessThan(0.1);
    }

    [Fact]
    public void The_cache_key_follows_the_text_that_was_embedded()
    {
        // Keying on raw content is how vectors survived a reindex: a normalisation rule change
        // rewrites the chunk's tsvector but never its content, so every cache lookup hit and the
        // provider was never called again.
        NormalizerRegistry normalizers = Normalizers();

        string folded = normalizers.Normalize("tr", Document);
        string raw = Document;

        EmbeddingCacheKey.Of(folded).Should().NotBe(EmbeddingCacheKey.Of(raw),
            "different text must key differently, or the cache serves a vector it did not compute");
    }

    [Fact]
    public void The_same_text_keys_the_same_way()
    {
        EmbeddingCacheKey.Of("menuyu yayinlama").Should().Be(EmbeddingCacheKey.Of("menuyu yayinlama"));
    }

    [Fact]
    public void A_chunk_key_cannot_collide_with_a_query_key()
    {
        // Both live in one table. An identical string arriving from each side must not share a row,
        // or a chunk's vector answers a query and vice versa.
        EmbeddingCacheKey.Of("ayni metin").Should().NotBe(QueryHash.Of("ayni metin"));
    }

    [Fact]
    public void A_model_carries_its_own_similarity_floor()
    {
        // Cosine similarity is comparable across queries only for a fixed model. The global
        // default was measured against one model; applying it to another is not an approximation.
        EmbeddingModelOptions options = new()
        {
            Provider = "deterministic",
            Model = "probe-model",
            Dimensions = 128,
            MinSimilarity = 0.2,
        };

        EmbeddingModelDescriptor descriptor = EmbeddingModelDescriptor.From(options);

        descriptor.MinSimilarity.Should().Be(0.2);
    }

    [Fact]
    public void A_model_without_a_floor_defers_to_the_global_default()
    {
        EmbeddingModelDescriptor descriptor = EmbeddingModelDescriptor.From(new EmbeddingModelOptions
        {
            Provider = "deterministic",
            Model = "probe-model",
            Dimensions = 128,
        });

        descriptor.MinSimilarity.Should().BeNull("null is what makes the engine fall back rather than to zero");
    }
}
