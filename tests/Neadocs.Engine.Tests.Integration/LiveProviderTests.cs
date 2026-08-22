namespace Neadocs.Engine.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Providers;

public sealed class LiveProviderTests
{
    private static string? GeminiKey =>
        Environment.GetEnvironmentVariable("NEADOCS_GEMINI_KEY");

    private static string? OpenAiKey =>
        Environment.GetEnvironmentVariable("NEADOCS_OPENAI_KEY");

    private static DocumentEngineOptions Options(string provider, string key, string baseUrl)
    {
        DocumentEngineOptions options = new()
        {
            PostgresConnectionString = "Host=127.0.0.1;Database=x;Username=x;Password=x",
            AllowedProjectKeys = "t:k",
            Text = new TextOptions { Locales = ["en"], DefaultLocale = "en" },
        };

        options.Providers[provider] = new ProviderOptions
        {
            ApiKey = key,
            BaseUrl = baseUrl,
            MaxBatch = 8,
            TimeoutSeconds = 60,
        };

        return options;
    }

    [SkippableFact]
    public async Task GeminiReturnsVectorsOfTheDeclaredWidth()
    {
        Skip.If(string.IsNullOrWhiteSpace(GeminiKey), "NEADOCS_GEMINI_KEY is not set.");

        DocumentEngineOptions options = Options(
            "gemini", GeminiKey!, "https://generativelanguage.googleapis.com/");
        options.EmbeddingModels.Add(new EmbeddingModelOptions
        {
            Provider = "gemini",
            Model = "gemini-embedding-001",
            Dimensions = 768,
        });

        EmbeddingChain chain = new(
            options, new EmbeddingModelRegistry(options), NullLogger<EmbeddingChain>.Instance, null);

        await chain.ProbeDimensionsAsync(CancellationToken.None);

        IReadOnlyList<float[]> vectors = await chain.EmbedAsync(
            "gemini_embedding_001",
            ["menüyü yayınlama", "şifremi unuttum"],
            CancellationToken.None);

        vectors.Should().HaveCount(2);
        vectors[0].Should().HaveCount(768);
        vectors[1].Should().HaveCount(768);
        vectors[0].Should().NotBeEquivalentTo(vectors[1],
            "two different Turkish phrases must not embed identically");
    }

    [SkippableFact]
    public async Task GeminiHonoursWhateverWidthIsDeclared()
    {
        Skip.If(string.IsNullOrWhiteSpace(GeminiKey), "NEADOCS_GEMINI_KEY is not set.");

        foreach (int width in new[] { 256, 1536 })
        {
            DocumentEngineOptions options = Options(
                "gemini", GeminiKey!, "https://generativelanguage.googleapis.com/");
            options.EmbeddingModels.Add(new EmbeddingModelOptions
            {
                Provider = "gemini",
                Model = "gemini-embedding-001",
                Dimensions = width,
            });

            EmbeddingChain chain = new(
                options, new EmbeddingModelRegistry(options), NullLogger<EmbeddingChain>.Instance, null);

            Func<Task> act = () => chain.ProbeDimensionsAsync(CancellationToken.None);

            await act.Should().NotThrowAsync(
                "the Gemini request carries outputDimensionality, so the model returns exactly the "
                + "declared width. The boot probe therefore cannot detect a mismatch for this "
                + "provider — it protects against a provider changing its DEFAULT width, which is "
                + "a real risk for OpenAI but not for one that takes the width as a parameter.");
        }
    }

    [SkippableFact]
    public async Task GeminiReportsHealthy()
    {
        Skip.If(string.IsNullOrWhiteSpace(GeminiKey), "NEADOCS_GEMINI_KEY is not set.");

        DocumentEngineOptions options = Options(
            "gemini", GeminiKey!, "https://generativelanguage.googleapis.com/");
        options.EmbeddingModels.Add(new EmbeddingModelOptions
        {
            Provider = "gemini",
            Model = "gemini-embedding-001",
            Dimensions = 768,
        });

        EmbeddingChain chain = new(
            options, new EmbeddingModelRegistry(options), NullLogger<EmbeddingChain>.Instance, null);

        IReadOnlyList<ProviderHealth> health = await chain.HealthAsync(CancellationToken.None);

        health.Should().ContainSingle().Which.Healthy.Should().BeTrue();
    }

    [SkippableFact]
    public async Task AnInvalidKeyIsReportedAsUnhealthyRatherThanThrowing()
    {
        Skip.If(string.IsNullOrWhiteSpace(GeminiKey), "NEADOCS_GEMINI_KEY is not set.");

        DocumentEngineOptions options = Options(
            "gemini", "definitely-not-a-real-key", "https://generativelanguage.googleapis.com/");
        options.EmbeddingModels.Add(new EmbeddingModelOptions
        {
            Provider = "gemini",
            Model = "gemini-embedding-001",
            Dimensions = 768,
        });

        EmbeddingChain chain = new(
            options, new EmbeddingModelRegistry(options), NullLogger<EmbeddingChain>.Instance, null);

        IReadOnlyList<ProviderHealth> health = await chain.HealthAsync(CancellationToken.None);

        health.Should().ContainSingle().Which.Healthy.Should().BeFalse();
    }

    [SkippableFact]
    public async Task OpenAiReturnsVectorsOfTheDeclaredWidth()
    {
        Skip.If(string.IsNullOrWhiteSpace(OpenAiKey), "NEADOCS_OPENAI_KEY is not set.");

        DocumentEngineOptions options = Options("openai", OpenAiKey!, "https://api.openai.com/");
        options.EmbeddingModels.Add(new EmbeddingModelOptions
        {
            Provider = "openai",
            Model = "text-embedding-3-small",
            Dimensions = 1536,
        });

        EmbeddingChain chain = new(
            options, new EmbeddingModelRegistry(options), NullLogger<EmbeddingChain>.Instance, null);

        await chain.ProbeDimensionsAsync(CancellationToken.None);

        IReadOnlyList<float[]> vectors = await chain.EmbedAsync(
            "text_embedding_3_small", ["publishing a menu"], CancellationToken.None);

        vectors.Should().ContainSingle().Which.Should().HaveCount(1536);
    }
}
