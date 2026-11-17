namespace Neadocs.Engine.Infrastructure.Providers;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Neadocs.Engine.Infrastructure.Configuration;
using Neadocs.Engine.Infrastructure.Serialization;

public sealed class EmbeddingRequestFailedException : Exception
{
    public EmbeddingRequestFailedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public abstract class HttpEmbeddingProvider : IEmbeddingProvider, IDisposable
{
    protected HttpEmbeddingProvider(
        string name,
        string model,
        int dimensions,
        ProviderOptions options,
        HttpMessageHandler? handler = null)
    {
        Name = name;
        Model = model;
        Dimensions = dimensions;
        MaxBatch = options.MaxBatch;
        MaxConcurrentRequests = options.MaxConcurrentRequests;
        ApiKey = options.ApiKey;

        Client = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        Client.BaseAddress = new Uri(options.BaseUrl);
        Client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }

    public string Name { get; }

    public string Model { get; }

    public int Dimensions { get; }

    public int MaxBatch { get; }

    public int MaxConcurrentRequests { get; }

    protected string ApiKey { get; }

    protected HttpClient Client { get; }

    public abstract Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct);

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            IReadOnlyList<float[]> probe = await EmbedAsync(["neadocs"], ct);

            return probe.Count == 1 && probe[0].Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => Client.Dispose();

    protected async Task<JsonDocument> PostAsync(string path, string payload, CancellationToken ct)
    {
        using StringContent content = new(payload, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Client.PostAsync(path, content, ct);

        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new EmbeddingRequestFailedException(
                $"{Name} returned {(int)response.StatusCode}: {Truncate(body)}");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new EmbeddingRequestFailedException($"{Name} returned unparsable JSON.", ex);
        }
    }

    protected static float[] ReadVector(string providerName, JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new EmbeddingRequestFailedException(
                $"{providerName} returned a non-array embedding vector.");
        }

        int length = array.GetArrayLength();
        float[] vector = new float[length];
        int i = 0;

        foreach (JsonElement value in array.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number
                || !value.TryGetSingle(out float element)
                || !float.IsFinite(element))
            {
                throw new EmbeddingRequestFailedException(
                    $"{providerName} returned an invalid or non-finite vector element.");
            }

            vector[i++] = element;
        }

        return vector;
    }

    private static string Truncate(string body) =>
        body.Length <= 300 ? body : body[..300] + "…";
}

public sealed class OpenAiEmbeddingProvider : HttpEmbeddingProvider
{
    public const string ProviderName = "openai";

    public OpenAiEmbeddingProvider(
        string model,
        int dimensions,
        ProviderOptions options,
        HttpMessageHandler? handler = null)
        : base(ProviderName, model, dimensions, options, handler)
    {
        Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
    }

    public override async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        StringBuilder payload = new();
        payload.Append("{\"model\":").Append(JsonSerializer.Serialize(Model, NeadocsJsonContext.Default.String)).Append(",\"input\":[");

        for (int i = 0; i < texts.Count; i++)
        {
            if (i > 0)
            {
                payload.Append(',');
            }

            payload.Append(JsonSerializer.Serialize(texts[i], NeadocsJsonContext.Default.String));
        }

        payload.Append("]}");

        using JsonDocument document = await PostAsync("v1/embeddings", payload.ToString(), ct);

        if (!document.RootElement.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new EmbeddingRequestFailedException($"{ProviderName} response omitted 'data' array.");
        }

        List<float[]> vectors = [];

        foreach (JsonElement item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("embedding", out JsonElement embedding))
            {
                throw new EmbeddingRequestFailedException($"{ProviderName} response item omitted 'embedding'.");
            }

            vectors.Add(ReadVector(ProviderName, embedding));
        }

        return vectors;
    }
}

public sealed class GeminiEmbeddingProvider : HttpEmbeddingProvider
{
    public const string ProviderName = "gemini";

    public GeminiEmbeddingProvider(
        string model,
        int dimensions,
        ProviderOptions options,
        HttpMessageHandler? handler = null)
        : base(ProviderName, model, dimensions, options, handler)
    {
    }

    public override async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        StringBuilder payload = new();
        payload.Append("{\"requests\":[");

        for (int i = 0; i < texts.Count; i++)
        {
            if (i > 0)
            {
                payload.Append(',');
            }

            payload.Append("{\"model\":\"models/").Append(Model)
                .Append("\",\"content\":{\"parts\":[{\"text\":")
                .Append(JsonSerializer.Serialize(texts[i], NeadocsJsonContext.Default.String))
                .Append("}]},\"outputDimensionality\":").Append(Dimensions).Append('}');
        }

        payload.Append("]}");

        string path = $"v1beta/models/{Model}:batchEmbedContents?key={Uri.EscapeDataString(ApiKey)}";

        using JsonDocument document = await PostAsync(path, payload.ToString(), ct);

        if (!document.RootElement.TryGetProperty("embeddings", out JsonElement embeddings)
            || embeddings.ValueKind != JsonValueKind.Array)
        {
            throw new EmbeddingRequestFailedException($"{ProviderName} response omitted 'embeddings' array.");
        }

        List<float[]> vectors = [];

        foreach (JsonElement item in embeddings.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("values", out JsonElement values))
            {
                throw new EmbeddingRequestFailedException($"{ProviderName} response item omitted 'values'.");
            }

            vectors.Add(ReadVector(ProviderName, values));
        }

        return vectors;
    }
}

public static class EmbeddingProviderFactory
{
    public static IEmbeddingProvider Create(EmbeddingModelDescriptor model, ProviderOptions options) =>
        model.Provider.ToLowerInvariant() switch
        {
            OpenAiEmbeddingProvider.ProviderName => new OpenAiEmbeddingProvider(model.Model, model.Dimensions, options),
            GeminiEmbeddingProvider.ProviderName => new GeminiEmbeddingProvider(model.Model, model.Dimensions, options),
            _ => throw new InvalidOperationException(
                $"'{model.Provider}' is not a known embedding provider. "
                + $"Known providers: {GeminiEmbeddingProvider.ProviderName}, "
                + $"{OpenAiEmbeddingProvider.ProviderName}, "
                + $"{DeterministicEmbeddingProvider.ProviderName}."),
        };
}
