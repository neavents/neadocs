namespace Neadocs.Engine.Infrastructure.Providers;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class DeterministicEmbeddingProvider : IEmbeddingProvider
{
    public const string ProviderName = "deterministic";

    public DeterministicEmbeddingProvider(string model, int dimensions)
    {
        Model = model;
        Dimensions = dimensions;
    }

    public string Name => ProviderName;

    public string Model { get; }

    public int Dimensions { get; }

    public int MaxBatch => 512;

    public int MaxConcurrentRequests => 1;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        List<float[]> vectors = new(texts.Count);

        foreach (string text in texts)
        {
            vectors.Add(Embed(text, Dimensions));
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    public Task<bool> IsHealthyAsync(CancellationToken ct) => Task.FromResult(true);

    public static float[] Embed(string text, int dimensions)
    {
        float[] vector = new float[dimensions];

        Span<byte> hash = stackalloc byte[32];

        foreach (string token in Tokenize(text))
        {
            SHA256.HashData(Encoding.UTF8.GetBytes(token), hash);

            int bucket = (int)(BitConverter.ToUInt32(hash[..4]) % (uint)dimensions);
            float sign = (hash[4] & 1) == 0 ? 1f : -1f;

            vector[bucket] += sign;
        }

        double magnitude = 0;

        foreach (float value in vector)
        {
            magnitude += value * value;
        }

        if (magnitude <= 0)
        {
            vector[0] = 1f;

            return vector;
        }

        float norm = (float)Math.Sqrt(magnitude);

        for (int i = 0; i < vector.Length; i++)
        {
            vector[i] /= norm;
        }

        return vector;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        StringBuilder token = new();

        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                token.Append(c);
                continue;
            }

            if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }
}
