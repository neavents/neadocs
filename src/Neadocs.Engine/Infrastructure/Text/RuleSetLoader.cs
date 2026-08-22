namespace Neadocs.Engine.Infrastructure.Text;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Neadocs.Engine.Infrastructure.Serialization;

public sealed record LoadedRuleSet(CompiledPipeline Pipeline, string Origin, bool FromFile);

public static class RuleSetLoader
{
    private const string EmbeddedPrefix = "Neadocs.Engine.Infrastructure.Text.Defaults.";

    public static IReadOnlyDictionary<string, LoadedRuleSet> Load(string? directory)
    {
        Dictionary<string, LoadedRuleSet> loaded = [];

        foreach ((string origin, string json) in EmbeddedSources())
        {
            Add(loaded, Parse(json, origin), origin, fromFile: false);
        }

        foreach ((string origin, string json) in DirectorySources(directory))
        {
            Add(loaded, Parse(json, origin), origin, fromFile: true);
        }

        if (!loaded.ContainsKey(RuleOperations.FallbackTag))
        {
            throw new RuleSetException(
                $"No rule set carries the fallback tag '{RuleOperations.FallbackTag}'. "
                + "Resolution must never fail, so the fallback is required.");
        }

        return loaded;
    }

    public static IEnumerable<(string Origin, string Json)> EmbeddedSources()
    {
        Assembly assembly = typeof(RuleSetLoader).Assembly;

        foreach (string name in assembly.GetManifestResourceNames()
                     .Where(n => n.StartsWith(EmbeddedPrefix, StringComparison.Ordinal)
                                 && n.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            using Stream? stream = assembly.GetManifestResourceStream(name);

            if (stream is null)
            {
                continue;
            }

            using StreamReader reader = new(stream);

            yield return ($"embedded:{name[EmbeddedPrefix.Length..]}", reader.ReadToEnd());
        }
    }

    private static IEnumerable<(string Origin, string Json)> DirectorySources(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            yield break;
        }

        foreach (string path in Directory
                     .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            yield return (path, File.ReadAllText(path));
        }
    }

    public static RuleSet Parse(string json, string origin)
    {
        try
        {
            RuleSet? parsed = JsonSerializer.Deserialize(json, NeadocsJsonContext.Default.RuleSet);

            return parsed ?? throw new RuleSetException($"{origin}: file is empty or 'null'.");
        }
        catch (JsonException ex)
        {
            throw new RuleSetException(
                $"{origin}: invalid JSON at line {ex.LineNumber + 1}, position "
                + $"{ex.BytePositionInLine}: {ex.Message}");
        }
    }

    private static void Add(
        Dictionary<string, LoadedRuleSet> loaded,
        RuleSet ruleSet,
        string origin,
        bool fromFile)
    {
        CompiledPipeline pipeline = PipelineCompiler.Compile(ruleSet, origin);

        if (loaded.TryGetValue(pipeline.Tag, out LoadedRuleSet? existing) && fromFile == existing.FromFile)
        {
            throw new RuleSetException(
                $"{origin}: tag '{pipeline.Tag}' is already defined by {existing.Origin}. "
                + "Two rule sets from the same source cannot claim one tag.");
        }

        loaded[pipeline.Tag] = new LoadedRuleSet(pipeline, origin, fromFile);
    }
}
