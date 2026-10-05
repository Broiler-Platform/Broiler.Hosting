# Open follow-ups, 5 October 2026

Hosting items left open by the 4–5 October 2026 round that produced the 0.1.0-preview.7 release
candidate (`claude/roadmap-integration` at `c388a66`; see the
[release notes](release-notes-0.1.0-preview.7.md)). Each Hosting item was checked against the code
at `c388a66`, and each Broiler.UI item against Broiler.UI `fd7657f`, the revision this release
candidate was verified with. Line numbers are approximate and refer to those revisions.

The items are grouped by area. "Decision" marks an item that needs the owner's choice before any
code is written.

## Release

### 1. The release candidate depends on an unpublished Broiler.UI

`Directory.Packages.props` sets `BroilerUiVersion` to 0.1.0-preview.18, which is not on nuget.org.
`NuGet.config` lists only nuget.org, so a plain restore and `ci.yml` fail until it is published.
Everything was verified with `-p:BroilerUiVersion=0.1.0-preview.18-local.7` and the local feed
`D:\local-packages\roadmap-2026-10-04`.

Action: follow the [release steps](release-notes-0.1.0-preview.7.md#release-steps). Broiler.UI must
be cut at or after `fd7657f`.

## Input and IME

### 2. A Windows `IUiTextInputHost` (per-focus IME placement and enable/disable)

`DrawsCompositionInline` is window-wide. Hosting does not know which element has the focus or
whether it draws a composition, so each application places the IME and turns it off itself.
`Broiler.Hosting.Android` implements `IUiTextInputHost` (`AndroidUiHost`); `Broiler.Hosting.Windows`
does not. Today Broiler.Mail's `WindowsTextInput` and Broiler.Code's `WindowsTextInputService` each
carry their own version, and Broiler.Code also has no `WM_IME_SETCONTEXT` handling.

A Windows text input host would:

- place the composition and candidate windows in physical pixels
  (`ImmSetCompositionWindow` with `CFS_POINT`, `ImmSetCandidateWindow` with `CFS_EXCLUDE`);
- turn the IME off (`ImmAssociateContextEx` with a null context) while the focus owner draws no
  composition, and back on for one that does, deciding when the focus moves. That also closes a small
  gap recorded for Broiler.Mail's password rule, which acts when the field publishes its caret on the
  next paint: a key already queued behind Tab can reach the IME before then;
- optionally answer `WM_IME_REQUEST` (0x0288) `IMR_QUERYCHARPOSITION` (0x0006) with the caret
  rectangle; neither is handled today.

Broiler.Mail's rule names `StandardRichEdit` concretely. A Broiler.UI flag saying whether a control
draws the IME composition inline would let the host apply the rule to any control.

### 3. Alt chords that the application handles still reach `DefWindowProc` and beep

`WM_SYSCHAR` now goes on to `DefWindowProc` (`WindowsInputBridge.cs`, about line 244), which turns it
into `SC_KEYMENU`; for a letter no menu takes, Windows beeps. That also happens for an Alt+letter the
application already handled as a shortcut on key down, because nothing marks it handled. The test
added in `b8b9f64` shows `DefWindowProc` sending `WM_SYSCOMMAND` `SC_KEYMENU` for 'f' and ' '.

Options: a `SysCharHandler`-style hook, or tracking whether the session consumed the preceding
`WM_SYSKEYDOWN` and swallowing the matching `WM_SYSCHAR`, ideally fed from Graphics' key path. Until
then, consumers should not add Alt+letter shortcuts.

### 4. A neutral Windows input source for consumers

Broiler.Input 0.1.0-preview.5 ships Windows message translators
(`Broiler.Input.Mouse.Windows` `WindowsMouseInputDevice`, `Broiler.Input.Keyboard.Windows`
`WindowsKeyboardInputDevice`) and a seam (`Broiler.Input.Windows` `IWindowsInputHost`,
`WindowsInputMessageDispatcher`). Nothing in Hosting or Graphics implements `IWindowsInputHost`.
Hosting pins both device packages in `Directory.Packages.props`, but no project references them.
So Broiler.Mail keeps Graphics' legacy callbacks through its `StandardLegacyGraphicsInputAdapter`
(main window, HTML preview window and measurement run).

`WindowsInputBridge` already subclasses the render window and sees every message first. It could:

- implement `IWindowsInputHost`, or feed the two devices directly, for pointer and key messages;
- convert their events with `UiInputEvent.FromMouseMove`, `FromMouseButton` and `FromKeyboardKey`,
  setting `WindowsMouseMessageOptions.CoordinateScale` to the DPI scale on each call;
- turn `ReceiveText` off on the keyboard device, because the bridge owns characters and IME;
- return 0 for those messages so Graphics stops raising its legacy callbacks, while keeping Graphics'
  `SetFocus` on button down and its `TrackMouseEvent`.

### 5. Shift with a tilted wheel (accepted; optional)

The bridge turns Shift+`WM_MOUSEWHEEL` into a horizontal notch that keeps Shift and the vertical
sign, and Broiler.UI preview.18 (ADR 0030) relies on that shape. A wheel tilted while Shift is held
looks the same and scrolls the other way. Broiler.UI records this as accepted. Removing it would need
Hosting to report Shift+wheel in a shape that can be told apart (for example a vertical notch with
Shift, which Broiler.UI already handles the same way) and Broiler.UI to read it. Do not negate the
delta.

## UI Automation

### 6. Decision: UIA2 clients hear the previously selected row first

For a UIA2 client (`System.Windows.Automation` and the tools built on it), UIAutomationCore calls
the provider's `SetFocus` and then its `Select` as two separate calls, and the dispatcher runs
between them. `SetFocus` does not select, so its focus event names the row selected before. A COM
client's `Select` arrives as `Select` alone and is clean. Provider-call traces confirmed this; the
README and the `HoldFocusEvents` remarks document it.

Option: make `SetFocus` a no-op on a row or tab that reports `IsKeyboardFocusable` false. That
matches UIA semantics, and the managed `AutomationElement.SetFocus` refuses unfocusable elements,
but it reverses the documented "SetFocus focuses the container without selecting" and the tests
built on it, such as `TabSetFocusFocusesTheTabViewWithoutSelecting`. The owner decides. Not yet
measured: whether Narrator calls `SetFocus` before `Select`, and the order Windows' own
`SysListView32` and WinUI list views produce.

### 7. `ElementAddedToSelection` and `ElementRemovedFromSelection` for multiple selection

On a multiple-selection list, change detection raises `ElementSelected` for the primary row when
`AddToSelection` or `RemoveFromSelection` changes it. UI Automation expects
`ElementAddedToSelection` (20010) or `ElementRemovedFromSelection` (20011). The change-detection
snapshot would need the full set of selected ids. This predates the branch.

### 8. `Toggle()` does nothing on a check box

`WindowsElementAutomationPeer.Toggle` (about line 859) acts only on `UiButton`-derived toggles.
Hosting does not reference `Broiler.UI.CheckBox`, so on a `UiCheckBox` the call does nothing; reading
its `ToggleState` works. Fix by referencing the check box package or by Broiler.UI offering a toggle
action Hosting can call. This predates the branch.

### 9. Tree follow-ups after Broiler.UI ADR 0030

Broiler.UI at `fd7657f` (`f60c201`, `0411f84`) now reports a change of `UiTreeView.FirstVisibleRow`,
and of how many rows the tree describes, as a semantic change. Hosting still describes the old
behavior:

- Two comments say Broiler.UI reports a scroll only as a render change: `WindowsAutomationBridge.cs`
  (the `_treeRows` note, about lines 410–413) and `WindowsElementAutomationPeer.TreeRows.cs` (on
  `ScrollTreeRowIntoView`, about line 224). The README's "Trees" bullet says the same. Update all
  three.
- `_bridge.QueueTreeCheck(tree)` in `ScrollTreeRowIntoView` is now redundant but harmless, because the
  flush removes duplicates.
- Consider folding the synchronous description raised on a tree's `StateChanged` into the per-flush
  tree check, so a burst of wheel notches or a thumb drag produces one description.
- Document that applications with a tree need `StandardQueuedUiDispatcher`: a resize that changes a
  large tree's rows in view is reported from inside `StandardTreeView` arrange, and with
  `ImmediateUiDispatcher` the comparison then runs mid-layout.

The full suite, including `AutomationStructureTests`' two tree scrolling tests, passes against
Broiler.UI local.7. No Hosting test drives a wheel scroll of a tree through the new invalidation.

### 10. Row, tab and tree-row form properties raise no change events

Rows, tabs and tree rows answer `IsDataValidForForm` and `IsRequiredForForm` when asked, and raise no
property-changed event (documented in the README). Nothing in Broiler.UI marks an item invalid or
required today. Revisit if a presenter or view ever does.

### 11. Focused but unselected items

Broiler.UI has no focused-but-unselected row or tab; only the tree has a focused node, and it moves
with the selection. So Hosting reports keyboard focusability only for the selected item. If
WinUI-style item focus without selection is wanted, Broiler.UI needs that state first; Hosting could
then report it from `GetFocus`, `HasKeyboardFocus` and `IsKeyboardFocusable`.

### 12. Consumers on `ImmediateUiDispatcher`

Structure-change coalescing and the tree row check need a dispatcher whose `Post` runs later.
Broiler.Mail's windows with a bridge use `StandardQueuedUiDispatcher`, pinned by a Mail test on its
adoption branch `claude/ui-09-upstream-adoption` (`NativeBridgeAttachmentTests`).
Broiler.Writer and Broiler.Browser were noted in this round's review as defaulting to
`ImmediateUiDispatcher`; neither was checked against this release. They should move to a queued
dispatcher before relying on coalesced structure events or tree rows.

## Theme

### 13. Decision: scrollbar thumb in WindowText or ButtonText

The system palette draws the thumb in WindowText on a Window track, like every other control at rest
in this palette, because Broiler.UI has no button roles. WinUI draws it in ButtonText, as its
buttons. In Desert, Dusk and Night sky ButtonText is not WindowText (#202020, #B6F6F0, #FFEE32), so
Broiler's bars differ from WinUI apps' bars there. The same gap applies to resting button labels,
which use WindowText. If Broiler.UI adds button roles, map the buttons and the thumb to
ButtonFace/ButtonText together, after the design owner agrees.

### 14. A primary button shows no hover or press feedback in the system palette

`Accent`, `AccentHover` and `AccentPressed` are all Highlight, so a hovered or pressed primary button
looks like one at rest. The highlight pair alone cannot fix this, because the primary label is
HighlightText. This is not yet listed among the known gaps in the `CreateHighContrastTheme` remarks or
the README; at least document it.

### 15. Thumb states, when Broiler.UI adds them

Broiler.UI has no hovered, pressed or disabled thumb role; those look like the thumb at rest. If they
are added (Broiler.UI ADR 0033 says they would default to `ScrollbarThumb`), map hovered and pressed
to Highlight, guarded at 3:1 on Window with a fallback to the thumb, and disabled to GrayText.

### 16. States that still look like the control at rest (needs Broiler.UI)

Broiler.UI draws a pressed secondary button and a pressed spin box arrow on `SurfaceDisabled`, and a
hovered unchecked toggle button on `SurfaceAlt`. Both are the window color here, so the states stay
readable but look like the control at rest. Hosting cannot remap those surfaces without changing
disabled and alternate surfaces. When Broiler.UI draws them in the state pair,
`Pressed_Buttons_And_A_Hovered_Unchecked_Toggle_Look_As_At_Rest` fails: move those states into
`Control_States_Are_Drawn_In_The_Highlight_Pair` and remove the gap from the remarks and the README.

### 17. The list's known gaps in the README and remarks are out of date

Broiler.UI `fd7657f`, the earliest revision this release candidate can be released with, contains
the fix for the list's focus ring (ADR 0034, `ac97b62` and `564809d`). After its ring, a focused
list strokes the ring again across an opaque thumb in
`StandardControlPaint.FocusRingColor(FocusRing, ScrollbarThumb, Background)`: in this palette, the
window color (10.43 to 21:1 on the thumb in the four Windows 11 themes). The stretch is clipped to
the thumb's pill, so the ring stays whole beside the thumb's rounded ends, apart from a
sliver of about 0.5 DIP, at most half the stroke wide, left in the ring's color where it crosses each
end. ADR 0034 also records that a selected row's fill does not meet the thumb: the fill is inset
2 DIP from its row, so the window color lies between the highlight and the thumb.

Hosting still describes the old gap in two places: the end of the README's `WindowsTheme` bullet ("A
focused list strokes its focus ring … the list does not") and the last paragraph of the
`CreateHighContrastTheme` remarks (`WindowsTheme.cs`, about lines 102–107). Rewrite both to describe
the ring as above, and drop the claim that a selected row's highlight fill meets the thumb.
Optionally, add a theme test that a focused list's ring crosses the thumb in the window color.

The same two places list the list's unread dot among the marks drawn in the accent on the window
color. ADR 0034 makes the two-line presenter draw the dot in `AccentText`, or else the row's text
color, where the accent is under 3:1 on the fill under it; in a palette whose highlight is lost on
its window, Hosting sets `AccentText` to the window text. Drop the unread dot from that list too.

### 18. Broiler.UI items that change what this palette draws

These are Broiler.UI changes, open at `fd7657f`. Hosting may need a test or doc update once they
land.

- `StandardFormatCodeView` keeps each token's own color on the selection while `SelectionText` equals
  `Text`, which is the case where HighlightText is WindowText. Paragraph, structure, escape, pending
  and diagnostic codes then have no contrast guarantee on the highlight (HotLight #0000EE on #767676 is
  about 2.1:1, by calculation). Suggested: use `SelectionText` on the selection whenever the palette is
  `IsHighContrast`.
- `StandardCheckBox` draws its check in a fixed white on the Accent fill instead of `OnAccent`; with
  the light highlights of Aquatic, Dusk and Night sky that is about 1.5 to 1.9:1, by calculation.
- `StandardCodeEditor` fills `BracketMatch` after drawing the text, so the matched bracket is painted
  over in every palette. Draw the fill first; in this palette the glyph then needs `StateText`.
- Accent drawn directly on the window color (an unthemed toggle's label, code editor keywords, a
  checked check box, a radio dot, progress and slider fills, active window and dialog borders) does
  not read in a custom contrast theme whose highlight is close to its window
  color. These are documented as known gaps.

Not covered by a test here: a focused tab view's ring in a custom contrast theme.

## Tests and CI

### 19. `ImeComposition_LifecycleAndDuplicateSuppression_MaintainsExactlyOnceDelivery` uses the real clock

The test (`WindowsInputBridgeTests.cs`, about line 273) leaves `Clock` at `TimeProvider.System`, so
its copy-suppression step depends on finishing within the real 500 ms window. The other suppression
tests use `ManualClock`; this one should too.

### 20. The theme-file check passes without checking outside Windows 11 clients

`The_Named_Contrast_Themes_Match_The_Theme_Files_Windows_Ships` returns early when the machine is not
a Windows 11 client or the `.theme` file is missing, because xUnit 2.9 has no dynamic skip. On CI's
`windows-latest` (Windows Server) it therefore passes without comparing anything. The table test
`The_Named_Contrast_Themes_Are_The_Windows_Tables` still runs there. Report it as skipped once the
test framework allows it.

## Checks that need a person or hardware

### 21. A real CJK IME

Inline composition was verified at the message level only; the machine had a German layout and no
IME. Still to check with a Japanese and a Chinese IME: no second copy of the composition, the
candidate window kept, a commit typed once, and a character typed right after a commit typed.
Broiler.Mail's manual acceptance checklist (`docs/ui-manual-acceptance-checklist.md`, checks IME-1
to IME-8, on Broiler.Mail's branch `claude/ui-13-acceptance`, not yet merged) has the steps; its H-01
and contrast-theme sections cover items 22 and 23.

### 22. Screen readers and Accessibility Insights

No Narrator or NVDA pass and no Accessibility Insights (Axe.Windows) run was done; only UI
Automation clients were. Listen in particular for:

- whether Narrator calls `SetFocus` before `Select` and so speaks the old row first (item 6);
- that rows and tabs are not announced as invalid;
- disclosure state, and field errors through `DescribedBy` and `FullDescription`;
- that a field's error and a tree row's expanded or collapsed state are not spoken twice: the
  FormField group's name and Invalid state carry the error as well as the control's `DescribedBy`
  and `FullDescription`, and tree row names include ", expanded" or ", collapsed" alongside the
  ExpandCollapse state;
- `Expand` on a disclosure toggle and `Invoke` on a button moving the keyboard focus to that control
  first, which Broiler.Mail observed.

Axe.Windows may warn that unselected ListItem and TabItem elements report `IsKeyboardFocusable`
false; that is the accepted cost of the `SetFocus` contract.

### 23. Windows actually switched to a contrast theme

The palettes were checked by tests and, in Broiler.Mail, by simulated palettes built from
`WindowsSystemColors`. No visible pass with Windows switched to Aquatic, Desert, Dusk or Night sky,
and no on-screen check of a custom contrast theme, was done.
