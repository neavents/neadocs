namespace Neadocs.Engine.Infrastructure.Configuration;

public sealed class EmbeddingModelOptions
{
    public string Provider { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int Dimensions { get; set; }

    public bool Retired { get; set; }
}
