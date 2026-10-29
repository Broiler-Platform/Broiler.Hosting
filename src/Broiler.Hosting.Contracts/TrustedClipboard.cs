namespace Broiler.Hosting;

/// <summary>Transient plain-text access within one synchronous browser clipboard event.</summary>
public sealed class TrustedClipboard
{
    private bool _active;
    private string? _incoming, _outgoing;
    public bool TryGetText(out string text) { text = _incoming ?? string.Empty; return _active && _incoming is not null; }
    public void SetText(string text) { if (!_active) throw new InvalidOperationException("Clipboard writes require a trusted event."); _outgoing = text; }

    /// <summary>The host must call this only from a trusted event; contents never survive the callback.</summary>
    public string? DuringEvent(string? incoming, Action action)
    {
        if (_active) throw new InvalidOperationException("Nested clipboard events are not supported.");
        _active = true; _incoming = incoming;
        try { action(); return _outgoing; }
        finally { _active = false; _incoming = _outgoing = null; }
    }
}
