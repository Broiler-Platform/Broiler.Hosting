namespace Broiler.Hosting;

/// <summary>Single-thread event-loop queue. Work posted while draining runs on the next frame.</summary>
public sealed class FrameDispatcher(Action requestFrame) : IDisposable
{
    private readonly Queue<Action> _pending = new();
    private bool _scheduled;
    private bool _disposed;

    public void Post(Action callback)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(callback);
        _pending.Enqueue(callback);
        if (!_scheduled) { _scheduled = true; requestFrame(); }
    }

    public int Drain()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _scheduled = true;
        int count = _pending.Count, completed = 0;
        try {
            while (completed < count && !_disposed) { _pending.Dequeue()(); completed++; }
            return completed;
        } finally {
            _scheduled = false;
            if (!_disposed && _pending.Count > 0) { _scheduled = true; requestFrame(); }
        }
    }

    public void Dispose() { _disposed = true; _pending.Clear(); }
}
