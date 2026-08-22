namespace Neadocs.Engine.Infrastructure.Storage;

using System.Threading;

public sealed class MigrationState
{
    private int _completed;

    public bool Completed => Volatile.Read(ref _completed) == 1;

    public void MarkCompleted() => Volatile.Write(ref _completed, 1);
}
