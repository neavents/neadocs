namespace Neadocs.Engine.Infrastructure.Evaluation;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Neadocs.Engine.Features;
using Neadocs.Engine.Infrastructure.Serialization;
using Neadocs.Engine.Infrastructure.Storage;
using Neadocs.Engine.Infrastructure.Text;

public static class CommandLine
{
    public const string SeedFlag = "--seed";
    public const string EvalFlag = "--eval";
    public const string TenantFlag = "--tenant";
    public const string CollectionFlag = "--collection";

    public static bool Handles(string[] args) =>
        args.Length > 0 && (args[0] == SeedFlag || args[0] == EvalFlag);

    public static string? Value(string[] args, string flag)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    public static async Task<int> RunAsync(string[] args, IServiceProvider services, CancellationToken ct)
    {
        string tenant = Value(args, TenantFlag) ?? "default";

        if (args[0] == SeedFlag)
        {
            string? directory = args.Length > 1 ? args[1] : null;

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                Console.Error.WriteLine($"{SeedFlag} needs a directory that exists; got '{directory}'.");

                return 1;
            }

            string collection = Value(args, CollectionFlag) ?? "docs";

            return await SeedAsync(directory, collection, tenant, services, ct);
        }

        string? path = args.Length > 1 ? args[1] : null;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.Error.WriteLine($"{EvalFlag} needs a golden set file; got '{path}'.");

            return 1;
        }

        return await EvalAsync(path, tenant, services, ct);
    }

    private static async Task<int> SeedAsync(
        string directory, string collectionKey, string tenant, IServiceProvider services, CancellationToken ct)
    {
        DocumentStore store = services.GetRequiredService<DocumentStore>();

        await store.UpsertCollectionAsync(tenant, collectionKey, collectionKey, "{}", ct);

        int changed = 0;
        int total = 0;

        foreach (string file in Directory
                     .EnumerateFiles(directory, "*.md", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            string[] segments = relative.Split('/');

            if (segments.Length < 2)
            {
                Console.Error.WriteLine(
                    $"skipped {relative}: expected <locale>/<name>.md so the locale is unambiguous.");
                continue;
            }

            string locale = LocaleTag.Normalize(segments[0]);

            if (!LocaleTag.IsWellFormed(locale))
            {
                Console.Error.WriteLine($"skipped {relative}: '{segments[0]}' is not a BCP-47 tag.");
                continue;
            }

            string externalKey = Path.GetFileNameWithoutExtension(file);
            string raw = await File.ReadAllTextAsync(file, ct);
            (string title, string metadata, string content) = ParseFrontMatter(raw, externalKey);

            UpsertDocumentResponse? result = await store.UpsertDocumentAsync(
                tenant, collectionKey, externalKey, locale, title, content,
                relative, metadata, null, null, force: false, ct);

            total++;

            if (result?.Changed == true)
            {
                changed++;
            }

            Console.WriteLine(
                $"{(result?.Changed == true ? "updated" : "unchanged")}  {locale}/{externalKey}");
        }

        Console.WriteLine($"seeded {total} document(s) into '{collectionKey}'; {changed} changed.");

        return 0;
    }

    private static async Task<int> EvalAsync(
        string path, string tenant, IServiceProvider services, CancellationToken ct)
    {
        EvalRunner runner = services.GetRequiredService<EvalRunner>();

        EvalSet? set = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(path, ct), NeadocsJsonContext.Default.EvalSet);

        if (set is null)
        {
            Console.Error.WriteLine($"{path} did not parse as a golden set.");

            return 1;
        }

        EvalReport? report = await runner.RunAsync(tenant, set, ct);

        if (report is null)
        {
            Console.Error.WriteLine($"collection '{set.Collection}' does not exist for tenant '{tenant}'.");

            return 1;
        }

        Console.WriteLine(Format(report));

        return report.Meets ? 0 : 1;
    }

    public static string Format(EvalReport report)
    {
        StringBuilder output = new();
        output.Append(report.Collection).Append(" [").Append(report.Locale).Append("] ")
            .Append(report.Mode).AppendLine();

        foreach (EvalCaseResult result in report.Cases)
        {
            output.Append(result.Passed ? "  ok    " : "  FAIL  ")
                .Append(result.Query.PadRight(36))
                .Append(result.ActualRank > 0 ? $"rank {result.ActualRank}" : "no match")
                .Append(" (max ").Append(result.MaxRank).Append(')')
                .AppendLine();
        }

        output.AppendLine();
        output.Append("recall@1 ").Append(Percent(report.RecallAt1))
            .Append("  recall@3 ").Append(Percent(report.RecallAt3))
            .Append("  recall@10 ").Append(Percent(report.RecallAt10))
            .Append("  MRR ").Append(report.Mrr.ToString("F3", CultureInfo.InvariantCulture))
            .Append("  mean ").Append(report.MeanLatencyMs.ToString("F1", CultureInfo.InvariantCulture))
            .AppendLine("ms");

        output.Append(report.Passed).Append('/').Append(report.Total).Append(" passed; ")
            .Append(report.Meets ? "meets" : "BELOW")
            .Append(" the recall@3 floor of ")
            .Append(Percent(report.MinRecallAt3));

        return output.ToString();
    }

    private static string Percent(double value) =>
        (value * 100).ToString("F0", CultureInfo.InvariantCulture) + "%";

    private static (string Title, string Metadata, string Content) ParseFrontMatter(
        string raw, string fallbackTitle)
    {
        if (!raw.StartsWith("---", StringComparison.Ordinal))
        {
            return (TitleFrom(raw, fallbackTitle), "{}", raw);
        }

        int end = raw.IndexOf("\n---", 3, StringComparison.Ordinal);

        if (end < 0)
        {
            return (TitleFrom(raw, fallbackTitle), "{}", raw);
        }

        string block = raw[3..end];
        string body = raw[(end + 4)..].TrimStart('\r', '\n');

        string title = fallbackTitle;
        Dictionary<string, string> metadata = [];

        foreach (string line in block.Split('\n'))
        {
            int colon = line.IndexOf(':');

            if (colon <= 0)
            {
                continue;
            }

            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim().Trim('"', '\'');

            if (key.Length == 0 || value.Length == 0)
            {
                continue;
            }

            if (string.Equals(key, "title", StringComparison.OrdinalIgnoreCase))
            {
                title = value;
                continue;
            }

            metadata[key] = value;
        }

        StringBuilder json = new();
        json.Append('{');

        bool first = true;

        foreach (KeyValuePair<string, string> entry in metadata)
        {
            if (!first)
            {
                json.Append(',');
            }

            json.Append(JsonSerializer.Serialize(entry.Key, NeadocsJsonContext.Default.String))
                .Append(':')
                .Append(JsonSerializer.Serialize(entry.Value, NeadocsJsonContext.Default.String));
            first = false;
        }

        json.Append('}');

        return (title == fallbackTitle ? TitleFrom(body, fallbackTitle) : title, json.ToString(), body);
    }

    private static string TitleFrom(string content, string fallback)
    {
        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                return trimmed[2..].Trim();
            }
        }

        return fallback;
    }
}
