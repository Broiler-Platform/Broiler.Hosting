using Broiler.Hosting.WebAssembly;
using Xunit;

namespace Broiler.Hosting.Tests;

public class ContractsTests
{
    [Fact] public async Task PickerCancellationDoesNotProduceADocument()
    {
        var port = new BrowserDocumentPort((_, _) => ValueTask.FromResult(new DocumentOperationResult(DocumentOperationStatus.Cancelled)), (_, _) => throw new NotImplementedException());
        Assert.Equal(DocumentOperationStatus.Cancelled, (await port.OpenAsync(new(".csv"))).Status);
    }
    [Fact] public async Task OversizedProviderResultIsRejected()
    {
        var port = new BrowserDocumentPort((_, _) => ValueTask.FromResult(new DocumentOperationResult(DocumentOperationStatus.Opened, new("large.csv", "text/csv", new byte[4]))), (_, _) => throw new NotImplementedException());
        Assert.Equal(DocumentOperationStatus.Failed, (await port.OpenAsync(new(".csv", 3))).Status);
    }
    [Fact] public async Task PickerNameCannotBecomeAPath()
    {
        var port = new BrowserDocumentPort((_, _) => ValueTask.FromResult(new DocumentOperationResult(DocumentOperationStatus.Opened, new("C:\\private\\file.csv", "text/csv", new byte[] { 1 }))), (_, _) => throw new NotImplementedException());
        var result = await port.OpenAsync(new(".csv"));
        Assert.Equal("file.csv", result.Document!.Name);
        Assert.Equal(new byte[] { 1 }, result.Document.Bytes.ToArray());
    }
    [Fact] public async Task ExportDoesNotUpgradeDownloadToSaved()
    {
        DocumentContent? received = null;
        var port = new BrowserDocumentPort((_, _) => throw new NotImplementedException(), (document, _) => {
            received = document; return ValueTask.FromResult(new DocumentOperationResult(DocumentOperationStatus.DownloadInitiated));
        });
        Assert.Equal(DocumentOperationStatus.DownloadInitiated, (await port.ExportAsync(new("../sheet.csv", "text/csv", new byte[] { 2 }))).Status);
        Assert.Equal("sheet.csv", received!.Name);
    }
    [Fact] public async Task CancellationPreventsCallingTransport()
    {
        var port = new BrowserDocumentPort((_, _) => throw new Exception("must not call"), (_, _) => throw new Exception("must not call"));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await port.OpenAsync(new(".csv"), new CancellationToken(true)));
    }
    [Fact] public void ReentrantWorkRunsOnNextFrame()
    {
        int requests = 0; var values = new List<int>();
        using var dispatcher = new FrameDispatcher(() => requests++);
        dispatcher.Post(() => { values.Add(1); dispatcher.Post(() => values.Add(3)); });
        dispatcher.Post(() => values.Add(2));
        Assert.Equal(1, requests);
        Assert.Equal(2, dispatcher.Drain()); Assert.Equal(new[] { 1, 2 }, values);
        Assert.Equal(2, requests); dispatcher.Drain(); Assert.Equal(new[] { 1, 2, 3 }, values);
    }
    [Fact] public void CallbackFailureDoesNotLoseRemainingWork()
    {
        int calls = 0; using var dispatcher = new FrameDispatcher(() => { });
        dispatcher.Post(() => throw new InvalidOperationException()); dispatcher.Post(() => calls++);
        Assert.Throws<InvalidOperationException>(() => dispatcher.Drain());
        dispatcher.Drain(); Assert.Equal(1, calls);
    }
    [Fact] public void DisposedDispatcherDoesNotRunPendingWork()
    {
        var dispatcher = new FrameDispatcher(() => { }); dispatcher.Post(() => throw new Exception()); dispatcher.Dispose();
        Assert.Throws<ObjectDisposedException>(() => dispatcher.Drain());
        Assert.Throws<ObjectDisposedException>(() => dispatcher.Post(() => { }));
    }
    [Fact] public void ClipboardReadsAndWritesAreScopedEvenOnFailure()
    {
        var clipboard = new TrustedClipboard();
        Assert.False(clipboard.TryGetText(out _));
        Assert.Throws<InvalidOperationException>(() => clipboard.SetText("outside"));
        Assert.Throws<InvalidOperationException>(() => clipboard.DuringEvent("secret", () => { Assert.True(clipboard.TryGetText(out _)); clipboard.SetText("out"); throw new InvalidOperationException(); }));
        Assert.False(clipboard.TryGetText(out _));
        Assert.Null(clipboard.DuringEvent(null, () => { }));
    }
    [Fact] public void EmptyClipboardTextIsStillAValidResult()
    {
        var clipboard = new TrustedClipboard();
        Assert.Equal("", clipboard.DuringEvent("", () => { Assert.True(clipboard.TryGetText(out var text)); Assert.Equal("", text); clipboard.SetText(""); }));
    }
}
