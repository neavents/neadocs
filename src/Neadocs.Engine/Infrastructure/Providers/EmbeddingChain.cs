namespace Neadocs.Engine.Infrastructure.Providers;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Diagnostics;
using Polly;
using Polly.CircuitBreaker;

public sealed record ProviderHealth(string Provider, string Model, bool Healthy, string? LastError, bool CircuitOpen);

public sealed class EmbeddingChain : IAsyncDisposable
{
    private readonly List<Link> _links = [];
    private readonly ILogger<EmbeddingChain> _logger;

    public EmbeddingChain(
        IOptions<DocumentEngineOptions> options,
        EmbeddingModelRegistry registry,
        ILogger<EmbeddingChain> logger)
        : this(options.Value, registry, logger, provider: null)
    {
    }

    public EmbeddingChain(
        DocumentEngineOptions options,
        EmbeddingModelRegistry registry,
        ILogger<EmbeddingChain> logger,
        Func<EmbeddingModelDescriptor, IEmbeddingProvider>? provider)
    {
        _logger = logger;
        ResilienceOptions resilience = options.Resilience;

        foreach (EmbeddingModelDescriptor model in registry.Active)
        {
            IEmbeddingProvider instance = provider is not null
                ? provider(model)
                : Build(model, options);

            ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
                .AddRetry(new Polly.Retry.RetryStrategyOptions
                {
                    MaxRetryAttempts = resilience.MaxRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromMilliseconds(200),
                    MaxDelay = TimeSpan.FromMilliseconds(resilience.RetryBackoffCeilingMs),
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(e => e is not OperationCanceledException),
                })
                .AddCircuitBreaker(new CircuitBreakerStrategyOptions
                {
                    FailureRatio = resilience.CircuitBreakerFailureRatio,
                    SamplingDuration = TimeSpan.FromSeconds(resilience.CircuitBreakerSamplingSeconds),
                    MinimumThroughput = resilience.CircuitBreakerMinimumThroughput,
                    BreakDuration = TimeSpan.FromSeconds(resilience.CircuitBreakerDurationSeconds),
                    ShouldHandle = new PredicateBuilder().Handle<Exception>(e => e is not OperationCanceledException),
                    OnOpened = args =>
                    {
                        NeadocsMeters.SetCircuitOpen(instance.Name, open: true);
                        return default;
                    },
                    OnClosed = args =>
                    {
                        NeadocsMeters.SetCircuitOpen(instance.Name, open: false);
                        return default;
                    },
                })
                .Build();

            _links.Add(new Link(model, instance, pipeline));
            NeadocsMeters.SetCircuitOpen(instance.Name, open: false);
        }
    }

    public bool HasProvider => _links.Count > 0;

    public IReadOnlyList<EmbeddingModelDescriptor> Models =>
        [.. _links.ConvertAll(l => l.Model)];

    public int DimensionsOf(string slug)
    {
        Link? link = _links.Find(l => l.Model.Slug == slug);

        return link?.Model.Dimensions ?? 0;
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        string slug, IReadOnlyList<string> texts, CancellationToken ct)
    {
        Link link = _links.Find(l => l.Model.Slug == slug)
            ?? throw new InvalidOperationException($"No configured embedding model has slug '{slug}'.");

        using Activity? activity = NeadocsActivitySources.Provider.StartActivity("embed");
        activity?.SetTag(NeadocsTags.Provider, link.Provider.Name);
        activity?.SetTag(NeadocsTags.Model, link.Model.Model);

        List<float[]> all = [];

        for (int offset = 0; offset < texts.Count; offset += link.Provider.MaxBatch)
        {
            int size = Math.Min(link.Provider.MaxBatch, texts.Count - offset);
            List<string> batch = new(size);

            for (int i = offset; i < offset + size; i++)
            {
                batch.Add(texts[i]);
            }

            IReadOnlyList<float[]> vectors = await link.Pipeline.ExecuteAsync(
                async token => await link.Provider.EmbedAsync(batch, token), ct);

            if (vectors.Count != batch.Count)
            {
                throw new EmbeddingRequestFailedException(
                    $"{link.Provider.Name} returned {vectors.Count} vectors for {batch.Count} inputs.");
            }

            all.AddRange(vectors);
        }

        NeadocsMeters.EmbeddingsComputed.Add(all.Count,
            new KeyValuePair<string, object?>(NeadocsTags.Model, link.Model.Slug),
            new KeyValuePair<string, object?>(NeadocsTags.Provider, link.Provider.Name));

        return all;
    }

    public async Task ProbeDimensionsAsync(CancellationToken ct)
    {
        foreach (Link link in _links)
        {
            IReadOnlyList<float[]> probe;

            try
            {
                probe = await link.Provider.EmbedAsync(["neadocs"], ct);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"The boot-time dimension probe for model '{link.Model.Model}' "
                    + $"({link.Provider.Name}) failed: {ex.Message}. Refusing to start rather than "
                    + "discover this one request at a time.", ex);
            }

            if (probe.Count != 1)
            {
                throw new InvalidOperationException(
                    $"The dimension probe for '{link.Model.Model}' returned {probe.Count} vectors "
                    + "for one input.");
            }

            if (probe[0].Length != link.Model.Dimensions)
            {
                throw new InvalidOperationException(
                    $"Model '{link.Model.Model}' returned {probe[0].Length} dimensions but "
                    + $"DocumentEngine:EmbeddingModels declares {link.Model.Dimensions}. Refusing "
                    + "to start: writing the wrong width corrupts the index silently and surfaces "
                    + "weeks later as 'search got worse'. Set Dimensions to "
                    + $"{probe[0].Length}, or configure a different model.");
            }

            _logger.LogInformation(
                "Embedding model {Model} ({Provider}) verified at {Dimensions} dimensions.",
                link.Model.Model, link.Provider.Name, probe[0].Length);
        }
    }

    public async Task<IReadOnlyList<ProviderHealth>> HealthAsync(CancellationToken ct)
    {
        List<ProviderHealth> health = [];

        foreach (Link link in _links)
        {
            string? error = null;
            bool healthy;

            try
            {
                healthy = await link.Provider.IsHealthyAsync(ct);
            }
            catch (Exception ex)
            {
                healthy = false;
                error = ex.Message;
            }

            health.Add(new ProviderHealth(link.Provider.Name, link.Model.Model, healthy, error, false));
        }

        return health;
    }

    private static IEmbeddingProvider Build(EmbeddingModelDescriptor model, DocumentEngineOptions options)
    {
        if (string.Equals(model.Provider, DeterministicEmbeddingProvider.ProviderName,
                StringComparison.OrdinalIgnoreCase))
        {
            return new DeterministicEmbeddingProvider(model.Model, model.Dimensions);
        }

        ProviderOptions? providerOptions =
            DocumentEngineOptionsValidator.FindProvider(options.Providers, model.Provider);

        return providerOptions is null
            ? throw new InvalidOperationException(
                $"Provider '{model.Provider}' has no entry under DocumentEngine:Providers.")
            : EmbeddingProviderFactory.Create(model, providerOptions);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Link link in _links)
        {
            if (link.Provider is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        await ValueTask.CompletedTask;
    }

    private sealed record Link(
        EmbeddingModelDescriptor Model,
        IEmbeddingProvider Provider,
        ResiliencePipeline Pipeline);
}
