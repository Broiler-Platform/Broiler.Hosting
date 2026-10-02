using System;

namespace Broiler.Hosting.Windows.Accessibility;

/// <summary>UIA text units. Values match the native <c>TextUnit</c> enumeration.</summary>
public enum TextUnit
{
    Character = 0,
    Format = 1,
    Word = 2,
    Line = 3,
    Paragraph = 4,
    Page = 5,
    Document = 6,
}

/// <summary>UIA range endpoints. Values match the native <c>TextPatternRangeEndpoint</c> enumeration.</summary>
public enum TextPatternRangeEndpoint
{
    Start = 0,
    End = 1,
}

/// <summary>Values match the native <c>SupportedTextSelection</c> enumeration.</summary>
public enum SupportedTextSelection
{
    None = 0,
    Single = 1,
    Multiple = 2,
}

/// <summary>Managed model of the UIA Text pattern, implemented by text-capable element peers.</summary>
public interface ITextProvider
{
    WindowsTextRange[] GetTextSelection();

    WindowsTextRange[] GetVisibleRanges();

    WindowsTextRange DocumentRange { get; }

    SupportedTextSelection SupportedTextSelection { get; }

    WindowsTextRange RangeFromPoint(double x, double y);
}

/// <summary>
/// A range over the plain-text value of an Edit or RichEdit, as published in its semantic text info.
/// Offsets are UTF-16 indices into that value and are clamped to the current text on every use, so a
/// range held by a client survives edits. Lines are treated as paragraphs: the semantic model has no
/// visual line breaks, so a wrapped paragraph is one line.
/// </summary>
public sealed class WindowsTextRange
{
    private int _start;
    private int _end;

    internal WindowsTextRange(WindowsElementAutomationPeer owner, int start, int end)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _start = Math.Min(start, end);
        _end = Math.Max(start, end);
    }

    public WindowsElementAutomationPeer Owner { get; }

    private string Source => Owner.TextValue ?? string.Empty;

    public int Start => Math.Clamp(_start, 0, Source.Length);

    public int End => Math.Clamp(_end, Start, Source.Length);

    public bool IsDegenerate => Start == End;

    public WindowsTextRange Clone() => new(Owner, Start, End);

    public bool Compare(WindowsTextRange other) =>
        ReferenceEquals(other.Owner, Owner) && other.Start == Start && other.End == End;

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, WindowsTextRange target, TextPatternRangeEndpoint targetEndpoint)
    {
        RequireSameOwner(target);
        return Get(endpoint) - target.Get(targetEndpoint);
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        string text = Source;
        if (text.Length == 0)
        {
            _start = _end = 0;
            return;
        }

        int position = Math.Min(Start, text.Length - 1);
        int start = TextUnitBoundaries.EnclosingStart(text, position, unit);
        _start = start;
        _end = TextUnitBoundaries.Next(text, start, unit);
    }

    public WindowsTextRange? FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text)) return null;
        string source = Source;
        int start = Start, length = End - Start;
        StringComparison comparison = ignoreCase ? StringComparison.CurrentCultureIgnoreCase : StringComparison.CurrentCulture;
        int index = backward
            ? source.LastIndexOf(text, start + length - 1 < 0 ? 0 : start + length - 1, length, comparison)
            : source.IndexOf(text, start, length, comparison);
        return index < 0 ? null : new WindowsTextRange(Owner, index, index + text.Length);
    }

    public string GetText(int maxLength)
    {
        string text = Source[Start..End];
        return maxLength >= 0 && text.Length > maxLength ? text[..maxLength] : text;
    }

    /// <summary>
    /// Moves by whole units. A degenerate range stays degenerate; otherwise the range becomes the unit
    /// at the new position. Returns the number of units actually moved (negative when backward).
    /// </summary>
    public int Move(TextUnit unit, int count)
    {
        if (count == 0) return 0;
        string text = Source;
        bool degenerate = IsDegenerate;
        int position = degenerate ? Start : TextUnitBoundaries.EnclosingStart(text, Start, unit);
        int moved = 0;
        while (moved != count)
        {
            int next = count > 0 ? TextUnitBoundaries.Next(text, position, unit) : TextUnitBoundaries.Previous(text, position, unit);
            // A non-degenerate range must land on a unit that exists, so it cannot move to the very end.
            if (next == position || (!degenerate && count > 0 && next >= text.Length)) break;
            position = next;
            moved += Math.Sign(count);
        }

        _start = position;
        _end = degenerate ? position : TextUnitBoundaries.Next(text, position, unit);
        return moved;
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        string text = Source;
        int position = Get(endpoint);
        int moved = 0;
        while (moved != count)
        {
            int next = count > 0 ? TextUnitBoundaries.Next(text, position, unit) : TextUnitBoundaries.Previous(text, position, unit);
            if (next == position) break;
            position = next;
            moved += Math.Sign(count);
        }

        Set(endpoint, position);
        return moved;
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, WindowsTextRange target, TextPatternRangeEndpoint targetEndpoint)
    {
        RequireSameOwner(target);
        Set(endpoint, target.Get(targetEndpoint));
    }

    public void Select() => Owner.SelectText(Start, End);

    private int Get(TextPatternRangeEndpoint endpoint) => endpoint == TextPatternRangeEndpoint.Start ? Start : End;

    // Moving one endpoint past the other collapses the range onto the moved endpoint, as UIA requires.
    private void Set(TextPatternRangeEndpoint endpoint, int position)
    {
        int start = Start, end = End;
        position = Math.Clamp(position, 0, Source.Length);
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            start = position;
            if (start > end) end = start;
        }
        else
        {
            end = position;
            if (end < start) start = end;
        }

        _start = start;
        _end = end;
    }

    private void RequireSameOwner(WindowsTextRange other)
    {
        if (!ReferenceEquals(other.Owner, Owner))
            throw new ArgumentException("The range belongs to a different text element.", nameof(other));
    }
}

/// <summary>Unit boundaries in a plain-text value; every unit starts at a boundary and ends at the next.</summary>
internal static class TextUnitBoundaries
{
    public static int Next(string text, int position, TextUnit unit)
    {
        if (position >= text.Length) return text.Length;
        if (unit == TextUnit.Character)
            return Math.Min(text.Length, position + (char.IsHighSurrogate(text[position]) && position + 1 < text.Length && char.IsLowSurrogate(text[position + 1]) ? 2 : 1));
        for (int index = position + 1; index < text.Length; index++)
        {
            if (IsBoundary(text, index, unit)) return index;
        }

        return text.Length;
    }

    public static int Previous(string text, int position, TextUnit unit)
    {
        if (position <= 0) return 0;
        position = Math.Min(position, text.Length);
        if (unit == TextUnit.Character)
            return Math.Max(0, position - (position >= 2 && char.IsLowSurrogate(text[position - 1]) && char.IsHighSurrogate(text[position - 2]) ? 2 : 1));
        for (int index = position - 1; index > 0; index--)
        {
            if (IsBoundary(text, index, unit)) return index;
        }

        return 0;
    }

    /// <summary>The start of the unit that contains <paramref name="position"/>.</summary>
    public static int EnclosingStart(string text, int position, TextUnit unit)
    {
        position = Math.Clamp(position, 0, text.Length);
        if (unit == TextUnit.Character)
            return position > 0 && position < text.Length && char.IsLowSurrogate(text[position]) && char.IsHighSurrogate(text[position - 1]) ? position - 1 : position;
        return IsBoundary(text, position, unit) ? position : Previous(text, position, unit);
    }

    private static bool IsBoundary(string text, int index, TextUnit unit)
    {
        if (index <= 0 || index >= text.Length) return true;
        return unit switch
        {
            // A word is a run of non-space characters plus the spaces that follow it.
            TextUnit.Word => !char.IsWhiteSpace(text[index]) && char.IsWhiteSpace(text[index - 1]),
            TextUnit.Line or TextUnit.Paragraph => text[index - 1] == '\n',
            // There is no formatting or pagination in the plain-text value.
            _ => false,
        };
    }
}
