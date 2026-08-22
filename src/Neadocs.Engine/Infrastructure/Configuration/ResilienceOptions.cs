namespace Neadocs.Engine.Infrastructure.Configuration;

public sealed class ResilienceOptions
{
    public double CircuitBreakerFailureRatio { get; set; } = 0.5;

    public int CircuitBreakerSamplingSeconds { get; set; } = 30;

    public int CircuitBreakerMinimumThroughput { get; set; } = 10;

    public int CircuitBreakerDurationSeconds { get; set; } = 30;

    public int MaxRetries { get; set; } = 3;

    public int RetryBackoffCeilingMs { get; set; } = 10_000;
}
