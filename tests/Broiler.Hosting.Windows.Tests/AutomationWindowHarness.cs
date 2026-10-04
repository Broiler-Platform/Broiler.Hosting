using System;
using System.Threading;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Hosting.Windows.Accessibility;
using Broiler.Native.Windows;
using Broiler.UI;
using Broiler.UI.Panel.Standard;
using Broiler.UI.Standard;

namespace Broiler.Hosting.Windows.Tests;

/// <summary>
/// A real, hidden window on its own STA thread with a message loop, a queued dispatcher drained by a
/// posted message (as Broiler.Mail's windows do), and an automation bridge attached to it.
/// </summary>
internal sealed class AutomationWindowHarness : IDisposable
{
    private const uint DrainMessage = 0x8000 + 0x42; // WM_APP + n
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private StandardQueuedUiDispatcher _dispatcher = null!;
    private Exception? _startupError;

    public AutomationWindowHarness(Action<StandardPanel>? build = null)
    {
        _thread = new Thread(() => Run(build)) { IsBackground = true, Name = "Automation test window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The test window did not start.");
        if (_startupError is not null)
            throw new InvalidOperationException("The test window could not be created.", _startupError);
    }

    public nint Hwnd { get; private set; }

    public UiSession Session { get; private set; } = null!;

    public StandardPanel Root { get; private set; } = null!;

    public WindowsAutomationBridge Bridge { get; private set; } = null!;

    public int UiThreadId { get; private set; }

    /// <summary>Runs <paramref name="work"/> on the window thread and waits for it.</summary>
    public T Invoke<T>(Func<T> work)
    {
        using var done = new ManualResetEventSlim();
        T result = default!;
        Exception? error = null;
        _dispatcher.Post(() =>
        {
            try { result = work(); }
            catch (Exception exception) { error = exception; }
            finally { done.Set(); }
        });
        if (!done.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The window thread did not run the work.");
        if (error is not null) throw new InvalidOperationException("The work failed on the window thread.", error);
        return result;
    }

    /// <summary>Destroys the window from its own thread, as closing it would, and ends the message loop.</summary>
    public void DestroyWindow() => Invoke(() =>
    {
        WindowNative.DestroyWindow(Hwnd);
        WindowNative.PostQuitMessage(0);
        return true;
    });

    private void Run(Action<StandardPanel>? build)
    {
        try
        {
            UiThreadId = Environment.CurrentManagedThreadId;
            Hwnd = WindowNative.CreateWindowEx(0, "STATIC", "Automation test window", 0, 0, 0, 400, 300, 0, 0, WindowNative.GetModuleHandle(null), 0);
            if (Hwnd == 0) throw new InvalidOperationException("CreateWindowEx failed.");
            nint hwnd = Hwnd;
            _dispatcher = new StandardQueuedUiDispatcher(() => WindowNative.PostMessage(hwnd, DrainMessage, 0, 0));
            Session = new StandardUiSessionBuilder().WithDispatcher(_dispatcher).Build(new Host());
            Root = new StandardPanel();
            Session.AddRoot(Root);
            build?.Invoke(Root);
            Root.Arrange(new BRect(0, 0, 400, 300));
            Bridge = new WindowsAutomationBridge(Hwnd, Session, Root);
        }
        catch (Exception error)
        {
            _startupError = error;
            _ready.Set();
            return;
        }

        _ready.Set();
        while (WindowNative.GetMessage(out WindowNative.MSG message, 0, 0, 0) > 0)
        {
            if (message.Message == DrainMessage)
            {
                _dispatcher.Drain();
                continue;
            }
            WindowNative.TranslateMessage(ref message);
            WindowNative.DispatchMessage(ref message);
        }

        Bridge.Dispose();
        Session.Dispose();
    }

    public void Dispose()
    {
        if (_thread.IsAlive && HwndNative.IsWindow(Hwnd))
            DestroyWindow();
        _thread.Join(TimeSpan.FromSeconds(10));
        _ready.Dispose();
    }

    private sealed class Host : IUiHost
    {
        public BSize ViewportSize => new(400, 300);
        public double Scale => 1.0;
        public BRenderList CreateRenderList(int capacity = 0) => new();
        public void Invalidate(UiInvalidation invalidation) { }
        public void Present(BRenderList renderList) { }
    }
}
