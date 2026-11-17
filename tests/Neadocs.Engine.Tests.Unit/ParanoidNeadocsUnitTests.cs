namespace Neadocs.Engine.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Providers;
using Neadocs.Engine.Infrastructure.Retrieval;
using Neadocs.Engine.Infrastructure.Security;
using Neadocs.Engine.Infrastructure.Storage;

public sealed class ParanoidNeadocsUnitTests
{
    [Fact]
    public void EmbeddingStore_Literal_RejectsNaNAndInfinityAndEmpty()
    {
        Action nanAct = () => EmbeddingStore.Literal([1.0f, float.NaN, 0.5f]);
        Action posInfAct = () => EmbeddingStore.Literal([float.PositiveInfinity, 0.0f]);
        Action negInfAct = () => EmbeddingStore.Literal([0.0f, float.NegativeInfinity]);
        Action emptyAct = () => EmbeddingStore.Literal([]);

        nanAct.Should().Throw<ArgumentException>();
        posInfAct.Should().Throw<ArgumentException>();
        negInfAct.Should().Throw<ArgumentException>();
        emptyAct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task EmbeddingChain_WrongVectorDimensions_ThrowsEmbeddingRequestFailedException()
    {
        EmbeddingChain chain = BuildChain(
            dimensions: 8,
            _ => Task.FromResult<IReadOnlyList<float[]>>([new float[4]]));

        Func<Task> act = () => chain.EmbedAsync("stub_model", ["hello"], CancellationToken.None);

        await act.Should().ThrowAsync<EmbeddingRequestFailedException>()
            .WithMessage("*dimension*");
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public async Task EmbeddingChain_NonFiniteVectorElement_ThrowsEmbeddingRequestFailedException(float badValue)
    {
        EmbeddingChain chain = BuildChain(
            dimensions: 4,
            _ => Task.FromResult<IReadOnlyList<float[]>>([new[] { 1.0f, badValue, 0.5f, 0.25f }]));

        Func<Task> act = () => chain.EmbedAsync("stub_model", ["hello"], CancellationToken.None);

        await act.Should().ThrowAsync<EmbeddingRequestFailedException>()
            .WithMessage("*finite*");
    }

    [Fact]
    public async Task EmbeddingChain_WrongVectorCount_ThrowsEmbeddingRequestFailedException()
    {
        EmbeddingChain chain = BuildChain(
            dimensions: 4,
            _ => Task.FromResult<IReadOnlyList<float[]>>([new float[4]]));

        Func<Task> act = () => chain.EmbedAsync("stub_model", ["one", "two"], CancellationToken.None);

        await act.Should().ThrowAsync<EmbeddingRequestFailedException>()
            .WithMessage("*returned 1 vectors for 2 inputs*");
    }

    [Fact]
    public void RrfFuser_DeterministicOrderingAndNonPositiveKRejected()
    {
        Action badK = () => RrfFuser.Fuse([], [], 0);
        badK.Should().Throw<ArgumentOutOfRangeException>();

        Guid a = Guid.Parse("00000000-0000-0000-0000-000000000001");
        Guid b = Guid.Parse("00000000-0000-0000-0000-000000000002");

        List<FusedChunk> fused = RrfFuser.Fuse(
            [new RankedChunk(b, 2, 0.9), new RankedChunk(a, 1, 0.9)],
            [new RankedChunk(a, 1, 0.8), new RankedChunk(b, 2, 0.8)],
            60);

        fused.Should().HaveCount(2);
        fused[0].ChunkId.Should().Be(a);
        fused[1].ChunkId.Should().Be(b);
    }

    [Fact]
    public void ApiKeyValidator_MatchesFullKeyAndRespectsUtf8Bytes()
    {
        ApiKeyValidator validator = new("t1:Anahtar-şğü-1:read+write,t2:Anahtar-2:read");

        validator.TryResolve("Anahtar-şğü-1", out string tenant, out DocumentScope scopes).Should().BeTrue();
        tenant.Should().Be("t1");
        scopes.Should().Be(DocumentScope.Read | DocumentScope.Write);

        validator.TryResolve("Anahtar-şğü-", out _, out _).Should().BeFalse();
        validator.TryResolve("Anahtar-şğü-1x", out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":null}")]
    [InlineData("{\"data\":[{\"embedding\":[\"NaN\"]}]}")]
    [InlineData("not-json")]
    public async Task OpenAiEmbeddingProvider_MalformedOrUnexpectedHttp200Payload_ThrowsEmbeddingRequestFailedException(string body)
    {
        using var handler = new FixedHttpHandler(HttpStatusCode.OK, body);
        using var provider = new OpenAiEmbeddingProvider("text-embedding-3-small", 4,
            new ProviderOptions { ApiKey = "k", BaseUrl = "https://api.openai.test/" }, handler);

        Func<Task> act = () => provider.EmbedAsync(["hello"], CancellationToken.None);

        await act.Should().ThrowAsync<EmbeddingRequestFailedException>();
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"embeddings\":[{\"values\":123}]}")]
    [InlineData("{\"embeddings\":[{}]}")]
    public async Task GeminiEmbeddingProvider_MalformedOrUnexpectedHttp200Payload_ThrowsEmbeddingRequestFailedException(string body)
    {
        using var handler = new FixedHttpHandler(HttpStatusCode.OK, body);
        using var provider = new GeminiEmbeddingProvider("gemini-embedding-001", 4,
            new ProviderOptions { ApiKey = "k", BaseUrl = "https://api.gemini.test/" }, handler);

        Func<Task> act = () => provider.EmbedAsync(["hello"], CancellationToken.None);

        await act.Should().ThrowAsync<EmbeddingRequestFailedException>();
    }

    private sealed class FixedHttpHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private static EmbeddingChain BuildChain(
        int dimensions,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<float[]>>> handler)
    {
        DocumentEngineOptions options = new()
        {
            PostgresConnectionString = "Host=127.0.0.1;Database=x;Username=x;Password=x",
            AllowedProjectKeys = "t:k",
            Text = new TextOptions { Locales = ["en"], DefaultLocale = "en" },
            Resilience = new ResilienceOptions
            {
                MaxRetries = 1,
                RetryBackoffCeilingMs = 10,
                CircuitBreakerMinimumThroughput = 100,
            },
        };
        options.EmbeddingModels.Add(new EmbeddingModelOptions
        {
            Provider = "stub",
            Model = "stub-model",
            Dimensions = dimensions,
        });

        return new EmbeddingChain(
            options,
            new EmbeddingModelRegistry(options),
            NullLogger<EmbeddingChain>.Instance,
            _ => new DelegateEmbeddingProvider("stub", "stub-model", dimensions, handler));
    }

    private sealed class DelegateEmbeddingProvider(
        string name,
        string model,
        int dimensions,
        Func<IReadOnlyList<string>, Task<IReadOnlyList<float[]>>> handler) : IEmbeddingProvider
    {
        public string Name => name;
        public string Model => model;
        public int Dimensions => dimensions;
        public int MaxBatch => 16;
        public int MaxConcurrentRequests => 1;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            handler(texts);

        public Task<bool> IsHealthyAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
