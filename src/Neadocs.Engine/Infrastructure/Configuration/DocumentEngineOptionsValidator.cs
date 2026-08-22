namespace Neadocs.Engine.Infrastructure.Configuration;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Neadocs.Engine.Infrastructure.Storage;
using Neadocs.Engine.Infrastructure.Text;

public static class DocumentEngineOptionsValidator
{
    private const string Section = "DocumentEngine";

    private static readonly string[] SearchModes = ["lexical", "vector", "hybrid"];

    public static IReadOnlyList<string> Validate(DocumentEngineOptions options)
    {
        List<string> errors = [];

        ValidateStorage(options, errors);
        ValidateRetrieval(options, errors);
        ValidateChunking(options.Chunking, errors);
        HashSet<string> locales = ValidateText(options.Text, errors);
        ValidateEmbeddingModels(options, errors);
        ValidateAuth(options, errors);
        ValidateLimits(options, errors);
        ValidateResilience(options.Resilience, errors);
        ValidateBacklogWorker(options.BacklogWorker, errors);
        ValidateSynonyms(options.Text, locales, errors);

        return errors;
    }

    public static void ThrowIfInvalid(DocumentEngineOptions options)
    {
        IReadOnlyList<string> errors = Validate(options);

        if (errors.Count == 0)
        {
            return;
        }

        StringBuilder message = new();
        message.Append(errors.Count);
        message.Append(errors.Count == 1 ? " configuration error:" : " configuration errors:");

        foreach (string error in errors)
        {
            message.Append("\n  - ");
            message.Append(error);
        }

        throw new InvalidOperationException(message.ToString());
    }

    private static void ValidateStorage(DocumentEngineOptions options, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(options.PostgresConnectionString))
        {
            errors.Add($"{Section}:PostgresConnectionString must be set.");
        }

        if (!SqlIdentifier.IsValid(options.Schema))
        {
            errors.Add(
                $"{Section}:Schema must be a bare lowercase SQL identifier matching " +
                $"[a-z_][a-z0-9_]{{0,{SqlIdentifier.MaxLength - 1}}}; got '{options.Schema}'.");
        }

        if (options.DatabaseCommandTimeoutSeconds <= 0)
        {
            errors.Add($"{Section}:DatabaseCommandTimeoutSeconds must be greater than 0.");
        }
    }

    private static void ValidateRetrieval(DocumentEngineOptions options, List<string> errors)
    {
        string? mode = SearchModes.FirstOrDefault(
            m => string.Equals(m, options.DefaultSearchMode, StringComparison.OrdinalIgnoreCase));

        if (mode is null)
        {
            errors.Add(
                $"{Section}:DefaultSearchMode must be one of lexical, vector, hybrid; " +
                $"got '{options.DefaultSearchMode}'.");
        }
        else if (mode == "vector" && !options.EmbeddingModels.Any(m => !m.Retired))
        {
            errors.Add(
                $"{Section}:DefaultSearchMode is 'vector' but no non-retired entry exists in " +
                $"{Section}:EmbeddingModels, so no search request could ever be served. " +
                "Use 'hybrid', which degrades to lexical, or configure a model.");
        }

        if (options.RrfK <= 0)
        {
            errors.Add($"{Section}:RrfK must be greater than 0; got {options.RrfK}.");
        }

        if (options.VectorMinSimilarity is < 0 or > 1)
        {
            errors.Add(
                $"{Section}:VectorMinSimilarity must be between 0 and 1; got " +
                options.VectorMinSimilarity.ToString(CultureInfo.InvariantCulture) +
                ". It is a cosine similarity, not a rank or a percentage.");
        }

        if (options.CandidateMultiplier <= 0)
        {
            errors.Add($"{Section}:CandidateMultiplier must be greater than 0; got {options.CandidateMultiplier}.");
        }

        if (options.MinCandidates <= 0)
        {
            errors.Add($"{Section}:MinCandidates must be greater than 0; got {options.MinCandidates}.");
        }

        if (options.HnswEfSearch <= 0)
        {
            errors.Add($"{Section}:HnswEfSearch must be greater than 0; got {options.HnswEfSearch}.");
        }

        if (options.MinRecallAt3 is < 0 or > 1)
        {
            errors.Add(
                $"{Section}:MinRecallAt3 must be between 0 and 1; got " +
                $"{options.MinRecallAt3.ToString(CultureInfo.InvariantCulture)}.");
        }
    }

    private static void ValidateChunking(ChunkingOptions chunking, List<string> errors)
    {
        if (chunking.TargetTokens is < 50 or > 4000)
        {
            errors.Add($"{Section}:Chunking:TargetTokens must be between 50 and 4000; got {chunking.TargetTokens}.");
        }

        if (chunking.OverlapPercent is < 0 or > 50)
        {
            errors.Add($"{Section}:Chunking:OverlapPercent must be between 0 and 50; got {chunking.OverlapPercent}.");
        }

        if (chunking.SplitAtHeadingLevel is < 1 or > 6)
        {
            errors.Add(
                $"{Section}:Chunking:SplitAtHeadingLevel must be between 1 and 6; " +
                $"got {chunking.SplitAtHeadingLevel}.");
        }

        if (chunking.CharsPerToken <= 0)
        {
            errors.Add(
                $"{Section}:Chunking:CharsPerToken must be greater than 0; got " +
                $"{chunking.CharsPerToken.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (chunking.MaxChunksPerDocument <= 0)
        {
            errors.Add(
                $"{Section}:Chunking:MaxChunksPerDocument must be greater than 0; " +
                $"got {chunking.MaxChunksPerDocument}.");
        }
    }

    private static HashSet<string> ValidateText(TextOptions text, List<string> errors)
    {
        HashSet<string> locales = [];

        if (text.Locales.Count == 0)
        {
            errors.Add($"{Section}:Text:Locales must declare at least one locale.");
        }

        foreach (string declared in text.Locales)
        {
            string normalized = LocaleTag.Normalize(declared);

            if (!LocaleTag.IsWellFormed(normalized))
            {
                errors.Add(
                    $"{Section}:Text:Locales contains '{declared}', which is not a well-formed " +
                    "BCP-47 tag (expected forms: tr, en-gb, zh-hant-tw).");
                continue;
            }

            if (!locales.Add(normalized))
            {
                errors.Add($"{Section}:Text:Locales lists '{normalized}' more than once.");
            }
        }

        string defaultLocale = LocaleTag.Normalize(text.DefaultLocale);

        if (string.IsNullOrEmpty(defaultLocale))
        {
            errors.Add($"{Section}:Text:DefaultLocale must be set.");
        }
        else if (!locales.Contains(defaultLocale))
        {
            errors.Add(
                $"{Section}:Text:DefaultLocale is '{text.DefaultLocale}', which is not listed in " +
                $"{Section}:Text:Locales.");
        }

        if (string.IsNullOrWhiteSpace(text.NormalizersDirectory))
        {
            errors.Add($"{Section}:Text:NormalizersDirectory must be set.");
        }

        if (text.TrigramThreshold is < 0 or > 1)
        {
            errors.Add(
                $"{Section}:Text:TrigramThreshold must be between 0 and 1; got " +
                $"{text.TrigramThreshold.ToString(CultureInfo.InvariantCulture)}.");
        }

        ValidateLocaleFallback(text, locales, errors);

        return locales;
    }

    private static void ValidateLocaleFallback(
        TextOptions text,
        HashSet<string> locales,
        List<string> errors)
    {
        Dictionary<string, List<string>> graph = [];

        foreach (KeyValuePair<string, List<string>> entry in text.LocaleFallback)
        {
            string from = LocaleTag.Normalize(entry.Key);

            if (!locales.Contains(from))
            {
                errors.Add(
                    $"{Section}:Text:LocaleFallback has key '{entry.Key}', which is not listed in " +
                    $"{Section}:Text:Locales.");
                continue;
            }

            List<string> targets = [];

            foreach (string target in entry.Value)
            {
                string to = LocaleTag.Normalize(target);

                if (!locales.Contains(to))
                {
                    errors.Add(
                        $"{Section}:Text:LocaleFallback['{entry.Key}'] falls back to '{target}', " +
                        $"which is not listed in {Section}:Text:Locales.");
                    continue;
                }

                if (to == from)
                {
                    errors.Add($"{Section}:Text:LocaleFallback['{entry.Key}'] falls back to itself.");
                    continue;
                }

                targets.Add(to);
            }

            graph[from] = targets;
        }

        DetectFallbackCycles(graph, errors);
    }

    private static void DetectFallbackCycles(
        Dictionary<string, List<string>> graph,
        List<string> errors)
    {
        HashSet<string> settled = [];
        HashSet<string> reported = [];

        foreach (string start in graph.Keys)
        {
            if (settled.Contains(start))
            {
                continue;
            }

            List<string> path = [];
            HashSet<string> onPath = [];

            Walk(start, graph, settled, onPath, path, reported, errors);
        }
    }

    private static void Walk(
        string node,
        Dictionary<string, List<string>> graph,
        HashSet<string> settled,
        HashSet<string> onPath,
        List<string> path,
        HashSet<string> reported,
        List<string> errors)
    {
        if (settled.Contains(node))
        {
            return;
        }

        if (!onPath.Add(node))
        {
            int start = path.IndexOf(node);
            IEnumerable<string> cycle = path.Skip(start).Append(node);
            string rendered = string.Join(" -> ", cycle);

            if (reported.Add(rendered))
            {
                errors.Add($"{Section}:Text:LocaleFallback contains a cycle: {rendered}.");
            }

            return;
        }

        path.Add(node);

        if (graph.TryGetValue(node, out List<string>? targets))
        {
            foreach (string target in targets)
            {
                Walk(target, graph, settled, onPath, path, reported, errors);
            }
        }

        path.RemoveAt(path.Count - 1);
        onPath.Remove(node);
        settled.Add(node);
    }

    private static void ValidateSynonyms(
        TextOptions text,
        HashSet<string> locales,
        List<string> errors)
    {
        foreach (KeyValuePair<string, List<SynonymGroupOptions>> entry in text.Synonyms)
        {
            string locale = LocaleTag.Normalize(entry.Key);

            if (!locales.Contains(locale))
            {
                errors.Add(
                    $"{Section}:Text:Synonyms has key '{entry.Key}', which is not listed in " +
                    $"{Section}:Text:Locales.");
                continue;
            }

            for (int i = 0; i < entry.Value.Count; i++)
            {
                if (entry.Value[i].Terms.Count < 2)
                {
                    errors.Add(
                        $"{Section}:Text:Synonyms['{entry.Key}'][{i}] must list at least two terms; " +
                        "a group of one expands to nothing.");
                }
            }
        }
    }

    private static void ValidateEmbeddingModels(DocumentEngineOptions options, List<string> errors)
    {
        Dictionary<string, string> slugs = [];

        for (int i = 0; i < options.EmbeddingModels.Count; i++)
        {
            EmbeddingModelOptions model = options.EmbeddingModels[i];
            string path = $"{Section}:EmbeddingModels:{i}";

            if (string.IsNullOrWhiteSpace(model.Model))
            {
                errors.Add($"{path}:Model must be set.");
                continue;
            }

            string slug = ModelSlug.From(model.Model);

            if (!ModelSlug.IsValid(slug))
            {
                errors.Add(
                    $"{path}:Model is '{model.Model}', which yields no usable table slug. " +
                    "It must contain at least one ASCII letter or digit.");
                continue;
            }

            if (slugs.TryGetValue(slug, out string? existing))
            {
                errors.Add(
                    $"{path}:Model is '{model.Model}', which yields the same table slug '{slug}' " +
                    $"as '{existing}'. Two models cannot share one embedding table.");
            }
            else
            {
                slugs[slug] = model.Model;
            }

            if (model.Dimensions <= 0)
            {
                errors.Add($"{path}:Dimensions must be greater than 0; got {model.Dimensions}.");
            }

            if (model.Retired)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(model.Provider))
            {
                errors.Add($"{path}:Provider must be set.");
                continue;
            }

            if (string.Equals(model.Provider, "deterministic", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ProviderOptions? provider = FindProvider(options.Providers, model.Provider);

            if (provider is null)
            {
                string known = options.Providers.Count == 0
                    ? "none are configured"
                    : string.Join(", ", options.Providers.Keys.OrderBy(k => k, StringComparer.Ordinal));

                errors.Add(
                    $"{path}:Provider is '{model.Provider}', which has no entry under " +
                    $"{Section}:Providers ({known}).");
                continue;
            }

            if (string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                errors.Add(
                    $"{Section}:Providers:{model.Provider}:ApiKey must be set because " +
                    $"{path} uses it. Supply it as " +
                    $"{Section}__Providers__{model.Provider}__ApiKey.");
            }

            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            {
                errors.Add($"{Section}:Providers:{model.Provider}:BaseUrl must be set.");
            }
            else if (!IsHttpUrl(provider.BaseUrl))
            {
                errors.Add(
                    $"{Section}:Providers:{model.Provider}:BaseUrl must be an absolute http or " +
                    $"https URL; got '{provider.BaseUrl}'.");
            }

            if (provider.MaxBatch <= 0)
            {
                errors.Add($"{Section}:Providers:{model.Provider}:MaxBatch must be greater than 0.");
            }

            if (provider.MaxConcurrentRequests <= 0)
            {
                errors.Add(
                    $"{Section}:Providers:{model.Provider}:MaxConcurrentRequests must be greater than 0.");
            }

            if (provider.TimeoutSeconds <= 0)
            {
                errors.Add($"{Section}:Providers:{model.Provider}:TimeoutSeconds must be greater than 0.");
            }
        }
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static ProviderOptions? FindProvider(
        Dictionary<string, ProviderOptions> providers,
        string name)
    {
        foreach (KeyValuePair<string, ProviderOptions> entry in providers)
        {
            if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static void ValidateAuth(DocumentEngineOptions options, List<string> errors)
    {
        bool hasJwt = !string.IsNullOrWhiteSpace(options.JwtSymmetricKey);
        bool hasProjectKeys = !string.IsNullOrWhiteSpace(options.AllowedProjectKeys);

        if (!hasJwt && !hasProjectKeys)
        {
            errors.Add(
                $"No credential mechanism is configured: set {Section}:JwtSymmetricKey, " +
                $"{Section}:AllowedProjectKeys, or both. Every route except /health, /ready and " +
                "/metrics requires a credential, so the service would answer nothing.");
        }

        if (hasJwt && Encoding.UTF8.GetByteCount(options.JwtSymmetricKey) < 32)
        {
            errors.Add(
                $"{Section}:JwtSymmetricKey must be at least 32 bytes; got " +
                $"{Encoding.UTF8.GetByteCount(options.JwtSymmetricKey)}.");
        }

        if (options.JwtClockSkewSeconds < 0)
        {
            errors.Add($"{Section}:JwtClockSkewSeconds must not be negative.");
        }
    }

    private static void ValidateLimits(DocumentEngineOptions options, List<string> errors)
    {
        if (options.MaxRequestBodyBytes <= 0)
        {
            errors.Add($"{Section}:MaxRequestBodyBytes must be greater than 0.");
        }

        if (options.MaxQueryLength <= 0)
        {
            errors.Add($"{Section}:MaxQueryLength must be greater than 0.");
        }

        if (options.MaxSearchLimit <= 0)
        {
            errors.Add($"{Section}:MaxSearchLimit must be greater than 0.");
        }

        if (options.MaxBulkDocuments <= 0)
        {
            errors.Add($"{Section}:MaxBulkDocuments must be greater than 0.");
        }

        if (options.RateLimitPermitCount <= 0)
        {
            errors.Add($"{Section}:RateLimitPermitCount must be greater than 0.");
        }

        if (options.RateLimitWindowSeconds <= 0)
        {
            errors.Add($"{Section}:RateLimitWindowSeconds must be greater than 0.");
        }

        if (options.RateLimitQueueSize < 0)
        {
            errors.Add($"{Section}:RateLimitQueueSize must not be negative.");
        }
    }

    private static void ValidateResilience(ResilienceOptions resilience, List<string> errors)
    {
        if (resilience.CircuitBreakerFailureRatio is <= 0 or > 1)
        {
            errors.Add(
                $"{Section}:Resilience:CircuitBreakerFailureRatio must be greater than 0 and at " +
                $"most 1; got {resilience.CircuitBreakerFailureRatio.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (resilience.CircuitBreakerSamplingSeconds <= 0)
        {
            errors.Add($"{Section}:Resilience:CircuitBreakerSamplingSeconds must be greater than 0.");
        }

        if (resilience.CircuitBreakerMinimumThroughput < 2)
        {
            errors.Add($"{Section}:Resilience:CircuitBreakerMinimumThroughput must be at least 2.");
        }

        if (resilience.CircuitBreakerDurationSeconds <= 0)
        {
            errors.Add($"{Section}:Resilience:CircuitBreakerDurationSeconds must be greater than 0.");
        }

        if (resilience.MaxRetries < 0)
        {
            errors.Add($"{Section}:Resilience:MaxRetries must not be negative.");
        }

        if (resilience.RetryBackoffCeilingMs <= 0)
        {
            errors.Add($"{Section}:Resilience:RetryBackoffCeilingMs must be greater than 0.");
        }
    }

    private static void ValidateBacklogWorker(BacklogWorkerOptions worker, List<string> errors)
    {
        if (!worker.Enabled)
        {
            return;
        }

        if (worker.IntervalSeconds <= 0)
        {
            errors.Add($"{Section}:BacklogWorker:IntervalSeconds must be greater than 0.");
        }

        if (worker.BatchSize <= 0)
        {
            errors.Add($"{Section}:BacklogWorker:BatchSize must be greater than 0.");
        }

        if (worker.MaxAttempts <= 0)
        {
            errors.Add($"{Section}:BacklogWorker:MaxAttempts must be greater than 0.");
        }
    }
}
