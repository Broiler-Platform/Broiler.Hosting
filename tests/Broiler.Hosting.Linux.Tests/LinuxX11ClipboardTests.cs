using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Broiler.Hosting.Linux.Tests;

/// <summary>
/// Exercises the X11 clipboard against a real X server.
///
/// Where there is no display — a bare container, a build agent — every test
/// here returns early rather than failing: reporting no clipboard is the
/// documented behavior on such a machine, not a defect. Run them under
/// `xvfb-run -a dotnet test tests/Broiler.Hosting.Linux.Tests` to see them actually talk to
/// an X server.
/// </summary>
public sealed class LinuxX11ClipboardTests
{
    [Fact]
    public void Copied_Text_Comes_Back_From_The_Selection()
    {
        using LinuxX11Clipboard? clipboard = TryOpen();
        if (clipboard is null)
            return;

        clipboard.SetText("hello from Broiler");

        Assert.True(clipboard.OwnsClipboard);
        Assert.True(clipboard.TryGetText(out string text));
        Assert.Equal("hello from Broiler", text);
    }

    /// <summary>
    /// The one that matters: a second connection is a separate X client, so this
    /// is a real paste of what Broiler copied by something that is not Broiler —
    /// the thing a private in-process string cannot do.
    /// </summary>
    [Fact]
    public void Another_Client_Pastes_What_Broiler_Copied()
    {
        using LinuxX11Clipboard? broiler = TryOpen();
        using LinuxX11Clipboard? otherApplication = TryOpen();
        if (broiler is null || otherApplication is null)
            return;

        broiler.SetText("crossed the client boundary");

        Assert.True(TryGetTextWhileServing(otherApplication, broiler, out string text));
        Assert.Equal("crossed the client boundary", text);
        Assert.False(otherApplication.OwnsClipboard);
    }

    [Fact]
    public void Text_Outside_Latin1_Survives_The_Round_Trip()
    {
        using LinuxX11Clipboard? broiler = TryOpen();
        using LinuxX11Clipboard? otherApplication = TryOpen();
        if (broiler is null || otherApplication is null)
            return;

        const string original = "grüße — naïve café 日本語 🎉";
        broiler.SetText(original);

        Assert.True(TryGetTextWhileServing(otherApplication, broiler, out string text));
        Assert.Equal(original, text);
    }

    /// <summary>
    /// A document-sized selection, to prove the transfer is not silently capped
    /// at one small property. Both ends here are this class, which answers in a
    /// single property; the chunked INCR path exists for owners that do not.
    /// </summary>
    [Fact]
    public void A_Document_Sized_Selection_Survives_The_Round_Trip()
    {
        using LinuxX11Clipboard? broiler = TryOpen();
        using LinuxX11Clipboard? otherApplication = TryOpen();
        if (broiler is null || otherApplication is null)
            return;

        string original = string.Concat(Enumerable.Repeat("0123456789abcdef", 8 * 1024));
        broiler.SetText(original);

        Assert.True(TryGetTextWhileServing(otherApplication, broiler, out string text));
        Assert.Equal(original.Length, text.Length);
        Assert.Equal(original, text);
    }

    [Fact]
    public void A_Later_Copy_By_Another_Client_Takes_Ownership_Away()
    {
        using LinuxX11Clipboard? broiler = TryOpen();
        using LinuxX11Clipboard? otherApplication = TryOpen();
        if (broiler is null || otherApplication is null)
            return;

        broiler.SetText("first");
        otherApplication.SetText("second");

        // Broiler has to notice the loss when it pumps rather than keep serving
        // a string that is no longer the clipboard's content.
        broiler.ProcessPendingEvents();

        Assert.False(broiler.OwnsClipboard);
        Assert.True(TryGetTextWhileServing(broiler, otherApplication, out string text));
        Assert.Equal("second", text);
    }

    [Fact]
    public void Releasing_The_Connection_Gives_Up_Ownership()
    {
        using LinuxX11Clipboard? observer = TryOpen();
        LinuxX11Clipboard? broiler = TryOpen();
        if (observer is null || broiler is null)
        {
            broiler?.Dispose();
            return;
        }

        broiler.SetText("goes away with the process");
        Assert.True(broiler.OwnsClipboard);

        broiler.Dispose();

        observer.ProcessPendingEvents();
        Assert.False(observer.TryGetText(out _));
    }

    [Fact]
    public void A_Disposed_Clipboard_Reports_No_Text_Instead_Of_Throwing()
    {
        LinuxX11Clipboard? clipboard = TryOpen();
        if (clipboard is null)
            return;

        clipboard.SetText("before");
        clipboard.Dispose();

        clipboard.SetText("after");
        Assert.False(clipboard.TryGetText(out _));
        Assert.False(clipboard.OwnsClipboard);
        clipboard.ProcessPendingEvents();
        clipboard.Dispose();
    }

    private static LinuxX11Clipboard? TryOpen() =>
        OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
            ? LinuxX11Clipboard.TryOpen()
            : null;

    /// <summary>
    /// Pastes on one connection while pumping the other, because an X11 paste is
    /// a conversation: the owner has to answer the request for the reader to see
    /// anything. In an application that pumping is the host loop; here it is a
    /// second thread, so the two run at once as two processes would.
    /// </summary>
    private static bool TryGetTextWhileServing(LinuxX11Clipboard reader, LinuxX11Clipboard owner, out string text)
    {
        using var stop = new CancellationTokenSource();
        Task serving = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                owner.ProcessPendingEvents();
                Thread.Sleep(1);
            }
        });

        try
        {
            return reader.TryGetText(out text);
        }
        finally
        {
            stop.Cancel();
            serving.Wait(TimeSpan.FromSeconds(5));
        }
    }
}
