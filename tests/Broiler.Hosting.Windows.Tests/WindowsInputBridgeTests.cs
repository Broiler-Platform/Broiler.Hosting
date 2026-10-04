using System;
using System.Collections.Generic;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.RenderList;
using Broiler.Hosting.Windows.Input;
using Broiler.Input;
using Broiler.Input.Mouse;
using Broiler.Input.Text;
using Broiler.Native.Windows;
using Broiler.Native.Windows.Input;
using Broiler.UI;
using Broiler.UI.Edit.Standard;
using Broiler.UI.Panel.Standard;
using Broiler.UI.RichEdit.Standard;
using Broiler.UI.Standard;
using Xunit;

namespace Broiler.Hosting.Windows.Tests;

public sealed class WindowsInputBridgeTests
{
    private sealed class HeadlessUiHost : IUiHost
    {
        public BSize ViewportSize => new(800, 600);
        public double Scale => 1.0;
        public BRenderList CreateRenderList(int capacity = 0) => new();
        public void Invalidate(UiInvalidation invalidation) { }
        public void Present(BRenderList renderList) { }
    }

    private static (UiSession Session, WindowsInputBridge Bridge, List<UiInputEvent> Events) CreateTestHarness(
        nint topLevelHwnd = 0,
        nint renderHwnd = 0)
    {
        var host = new HeadlessUiHost();
        var dispatcher = new ImmediateUiDispatcher();
        var session = new StandardUiSessionBuilder().WithDispatcher(dispatcher).Build(host);
        var root = new StandardPanel();
        root.Arrange(new BRect(0, 0, 800, 600));
        session.AddRoot(root);

        var events = new List<UiInputEvent>();
        var bridge = new WindowsInputBridge(topLevelHwnd, renderHwnd, session, null, () => 1.0, null);
        bridge.EventDispatched += ev => events.Add(ev);

        return (session, bridge, events);
    }

    [Fact]
    public void TopLevelActivation_TransfersFocus_ToRenderChildHandle()
    {
        nint topLevel = 0x1000;
        nint renderChild = 0x2000;
        var (_, bridge, _) = CreateTestHarness(topLevel, renderChild);

        nint focusedHandle = 0;
        bridge.SetFocusAction = hwnd => focusedHandle = hwnd;

        // 1. WM_SETFOCUS on top-level moves focus to render child
        bridge.OnTopLevelMessage(WindowNative.WmSetFocus, 0, 0);
        Assert.Equal(renderChild, focusedHandle);

        focusedHandle = 0;

        // 2. WM_ACTIVATE (WA_ACTIVE = 1) moves focus to render child
        bridge.OnTopLevelMessage(WindowNative.WmActivate, (nint)WindowNative.WaActive, 0);
        Assert.Equal(renderChild, focusedHandle);

        focusedHandle = 0;

        // 3. WM_ACTIVATE (WA_CLICKACTIVE = 2) moves focus to render child
        bridge.OnTopLevelMessage(WindowNative.WmActivate, (nint)WindowNative.WaClickActive, 0);
        Assert.Equal(renderChild, focusedHandle);

        focusedHandle = 0;

        // 4. WM_ACTIVATE (WA_INACTIVE = 0) does NOT transfer focus
        bridge.OnTopLevelMessage(WindowNative.WmActivate, (nint)WindowNative.WaInactive, 0);
        Assert.Equal(0, focusedHandle);
    }

    [Fact]
    public void SurrogatePairs_AreAssembled_IntoExactlyOneTextEvent()
    {
        var (_, bridge, events) = CreateTestHarness();

        // High surrogate U+D83D (part of 😀 U+1F600)
        char high = '\uD83D';
        char low = '\uDE00';

        bridge.ProcessChar(high);
        Assert.Equal(high, bridge.PendingHighSurrogate);
        Assert.Empty(events);

        // Low surrogate U+DE00 completes the pair
        bridge.ProcessChar(low);
        Assert.Equal('\0', bridge.PendingHighSurrogate);
        Assert.Single(events);

        UiInputEvent ev = events[0];
        Assert.Equal(UiInputEventKind.TextInput, ev.Kind);
        Assert.Equal("😀", ev.Text);
    }

    [Fact]
    public void StrayLowSurrogate_IsDiscardedWithoutEvent()
    {
        var (_, bridge, events) = CreateTestHarness();

        // Low surrogate without prior high surrogate
        bridge.ProcessChar('\uDE00');
        Assert.Empty(events);
        Assert.Equal('\0', bridge.PendingHighSurrogate);
    }

    [Fact]
    public void OrphanedHighSurrogate_IsCleanedUpOnNonSurrogateOrFocusLoss()
    {
        var (_, bridge, events) = CreateTestHarness();

        // High surrogate followed by normal character
        bridge.ProcessChar('\uD83D');
        Assert.Equal('\uD83D', bridge.PendingHighSurrogate);

        bridge.ProcessChar('a');
        Assert.Equal('\0', bridge.PendingHighSurrogate);
        Assert.Single(events);
        Assert.Equal("a", events[0].Text);

        events.Clear();

        // High surrogate followed by WM_KILLFOCUS
        bridge.ProcessChar('\uD83D');
        Assert.Equal('\uD83D', bridge.PendingHighSurrogate);
        bridge.ProcessNativeMessage(0, WindowNative.WmKillFocus, 0, 0);
        Assert.Equal('\0', bridge.PendingHighSurrogate);
        Assert.Empty(events);
    }

    [Fact]
    public void ShortcutControlChords_DoNotProduceText()
    {
        var (_, bridge, events) = CreateTestHarness();

        // Simulate Ctrl down, Alt up
        bridge.KeyStateProvider = vk => vk == WindowNative.VkControl ? unchecked((short)0x8000) : (short)0;

        // Ctrl+C produces ASCII 0x03 (ETX)
        bridge.ProcessChar((char)0x03);
        // Ctrl+A produces ASCII 0x01 (SOH)
        bridge.ProcessChar((char)0x01);
        // Ctrl+V produces ASCII 0x16 (SYN)
        bridge.ProcessChar((char)0x16);
        // Ctrl+Z produces ASCII 0x1A (SUB)
        bridge.ProcessChar((char)0x1A);
        // Ctrl+Backspace produces ASCII 0x7F (DEL)
        bridge.ProcessChar((char)0x7F);
        // Ctrl+Enter produces ASCII 0x0A (LF)
        bridge.ProcessChar((char)0x0A);

        Assert.Empty(events);
    }

    [Fact]
    public void AltGr_GraphicCharacters_AreDeliveredAsText()
    {
        var (_, bridge, events) = CreateTestHarness();

        // AltGr simulates both Ctrl and Alt down in Windows
        bridge.KeyStateProvider = vk =>
            (vk == WindowNative.VkControl || vk == WindowNative.VkMenu)
                ? unchecked((short)0x8000)
                : (short)0;

        // AltGr+Q on German keyboard produces '@'
        bridge.ProcessChar('@');
        // AltGr+E on European keyboards produces '€'
        bridge.ProcessChar('€');
        // AltGr+7 produces '{'
        bridge.ProcessChar('{');
        // AltGr+0 produces '}'
        bridge.ProcessChar('}');
        // AltGr+\ produces '\'
        bridge.ProcessChar('\\');

        Assert.Equal(5, events.Count);
        Assert.Equal("@", events[0].Text);
        Assert.Equal("€", events[1].Text);
        Assert.Equal("{", events[2].Text);
        Assert.Equal("}", events[3].Text);
        Assert.Equal("\\", events[4].Text);
    }

    // The context code of a key message: bit 29 is set while Alt is down.
    private const nint AltDownContext = 0x20000000;

    [Fact]
    public void AltCharacters_AreNotTyped()
    {
        var (_, bridge, events) = CreateTestHarness();
        bridge.KeyStateProvider = vk => vk == WindowNative.VkMenu ? unchecked((short)0x8000) : (short)0;

        // Alt+F (a menu key) and Alt+Space (the window menu)
        bridge.ProcessNativeMessage(0, WindowNative.WmSysChar, 'f', AltDownContext);
        bridge.ProcessNativeMessage(0, WindowNative.WmSysChar, ' ', AltDownContext);

        Assert.Empty(events);
    }

    [Fact]
    public void AltCharacters_GoOnToTheWindowProcedure()
    {
        using var window = new ProbeWindow(WindowNative.WmSysChar, WindowNative.WmSysDeadChar);
        var (_, bridge, events) = CreateTestHarness(0, window.Handle);
        using var attached = bridge;
        bridge.KeyStateProvider = vk => vk == WindowNative.VkMenu ? unchecked((short)0x8000) : (short)0;

        window.Send(WindowNative.WmSysChar, 'f', AltDownContext);
        window.Send(WindowNative.WmSysChar, ' ', AltDownContext);
        window.Send(WindowNative.WmSysDeadChar, '^', AltDownContext);

        // DefWindowProc turns them into SC_KEYMENU: Alt+Space opens the window menu.
        Assert.Equal(
            new[] { (WindowNative.WmSysChar, (nint)'f'), (WindowNative.WmSysChar, (nint)' '), (WindowNative.WmSysDeadChar, (nint)'^') },
            window.Received.ConvertAll(received => (received.Message, received.WParam)));
        Assert.Empty(events);
        Assert.True(bridge.DeadKeyActive);
    }

    [Fact]
    public void AltGrAndAltNumpadCharacters_ArriveAsWmChar_AndAreTyped()
    {
        var (_, bridge, events) = CreateTestHarness();

        // AltGr+Q on a German keyboard: Windows reports Ctrl and Alt down.
        bridge.KeyStateProvider = vk => vk == WindowNative.VkControl || vk == WindowNative.VkMenu ? unchecked((short)0x8000) : (short)0;
        bridge.ProcessNativeMessage(0, WindowNative.WmChar, '@', AltDownContext);

        // Alt+0233 on the numeric keypad: the character comes as WM_CHAR, here with Alt still reported down.
        bridge.KeyStateProvider = vk => vk == WindowNative.VkMenu ? unchecked((short)0x8000) : (short)0;
        bridge.ProcessNativeMessage(0, WindowNative.WmChar, 'é', AltDownContext);

        Assert.Equal(new[] { "@", "é" }, events.ConvertAll(ev => ev.Text));
    }

    [Fact]
    public void DeadKeys_AreTrackedAndCompositeCharactersDelivered()
    {
        var (_, bridge, events) = CreateTestHarness();

        bridge.ProcessNativeMessage(0, WindowNative.WmDeadChar, '^', 0);
        Assert.True(bridge.DeadKeyActive);
        Assert.Empty(events);

        // Next character combined: 'ê'
        bridge.ProcessChar('ê');
        Assert.False(bridge.DeadKeyActive);
        Assert.Single(events);
        Assert.Equal("ê", events[0].Text);
    }

    [Fact]
    public void ImeComposition_LifecycleAndDuplicateSuppression_MaintainsExactlyOnceDelivery()
    {
        var (_, bridge, events) = CreateTestHarness();

        // 1. WM_IME_STARTCOMPOSITION
        bridge.ProcessNativeMessage(0, ImmNative.WM_IME_STARTCOMPOSITION, 0, 0);
        Assert.True(bridge.IsComposing);
        Assert.Single(events);
        Assert.Equal(UiInputEventKind.TextComposition, events[0].Kind);
        Assert.Equal(TextCompositionState.Started, events[0].CompositionState);

        events.Clear();

        // 2. WM_IME_COMPOSITION (GCS_COMPSTR)
        bridge.CompositionStringProvider = (hwnd, idx) => idx == ImmNative.GCS_COMPSTR ? "nihon" : "";
        bridge.ProcessNativeMessage(0, ImmNative.WM_IME_COMPOSITION, 0, (nint)ImmNative.GCS_COMPSTR);
        Assert.True(bridge.IsComposing);
        Assert.Single(events);
        Assert.Equal("nihon", events[0].Text);
        Assert.Equal(TextCompositionState.Updated, events[0].CompositionState);

        events.Clear();

        // 3. WM_IME_COMPOSITION (GCS_RESULTSTR commit)
        bridge.CompositionStringProvider = (hwnd, idx) => idx == ImmNative.GCS_RESULTSTR ? "日本" : "";
        bridge.ProcessNativeMessage(0, ImmNative.WM_IME_COMPOSITION, 0, (nint)ImmNative.GCS_RESULTSTR);
        Assert.False(bridge.IsComposing);
        Assert.Single(events);
        Assert.Equal("日本", events[0].Text);
        Assert.Equal(TextCompositionState.Committed, events[0].CompositionState);

        events.Clear();

        // 4. Windows DefWindowProc subsequently pumps synthetic WM_CHAR for '日' then '本'.
        // These MUST be suppressed to prevent duplicate text!
        bridge.ProcessChar('日');
        bridge.ProcessChar('本');
        Assert.Empty(events); // Exactly once: no duplicate insertions!

        // 5. Subsequent regular typing arrives normally
        bridge.ProcessChar('!');
        Assert.Single(events);
        Assert.Equal("!", events[0].Text);
    }

    [Fact]
    public void ImeCommit_CopiesAreSuppressed_EvenWhenTheCommitDispatchIsSlow()
    {
        var (_, bridge, events) = CreateTestHarness();
        var clock = new ManualClock();
        bridge.Clock = clock;
        // The first insertion into a cold editor (JIT, first layout) can take longer than the window.
        bridge.EventDispatched += ev =>
        {
            if (ev.CompositionState == TextCompositionState.Committed)
                clock.Advance(TimeSpan.FromSeconds(2));
        };

        Commit(bridge, "日本");
        events.Clear();
        bridge.ProcessChar('日');
        bridge.ProcessChar('本');

        Assert.Empty(events);
    }

    [Fact]
    public void ImeCommit_AnotherCharacterEndsTheSuppression()
    {
        var (_, bridge, events) = CreateTestHarness();
        bridge.Clock = new ManualClock();

        Commit(bridge, "日本");
        events.Clear();
        bridge.ProcessChar('日');   // the first copy
        bridge.ProcessChar('x');    // typed: the second copy is not coming
        bridge.ProcessChar('本');   // typed by the user, not a late copy

        Assert.Equal(new[] { "x", "本" }, events.ConvertAll(ev => ev.Text));
    }

    [Fact]
    public void ImeCommit_CopiesAfterTheBackstopOrANewCompositionAreTyped()
    {
        var (_, bridge, events) = CreateTestHarness();
        var clock = new ManualClock();
        bridge.Clock = clock;

        Commit(bridge, "日");
        clock.Advance(TimeSpan.FromMilliseconds(600));
        events.Clear();
        bridge.ProcessChar('日');
        Assert.Equal("日", Assert.Single(events).Text);

        Commit(bridge, "本");
        bridge.ProcessNativeMessage(0, ImmNative.WM_IME_STARTCOMPOSITION, 0, 0);
        events.Clear();
        bridge.ProcessChar('本');
        Assert.Equal("本", Assert.Single(events).Text);
    }

    private static void Commit(WindowsInputBridge bridge, string text)
    {
        bridge.ProcessNativeMessage(0, ImmNative.WM_IME_STARTCOMPOSITION, 0, 0);
        bridge.CompositionStringProvider = (_, idx) => idx == ImmNative.GCS_RESULTSTR ? text : "";
        bridge.ProcessNativeMessage(0, ImmNative.WM_IME_COMPOSITION, 0, (nint)ImmNative.GCS_RESULTSTR);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    [Fact]
    public void ImeComposition_CancelledOnKillFocus()
    {
        var (_, bridge, events) = CreateTestHarness();

        bridge.ProcessNativeMessage(0, ImmNative.WM_IME_STARTCOMPOSITION, 0, 0);
        Assert.True(bridge.IsComposing);
        events.Clear();

        bridge.ProcessNativeMessage(0, WindowNative.WmKillFocus, 0, 0);
        Assert.False(bridge.IsComposing);
        Assert.Single(events);
        Assert.Equal(TextCompositionState.Cancelled, events[0].CompositionState);
    }

    // WM_IME_SETCONTEXT's display options as Windows sends them: ISC_SHOWUICOMPOSITIONWINDOW (0x80000000),
    // ISC_SHOWUIGUIDELINE (0x40000000), and ISC_SHOWUIALLCANDIDATEWINDOW (0xF).
    private const long ShowEveryImeWindow = 0xC000000F;
    private const long ShowGuideAndCandidates = 0x4000000F;
    private static readonly nint EveryImeWindow = unchecked((nint)ShowEveryImeWindow);

    private static long DisplayOptions(nint lParam) => lParam.ToInt64() & 0xFFFFFFFF;

    [Fact]
    public void InlineComposition_HidesTheImeCompositionWindow_AndKeepsItsCandidates()
    {
        using var window = new ProbeWindow(ImmNative.WM_IME_SETCONTEXT);
        var (_, bridge, _) = CreateTestHarness(0, window.Handle);
        using var attached = bridge;
        Assert.True(bridge.DrawsCompositionInline);

        window.Send(ImmNative.WM_IME_SETCONTEXT, 1, EveryImeWindow);
        window.Send(ImmNative.WM_IME_SETCONTEXT, 0, EveryImeWindow);
        bridge.DrawsCompositionInline = false;
        window.Send(ImmNative.WM_IME_SETCONTEXT, 1, EveryImeWindow);

        Assert.Equal(new nint[] { 1, 0, 1 }, window.Received.ConvertAll(received => received.WParam));
        Assert.Equal(
            new[] { ShowGuideAndCandidates, ShowGuideAndCandidates, ShowEveryImeWindow },
            window.Received.ConvertAll(received => DisplayOptions(received.LParam)));
    }

    [Fact]
    public void InlineComposition_IsKeptFromTheImeWindow_AndItsCommitIsTypedOnce()
    {
        using var window = new ProbeWindow(ImmNative.WM_IME_STARTCOMPOSITION, ImmNative.WM_IME_COMPOSITION, ImmNative.WM_IME_ENDCOMPOSITION);
        var (session, bridge, events) = CreateTestHarness(0, window.Handle);
        using var attached = bridge;
        var edit = FocusedEdit(session);
        var ime = new ImeStrings(bridge);

        window.Send(ImmNative.WM_IME_STARTCOMPOSITION, 0, 0);
        ime.Composition = "にほん";
        window.Send(ImmNative.WM_IME_COMPOSITION, 0, (nint)ImmNative.GCS_COMPSTR);
        ime.Composition = string.Empty;
        ime.Result = "日本";
        window.Send(ImmNative.WM_IME_COMPOSITION, 0, (nint)ImmNative.GCS_RESULTSTR);
        window.Send(ImmNative.WM_IME_ENDCOMPOSITION, 0, 0);
        // DefWindowProc never saw the commit, so no WM_CHAR copies follow: the next character is the user's.
        window.Send(WindowNative.WmChar, '!', 0);

        Assert.Equal("日本!", edit.Text);
        Assert.Equal(
            new TextCompositionState?[] { TextCompositionState.Started, TextCompositionState.Updated, TextCompositionState.Committed },
            events.FindAll(ev => ev.Kind == UiInputEventKind.TextComposition).ConvertAll(ev => ev.CompositionState));
        // Only the end of the composition goes on: the IME's window gets nothing to draw and no commit to copy.
        Assert.Equal(new[] { ImmNative.WM_IME_ENDCOMPOSITION }, window.Received.ConvertAll(received => received.Message));
    }

    [Fact]
    public void InlineComposition_StillSuppressesCopiesOfItsCommit()
    {
        // A host that passes the commit on itself gets the copies; they are not typed twice.
        using var window = new ProbeWindow(ImmNative.WM_IME_COMPOSITION);
        var (session, bridge, _) = CreateTestHarness(0, window.Handle);
        using var attached = bridge;
        var edit = FocusedEdit(session);
        _ = new ImeStrings(bridge) { Result = "日本" };

        window.Send(ImmNative.WM_IME_STARTCOMPOSITION, 0, 0);
        window.Send(ImmNative.WM_IME_COMPOSITION, 0, (nint)ImmNative.GCS_RESULTSTR);
        window.Send(WindowNative.WmChar, '日', 1);
        window.Send(WindowNative.WmChar, '本', 1);
        window.Send(WindowNative.WmChar, '!', 1);

        Assert.Empty(window.Received);
        Assert.Equal("日本!", edit.Text);
    }

    [Fact]
    public void InlineComposition_PassesOnAResultItCouldNotRead()
    {
        using var window = new ProbeWindow(ImmNative.WM_IME_COMPOSITION);
        var (session, bridge, events) = CreateTestHarness(0, window.Handle);
        using var attached = bridge;
        var edit = FocusedEdit(session);
        _ = new ImeStrings(bridge);

        window.Send(ImmNative.WM_IME_STARTCOMPOSITION, 0, 0);
        window.Send(ImmNative.WM_IME_COMPOSITION, 0, (nint)ImmNative.GCS_RESULTSTR);
        // DefWindowProc reads the result itself and delivers it as WM_IME_CHAR, then WM_CHAR.
        window.Send(WindowNative.WmChar, '日', 1);

        Assert.Equal((nint)ImmNative.GCS_RESULTSTR, Assert.Single(window.Received).LParam);
        Assert.DoesNotContain(events, ev => ev.CompositionState == TextCompositionState.Committed);
        Assert.Equal("日", edit.Text);
    }

    [Fact]
    public void CompositionDrawnByTheIme_IsPassedOnWhole_AndItsCommitIsTypedOnce()
    {
        using var window = new ProbeWindow(ImmNative.WM_IME_STARTCOMPOSITION, ImmNative.WM_IME_COMPOSITION, ImmNative.WM_IME_ENDCOMPOSITION);
        var (session, bridge, _) = CreateTestHarness(0, window.Handle);
        using var attached = bridge;
        bridge.DrawsCompositionInline = false;
        var edit = FocusedEdit(session);
        var ime = new ImeStrings(bridge);

        window.Send(ImmNative.WM_IME_STARTCOMPOSITION, 0, 0);
        ime.Composition = "にほん";
        window.Send(ImmNative.WM_IME_COMPOSITION, 0, (nint)ImmNative.GCS_COMPSTR);
        ime.Composition = string.Empty;
        ime.Result = "日本";
        window.Send(ImmNative.WM_IME_COMPOSITION, 0, (nint)ImmNative.GCS_RESULTSTR);
        // DefWindowProc copies the commit as WM_CHAR.
        window.Send(WindowNative.WmChar, '日', 1);
        window.Send(WindowNative.WmChar, '本', 1);
        window.Send(ImmNative.WM_IME_ENDCOMPOSITION, 0, 0);

        Assert.Equal("日本", edit.Text);
        Assert.Equal(
            new[] { ImmNative.WM_IME_STARTCOMPOSITION, ImmNative.WM_IME_COMPOSITION, ImmNative.WM_IME_COMPOSITION, ImmNative.WM_IME_ENDCOMPOSITION },
            window.Received.ConvertAll(received => received.Message));
    }

    [Fact]
    public void InlineComposition_TellsTheDefaultImeWindowNotToShowTheComposition()
    {
        // End to end: DefWindowProc hands WM_IME_SETCONTEXT to the thread's default IME window, which shows
        // the IME's own composition, candidate, and guide windows as its display options say.
        using var window = new ProbeWindow(ImmNative.WM_IME_SETCONTEXT);
        window.PassedOn.Add(ImmNative.WM_IME_SETCONTEXT);
        nint imeWindow = ImeWindowRecorder.DefaultImeWindow(window.Handle);
        if (imeWindow == 0)
        {
            // Only a system without IME support has no default IME window.
            Assert.Equal(0, WindowNative.GetSystemMetrics(ImeWindowRecorder.SmImmEnabled));
            return;
        }

        var (_, bridge, _) = CreateTestHarness(0, window.Handle);
        using var attached = bridge;
        using var ime = new ImeWindowRecorder(imeWindow, ImmNative.WM_IME_SETCONTEXT);

        window.Send(ImmNative.WM_IME_SETCONTEXT, 1, EveryImeWindow);
        bridge.DrawsCompositionInline = false;
        window.Send(ImmNative.WM_IME_SETCONTEXT, 1, EveryImeWindow);
        var received = ime.Received.ConvertAll(message => (message.WParam, DisplayOptions(message.LParam)));
        window.Send(ImmNative.WM_IME_SETCONTEXT, 0, EveryImeWindow);

        Assert.Equal(new[] { ((nint)1, ShowGuideAndCandidates), ((nint)1, ShowEveryImeWindow) }, received);
    }

    private static StandardEdit FocusedEdit(UiSession session)
    {
        var edit = new StandardEdit();
        edit.Arrange(new BRect(0, 0, 300, 30));
        session.AddRoot(edit);
        session.SetFocus(edit);
        return edit;
    }

    /// <summary>The strings an injected IME reports, read when a composition message arrives.</summary>
    private sealed class ImeStrings
    {
        public ImeStrings(WindowsInputBridge bridge) =>
            bridge.CompositionStringProvider = (_, index) =>
                index == ImmNative.GCS_RESULTSTR ? Result : index == ImmNative.GCS_COMPSTR ? Composition : string.Empty;

        public string Composition { get; set; } = string.Empty;
        public string Result { get; set; } = string.Empty;
    }

    [Fact]
    public void PrecisionMouseWheel_MaintainsSubNotchPrecisionAndAxis()
    {
        var (_, bridge, events) = CreateTestHarness();

        // Standard vertical notch (120)
        nint vWParam = (nint)(120 << 16);
        bridge.ProcessNativeMessage(0, WindowNative.WmMouseWheel, vWParam, 0);
        Assert.Single(events);
        Assert.Equal(UiInputEventKind.PointerWheel, events[0].Kind);
        Assert.Equal(MouseWheelAxis.Vertical, events[0].WheelAxis);
        Assert.Equal(1.0, events[0].WheelDeltaNotches);

        events.Clear();

        // High-precision vertical delta (30 = 0.25 notch)
        nint precisionWParam = (nint)(30 << 16);
        bridge.ProcessNativeMessage(0, WindowNative.WmMouseWheel, precisionWParam, 0);
        Assert.Single(events);
        Assert.Equal(MouseWheelAxis.Vertical, events[0].WheelAxis);
        Assert.Equal(0.25, events[0].WheelDeltaNotches);

        events.Clear();

        // Horizontal tilt wheel (WM_MOUSEHWHEEL, 120 = 1.0 notch)
        nint hWParam = (nint)(120 << 16);
        bridge.ProcessNativeMessage(0, WindowNative.WmMouseHWheel, hWParam, 0);
        Assert.Single(events);
        Assert.Equal(MouseWheelAxis.Horizontal, events[0].WheelAxis);
        Assert.Equal(1.0, events[0].WheelDeltaNotches);

        events.Clear();

        // Shift + Vertical Wheel converts to Horizontal scroll
        bridge.KeyStateProvider = vk => vk == WindowNative.VkShift ? unchecked((short)0x8000) : (short)0;
        bridge.ProcessNativeMessage(0, WindowNative.WmMouseWheel, vWParam, 0);
        Assert.Single(events);
        Assert.Equal(MouseWheelAxis.Horizontal, events[0].WheelAxis);
        Assert.Equal(1.0, events[0].WheelDeltaNotches);
    }

    [Fact]
    public void EndToEnd_FocusedEditor_ReceivesCleanGraphemesAndIgnoresControlChords()
    {
        var (session, bridge, _) = CreateTestHarness();
        var edit = new StandardEdit();
        edit.Arrange(new BRect(0, 0, 300, 30));
        session.AddRoot(edit);
        session.SetFocus(edit);

        // 1. Type "Hello"
        foreach (char ch in "Hello")
        {
            bridge.ProcessChar(ch);
        }
        Assert.Equal("Hello", edit.Text);

        // 2. Simulate Ctrl+A and Ctrl+C chords
        bridge.KeyStateProvider = vk => vk == WindowNative.VkControl ? unchecked((short)0x8000) : (short)0;
        bridge.ProcessChar((char)0x01); // Ctrl+A
        bridge.ProcessChar((char)0x03); // Ctrl+C
        Assert.Equal("Hello", edit.Text);

        // 3. Type emoji surrogate pair
        bridge.KeyStateProvider = _ => 0;
        bridge.ProcessChar('\uD83D');
        bridge.ProcessChar('\uDE00');
        Assert.Equal("Hello😀", edit.Text);
    }

    [Fact]
    public void EndToEnd_RichEdit_HandlesImeCompositionAndCommit()
    {
        var (session, bridge, _) = CreateTestHarness();
        var richEdit = new StandardRichEdit();
        richEdit.Arrange(new BRect(0, 0, 400, 300));
        session.AddRoot(richEdit);
        session.SetFocus(richEdit);

        // Start composition
        bridge.ProcessNativeMessage(0, ImmNative.WM_IME_STARTCOMPOSITION, 0, 0);

        // Update composition
        bridge.CompositionStringProvider = (_, idx) => idx == ImmNative.GCS_COMPSTR ? "nihon" : "";
        bridge.ProcessNativeMessage(0, ImmNative.WM_IME_COMPOSITION, 0, (nint)ImmNative.GCS_COMPSTR);

        // Commit composition
        bridge.CompositionStringProvider = (_, idx) => idx == ImmNative.GCS_RESULTSTR ? "日本" : "";
        bridge.ProcessNativeMessage(0, ImmNative.WM_IME_COMPOSITION, 0, (nint)ImmNative.GCS_RESULTSTR);

        // Duplicate WM_CHAR suppression
        bridge.ProcessChar('日');
        bridge.ProcessChar('本');

        // Verify committed text in rich edit
        Assert.Equal("日本", richEdit.GetPlainText());
    }
}
