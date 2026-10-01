using Android.OS;
using Android.Views;
using Android.Views.InputMethods;
using Java.Lang;

namespace Broiler.Hosting.Android;

internal sealed class BroilerInputConnection : BaseInputConnection
{
    private readonly AndroidInputCoordinator _input;

    public BroilerInputConnection(View targetView, AndroidInputCoordinator input)
        : base(targetView, true)
    {
        _input = input;
    }

    public override bool CommitText(ICharSequence? text, int newCursorPosition) =>
        _input.TextDevice.CommitText(text?.ToString() ?? string.Empty, newCursorPosition, SystemClock.UptimeMillis());

    public override bool SetComposingText(ICharSequence? text, int newCursorPosition) =>
        _input.TextDevice.SetComposingText(text?.ToString() ?? string.Empty, newCursorPosition, SystemClock.UptimeMillis());

    public override bool FinishComposingText() =>
        _input.TextDevice.FinishComposingText(SystemClock.UptimeMillis()) || base.FinishComposingText();

    public override bool DeleteSurroundingText(int beforeLength, int afterLength) =>
        _input.TextDevice.DeleteSurroundingText(beforeLength, afterLength);

    public override bool SetSelection(int start, int end) => _input.TextDevice.SetSelection(start, end);

    public override bool SetComposingRegion(int start, int end) => _input.TextDevice.SetComposingRegion(start, end);

    public override bool PerformEditorAction(ImeAction editorAction) =>
        _input.TextDevice.PerformEditorAction((int)editorAction);

    public override ICharSequence? GetTextBeforeCursorFormatted(int length, GetTextFlags flags) =>
        new Java.Lang.String(_input.TextDevice.GetTextBeforeCursor(length));

    public override ICharSequence? GetTextAfterCursorFormatted(int length, GetTextFlags flags) =>
        new Java.Lang.String(_input.TextDevice.GetTextAfterCursor(length));

    public override ICharSequence? GetSelectedTextFormatted(GetTextFlags flags) =>
        new Java.Lang.String(_input.TextDevice.GetSelectedText());
}
