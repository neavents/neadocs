namespace Neadocs.Engine.Infrastructure.Configuration;

public sealed class ProviderOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public int MaxBatch { get; set; } = 64;

    public int MaxConcurrentRequests { get; set; } = 8;

    public int TimeoutSeconds { get; set; } = 30;
}
