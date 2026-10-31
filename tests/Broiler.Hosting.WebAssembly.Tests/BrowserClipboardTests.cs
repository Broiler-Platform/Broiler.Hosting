using Broiler.Hosting.WebAssembly;
using Xunit;

namespace Broiler.Hosting.Tests;

public class BrowserClipboardTests
{
    private static BrowserClipboard Create(Func<string, ValueTask<bool>>? write = null, Func<ValueTask<string?>>? read = null, Action? replay = null, List<string>? failures = null) =>
        new(write ?? (_ => ValueTask.FromResult(true)), read ?? (() => ValueTask.FromResult<string?>(null)), replay ?? (() => { }), failures is null ? null : failures.Add);

    [Fact] public void TrustedEventIsSynchronousAndScoped()
    {
        int writes = 0;
        var clipboard = Create(write: _ => { writes++; return ValueTask.FromResult(true); });
        string? output = clipboard.DuringEvent("in", () =>
        {
            Assert.True(clipboard.TryGetText(out var text)); Assert.Equal("in", text);
            clipboard.SetText("out");
        });
        Assert.Equal("out", output);
        Assert.Equal(0, writes);
        Assert.False(clipboard.IsEventActive);
    }

    [Fact] public void CopyDuringEventWithoutPasteTextReportsNoText()
    {
        var clipboard = Create();
        clipboard.DuringEvent(null, () => Assert.False(clipboard.TryGetText(out _)));
        Assert.False(clipboard.IsReadPending);
    }

    [Fact] public void ScopeIsClearedOnFailure()
    {
        var clipboard = Create();
        Assert.Throws<InvalidOperationException>(() => clipboard.DuringEvent("secret", () => throw new InvalidOperationException()));
        Assert.False(clipboard.IsEventActive);
        Assert.Throws<InvalidOperationException>(() => clipboard.DuringEvent("a", () => clipboard.DuringEvent("b", () => { })));
    }

    [Fact] public void UntrustedWriteUsesAsyncTransport()
    {
        var written = new List<string>();
        var clipboard = Create(write: text => { written.Add(text); return ValueTask.FromResult(true); });
        clipboard.SetText("menu copy");
        Assert.Equal(new[] { "menu copy" }, written);
    }

    [Fact] public void RefusedWriteIsReported()
    {
        var failures = new List<string>();
        var clipboard = Create(write: _ => ValueTask.FromResult(false), failures: failures);
        clipboard.SetText("x");
        Assert.Single(failures);
    }

    [Fact] public async Task UntrustedPasteReadsThenReplaysInsideScope()
    {
        var source = new TaskCompletionSource<string?>();
        string? pasted = null; int reads = 0;
        BrowserClipboard clipboard = null!;
        clipboard = Create(
            read: () => { reads++; return new ValueTask<string?>(source.Task); },
            replay: () => { Assert.True(clipboard.TryGetText(out var text)); pasted = text; });

        Assert.False(clipboard.TryGetText(out _));
        Assert.False(clipboard.TryGetText(out _)); // coalesced while pending
        Assert.True(clipboard.IsReadPending);
        Assert.Equal(1, reads);

        source.SetResult("from browser");
        await Task.Yield();
        await WaitUntil(() => pasted is not null);
        Assert.Equal("from browser", pasted);
        Assert.False(clipboard.IsReadPending);
        Assert.False(clipboard.IsEventActive);
    }

    [Fact] public void DeniedReadIsReportedWithoutReplay()
    {
        var failures = new List<string>(); int replays = 0;
        var clipboard = Create(read: () => ValueTask.FromResult<string?>(null), replay: () => replays++, failures: failures);
        Assert.False(clipboard.TryGetText(out _));
        Assert.Equal(0, replays);
        Assert.Single(failures);
        Assert.False(clipboard.IsReadPending);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
    }
}
