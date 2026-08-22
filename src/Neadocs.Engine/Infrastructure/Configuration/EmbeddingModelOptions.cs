namespace Neadocs.Engine.Infrastructure.Configuration;

public sealed class EmbeddingModelOptions
{
    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int Dimensions { get; set; }

    public bool Retired { get; set; }

    /// <summary>
    /// Cosine similarity a neighbour must reach under THIS model, overriding
    /// <c>DocumentEngine:VectorMinSimilarity</c>. Null uses the global default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The threshold is a property of the model, not of the engine. Cosine similarity is
    /// comparable across queries for a fixed model — which is what makes a fixed floor meaningful
    /// — but the absolute scale belongs to whatever produced the vectors, and the global default
    /// was measured against one specific model on one specific corpus.
    /// </para>
    /// <para>
    /// Applying that number to a different model is not a small inaccuracy. Measured against the
    /// deterministic provider, a query matching a document's title word scores 0.30 and an
    /// ASCII-folded variant of the same word scores 0.00, where the global floor is 0.60. Every
    /// neighbour is discarded, the search reports <c>degraded: false</c> because nothing failed,
    /// and the caller is shown the no-results screen — which this engine deliberately makes
    /// reachable, and which is therefore indistinguishable from a genuine absence of an answer.
    /// </para>
    /// <para>
    /// Set per model so a model change forces the decision rather than inheriting a number that no
    /// longer means anything.
    /// </para>
    /// </remarks>
    public double? MinSimilarity { get; set; }
}
