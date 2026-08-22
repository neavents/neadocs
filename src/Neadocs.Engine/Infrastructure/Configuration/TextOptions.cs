namespace Neadocs.Engine.Infrastructure.Configuration;

using System.Collections.Generic;

public sealed class TextOptions
{
    public List<string> Locales { get; set; } = [];

    public string NormalizersDirectory { get; set; } = "./normalizers";

    public string DefaultLocale { get; set; } = string.Empty;

    public Dictionary<string, List<string>> LocaleFallback { get; set; } = [];

    public double TrigramThreshold { get; set; } = 0.3;

    public Dictionary<string, List<SynonymGroupOptions>> Synonyms { get; set; } = [];
}

public sealed class SynonymGroupOptions
{
    public List<string> Terms { get; set; } = [];
}
