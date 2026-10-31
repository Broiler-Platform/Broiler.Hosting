namespace Broiler.Hosting.WebAssembly;

/// <summary>
/// Plain-text browser clipboard for Canvas hosts. Inside a trusted DOM copy/cut/paste event it
/// works synchronously on the event's <c>clipboardData</c>; outside one (menus, context menus,
/// toolbar commands) it falls back to the asynchronous Async Clipboard API.
/// </summary>
/// <remarks>
/// <para>The caller supplies its JS interop marshalling, typically <c>createClipboard()</c> from
/// <c>broiler.hosting.webassembly.js</c>.</para>
/// <para><see cref="TryGetText"/> is synchronous, so an untrusted paste cannot complete at once:
/// it returns <see langword="false"/>, starts an asynchronous read, and when the text arrives runs
/// <c>replayPaste</c> inside a synthetic clipboard scope where <see cref="TryGetText"/> succeeds.</para>
/// </remarks>
public sealed class BrowserClipboard
{
    private readonly Func<string, ValueTask<bool>> _write;
    private readonly Func<ValueTask<string?>> _read;
    private readonly Action _replayPaste;
    private readonly Action<string>? _reportFailure;
    private bool _active, _readPending;
    private string? _incoming, _outgoing;

    /// <param name="write">Writes text through <c>navigator.clipboard.writeText</c>; returns <see langword="false"/> when refused.</param>
    /// <param name="read">Reads text through <c>navigator.clipboard.readText</c>; returns <see langword="null"/> when refused or unavailable.</param>
    /// <param name="replayPaste">Re-runs the paste command on the focused element once asynchronous text is available.</param>
    /// <param name="reportFailure">Optional user-visible failure reporter.</param>
    public BrowserClipboard(Func<string, ValueTask<bool>> write, Func<ValueTask<string?>> read, Action replayPaste, Action<string>? reportFailure = null)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _replayPaste = replayPaste ?? throw new ArgumentNullException(nameof(replayPaste));
        _reportFailure = reportFailure;
    }

    /// <summary>True while a trusted (or replayed) clipboard event is being handled.</summary>
    public bool IsEventActive => _active;

    /// <summary>True while an asynchronous read started by an untrusted paste is outstanding.</summary>
    public bool IsReadPending => _readPending;

    public bool TryGetText(out string text)
    {
        if (_active)
        {
            text = _incoming ?? string.Empty;
            return _incoming is not null;
        }
        text = string.Empty;
        if (!_readPending)
        {
            _readPending = true;
            _ = ReadAndReplayAsync();
        }
        return false;
    }

    public void SetText(string text)
    {
        text ??= string.Empty;
        if (_active) { _outgoing = text; return; }
        _ = WriteAsync(text);
    }

    /// <summary>
    /// Runs <paramref name="action"/> for one trusted DOM clipboard event. Returns the text to place
    /// on <c>clipboardData</c>, or <see langword="null"/> when nothing was written. Contents never
    /// survive the callback.
    /// </summary>
    public string? DuringEvent(string? incoming, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_active) throw new InvalidOperationException("Nested clipboard events are not supported.");
        _active = true; _incoming = incoming; _outgoing = null;
        try { action(); return _outgoing; }
        finally { _active = false; _incoming = _outgoing = null; }
    }

    private async Task ReadAndReplayAsync()
    {
        string? text;
        try { text = await _read(); }
        catch (Exception error) { _reportFailure?.Invoke($"Clipboard read failed: {error.Message}"); _readPending = false; return; }
        _readPending = false;
        if (text is null) { _reportFailure?.Invoke("The browser denied clipboard access. Use Ctrl/Cmd+V to paste."); return; }
        if (_active) return; // A trusted event raced the read; it already delivered fresher contents.
        DuringEvent(text, _replayPaste);
    }

    private async Task WriteAsync(string text)
    {
        bool written;
        try { written = await _write(text); }
        catch (Exception) { written = false; }
        if (!written) _reportFailure?.Invoke("The browser denied clipboard access. Use Ctrl/Cmd+C or X to copy.");
    }
}
