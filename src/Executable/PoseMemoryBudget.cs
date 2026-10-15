namespace VeraFirefly.VmdConverter.Core.Executable;

/// <summary>Bound concurrent pose caches; a single oversized motion runs alone.</summary>
internal sealed class PoseMemoryBudget(long capacity)
{
    private readonly object sync = new();
    private long used;
    public IDisposable Reserve(long estimate)
    {
        long charge = Math.Min(capacity, Math.Max(1, estimate));
        lock (sync)
        {
            while (used + charge > capacity) Monitor.Wait(sync);
            used += charge;
        }
        return new Lease(this, charge);
    }
    private sealed class Lease(PoseMemoryBudget owner, long charge) : IDisposable
    {
        public void Dispose() { lock (owner.sync) { owner.used -= charge; Monitor.PulseAll(owner.sync); } }
    }
}
