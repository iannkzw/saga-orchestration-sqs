namespace OrderService.Diagnostics;

public sealed class OptimisticRetryCounter
{
    private long _count;

    public long Value => Interlocked.Read(ref _count);

    public void Increment() => Interlocked.Increment(ref _count);

    public void Reset() => Interlocked.Exchange(ref _count, 0);
}
