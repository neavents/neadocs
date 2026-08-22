namespace Neadocs.Engine.Infrastructure.Configuration;

public sealed class ChunkingOptions
{
    public int TargetTokens { get; set; } = 400;

    public int OverlapPercent { get; set; } = 15;

    public int SplitAtHeadingLevel { get; set; } = 2;

    public double CharsPerToken { get; set; } = 3.5;

    public int MaxChunksPerDocument { get; set; } = 500;
}
