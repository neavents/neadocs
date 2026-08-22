namespace Neadocs.Engine.Infrastructure.Http;

public sealed class ProblemResponse
{
    public string Type { get; set; } = "about:blank";

    public string Title { get; set; } = string.Empty;

    public int Status { get; set; }

    public string? Detail { get; set; }

    public string? CorrelationId { get; set; }
}
