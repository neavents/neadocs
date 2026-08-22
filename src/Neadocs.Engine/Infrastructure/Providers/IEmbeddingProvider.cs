namespace Neadocs.Engine.Infrastructure.Providers;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Neadocs.Engine.Infrastructure.Configuration;

public interface IEmbeddingProvider
{
    string Name { get; }

    string Model { get; }

    int Dimensions { get; }

    int MaxBatch { get; }

    int MaxConcurrentRequests { get; }

    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);

    Task<bool> IsHealthyAsync(CancellationToken ct);
}

public sealed record EmbeddingModelDescriptor(
    string Provider,
    string Model,
    string Slug,
    int Dimensions,
    bool Retired,
    double? MinSimilarity = null)
{
    public static EmbeddingModelDescriptor From(EmbeddingModelOptions options) =>
        new(options.Provider, options.Model, ModelSlug.From(options.Model), options.Dimensions,
            options.Retired, options.MinSimilarity);
}

public sealed class EmbeddingModelRegistry
{
    public EmbeddingModelRegistry(DocumentEngineOptions options)
    {
        List<EmbeddingModelDescriptor> all = [];

        foreach (EmbeddingModelOptions model in options.EmbeddingModels)
        {
            all.Add(EmbeddingModelDescriptor.From(model));
        }

        All = all;
        Active = all.FindAll(m => !m.Retired);
    }

    public List<EmbeddingModelDescriptor> All { get; }

    public List<EmbeddingModelDescriptor> Active { get; }

    public bool HasActiveModel => Active.Count > 0;

    public EmbeddingModelDescriptor? Primary => Active.Count > 0 ? Active[0] : null;

    public EmbeddingModelDescriptor? BySlug(string slug) =>
        All.Count == 0 ? null : All.Find(m => m.Slug == slug);
}
