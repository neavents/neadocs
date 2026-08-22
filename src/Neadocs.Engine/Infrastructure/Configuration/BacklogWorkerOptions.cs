namespace Neadocs.Engine.Infrastructure.Configuration;

public sealed class BacklogWorkerOptions
{
    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 30;

    public int BatchSize { get; set; } = 100;

    public int MaxAttempts { get; set; } = 10;
}
