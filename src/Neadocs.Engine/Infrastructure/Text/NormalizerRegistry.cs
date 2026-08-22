namespace Neadocs.Engine.Infrastructure.Text;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Options;
using Neadocs.Engine.Infrastructure.Configuration;

public sealed class NormalizerRegistry
{
    private readonly FrozenDictionary<string, LoadedRuleSet> _byTag;
    private readonly CompiledPipeline _fallback;

    public NormalizerRegistry(IOptions<DocumentEngineOptions> options)
        : this(RuleSetLoader.Load(options.Value.Text.NormalizersDirectory))
    {
    }

    public NormalizerRegistry(IReadOnlyDictionary<string, LoadedRuleSet> loaded)
    {
        _byTag = loaded.ToFrozenDictionary(StringComparer.Ordinal);
        _fallback = _byTag[RuleOperations.FallbackTag].Pipeline;
    }

    public IReadOnlyCollection<string> Tags => _byTag.Keys;

    public CompiledPipeline Resolve(string? locale)
    {
        string tag = LocaleTag.Normalize(locale);

        while (tag.Length > 0)
        {
            if (_byTag.TryGetValue(tag, out LoadedRuleSet? match))
            {
                return match.Pipeline;
            }

            int lastDash = tag.LastIndexOf('-');

            if (lastDash <= 0)
            {
                break;
            }

            tag = tag[..lastDash];
        }

        return _fallback;
    }

    public string Normalize(string? locale, string? text) => Resolve(locale).Normalize(text);

    public LoadedRuleSet Describe(string tag) => _byTag[tag];

    public IEnumerable<LoadedRuleSet> All => _byTag.Values;
}
