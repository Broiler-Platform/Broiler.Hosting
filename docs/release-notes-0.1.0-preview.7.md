# Broiler.Hosting 0.1.0-preview.7 release notes (release candidate)

**Status: not published.** This describes a release candidate prepared on 4–5 October 2026. No pull
request exists for it, hosted CI has not run on it (`ci.yml` runs on pull requests and on pushes to
`main`), and no package has been published. It was verified only with locally packed packages, and
in one consumer, Broiler.Mail.

| | |
| --- | --- |
| Release candidate | code at `c388a66` on branch `claude/roadmap-integration` |
| Base | `origin/main` `380e0e8` (0.1.0-preview.6) |
| Contents | merges of `claude/uia-semantics-mapping` (tip `b846227`, 31 commits) and `claude/hc-palette-and-input` (tip `bd1799e`, 22 commits); both topic branches are on origin at those tips |
| Requires | Broiler.UI 0.1.0-preview.18 (not yet published), Broiler.Native 0.1.0-preview.7 (on NuGet) |
| Verified with | local pack `0.1.0-preview.7-local.5`, built against Broiler.UI `0.1.0-preview.18-local.7` (Broiler.UI `claude/roadmap-integration` at `fd7657f`) |

Open work that this release does not do is listed in [open follow-ups](open-follow-ups-2026-10-05.md).

## Summary

- **UI Automation:** the bridge maps the semantics Broiler.UI 0.1.0-preview.18 adds in its ADR 0028
  (disclosure state, controlled parts, field validity and error descriptions). It also reports
  visible, clipped bounds, names list rows from their presenter, gives tabs their header bounds,
  keeps runtime IDs stable per item, disconnects providers when elements go away, coalesces
  structure changes, and changes what `SetFocus` and `Select` do on rows and tabs.
- **High contrast:** the system palette built by `WindowsTheme.CreateHighContrastTheme` now sets
  the roles Broiler.UI preview.18 adds (selection text, state fill and text, accent text, scrollbar
  track and thumb), flags itself `IsHighContrast`, and applies the system text scale. New
  `WindowsSystemColors.Aquatic`, `Desert`, `Dusk` and `NightSky` hold the Windows 11 contrast themes.
- **Input:** an IME composition is drawn inline only, without a second copy in the IME's own window
  (`WindowsInputBridge.DrawsCompositionInline`, on by default). Alt chords (`WM_SYSCHAR`) are no
  longer typed.
- **Dependencies:** every Broiler.UI package follows `$(BroilerUiVersion)`, now 0.1.0-preview.18.
  This release does not build against Broiler.UI preview.17.

## Dependencies

- **Broiler.UI 0.1.0-preview.18.** Every Broiler.UI pin in `Directory.Packages.props` now follows
  `$(BroilerUiVersion)`. Before, the property said preview.12 while each package was pinned to
  preview.17 directly. The code uses API from Broiler.UI ADR 0028 (semantics), ADR 0029
  (`SelectionText`, `SelectionTextMuted`, `IsHighContrast`, `StateFill`, `StateText`), ADR 0031
  (`AccentText`) and ADR 0033 (`ScrollbarTrack`, `ScrollbarThumb`). It does not compile against a
  Broiler.UI without them. Broiler.UI must be cut at or after `fd7657f`, which contains ADRs
  0028–0034.
- **New package dependencies:** `Broiler.Hosting.Windows` now references `Broiler.UI.TreeView` and
  `Broiler.UI.TreeView.Standard`. The Windows test project adds `FormatCodeView.Standard`,
  `Forms.Standard`, `ListView.Standard`, `ScrollView.Standard`, `SpinBox.Standard`,
  `ToggleButton.Standard`, `Toolbar.Standard` and `TreeView.Standard` (tests only).
- **Unchanged:** Broiler.Native and Broiler.Native.Windows 0.1.0-preview.7, Broiler.Graphics
  0.1.0-preview.7, Broiler.Input 0.1.0-preview.5.
- **Native declarations:** the new ones stay internal in `AutomationInterop`: `UiaDisconnectProvider`,
  `UiaReturnRawElementProvider` with a null provider, the form property IDs, `IsDialog` (30174) and
  the HRESULTs. Broiler.Native is unchanged.

## Changes

### UI Automation (`WindowsAutomationBridge`, `WindowsElementAutomationPeer`)

**Disclosure and validation (Broiler.UI ADR 0028)**

- ExpandCollapse is exposed wherever the semantic state reports Expanded or Collapsed. It acts
  through `IUiExpandable` or the element's `Discloses` target (for example the Cc/Bcc toggle of a
  `FormSection`), and for tree rows through `UiTreeView.Expand`/`Collapse`.
- `ControllerFor` is the element's `Controls` target while that target is shown.
- `IsDataValidForForm` and `IsRequiredForForm` come from the field's control.
- `DescribedBy` lists the shown error message first, then the description. `FullDescription` is
  the description.
- Each of these raises a property-changed event when it changes.

**Rows, tabs and the window's pane valid for forms**

- List rows, tabs and tree rows answer `IsDataValidForForm` and `IsRequiredForForm` from the node
  their container describes them with: valid and not required unless that node says otherwise.
  Before, they left the properties unanswered, and UI Automation reports an unanswered
  `IsDataValidForForm` as false, so clients read every row and tab as not valid.
- A row's form properties are read when a client asks; they raise no change event.
- The window's pane is not a form field. It answers valid and not required without building the
  semantic tree.

**Geometry**

- `BoundingRectangle` is the visible part of an element (`GetVisibleBounds`, cut to the window), and
  `IsOffscreen` is true when nothing of it is visible.
- List rows take their name and bounds from `UiListView.GetItemSemanticNode`. A row is named by its
  presenter, so Broiler.Mail's rows carry the full received date.
- Tab items report their header rectangle (`GetTabHeaderBounds`), not an equal share of the strip.
- `ElementProviderFromPoint` uses `UiSession.HitTest` within the bridge's root, so overlays such as
  an open drop-down win, and a point over another root of the session is the window's.

**Runtime IDs**

- Runtime IDs are `[UiaAppendRuntimeId, n]`, allocated per bridge and never reused. Clients see them
  as `[42, hwnd, 4, n]`.
- An element keeps its ID for its lifetime. A list row, tab or tree row keeps its ID by its item id,
  so rows inserted above a message never make its ID name another message.
- Item and tab peers follow their item id. The index-based peer factories throw for an invalid index.

**Lifetime**

- A peer whose element is removed, disposed or detached, whose row or tab is gone, or a tree row
  outside the exposed set, answers with `UIA_E_ELEMENTNOTAVAILABLE`.
- After `Dispose`, `WM_DESTROY` or the window being gone, every call fails at once rather than after
  a timeout.
- `WM_DESTROY` and `Dispose` disconnect every provider handed to UI Automation and release the
  window's providers.

**Structure changes**

- `StructureChanged` is collected until the session's dispatcher next runs, then raised once per
  changed parent that a client has reached, on that parent's peer.
- This needs a dispatcher whose `Post` runs later, such as `StandardQueuedUiDispatcher`. With
  `ImmediateUiDispatcher` each change is raised on its own, coalesced only as far as Broiler.UI
  batches the changes made during `DispatchInput` and `RenderFrame`.

**Focus and selection**

- `SetFocus` on a row or tab focuses its container without selecting. Before, `SetFocus` on a list
  row or tab item selected it and then focused the list or tab view.
- `Select` does what a click does: focus the list or tab view, then select. `ElementSelected` is
  raised once, from the container's change detection.
- `Select` holds focus events until it returns. A client hears `ElementSelected` on the new row or
  tab, then one `FocusChanged`, on it or on whatever the application's selection handler focused,
  and none if the handler put the focus back where it was.
- A COM client's `Select` reaches the provider as `Select` alone. For a UIA2 client
  (`System.Windows.Automation` and the tools built on it), UIAutomationCore first calls `SetFocus`
  on the item as a separate call, so that client hears the previously selected row first. This is
  documented in the README; see the open follow-ups for the option to change it.
- `AddToSelection` adds a row to a multiple-selection list and leaves the focus alone. `IsSelected`
  and `GetSelection` reflect the whole selection, not only the primary row.
- `IsKeyboardFocusable` is `CanFocus`, or having the focus. A row, tab or tree row reports it only
  while it is the item the focus is on (the selected one, or a tree's focused node) in a container
  that can take the focus, because Broiler.UI has no focused but unselected item. Layout-only
  detection uses the same rule, so a `FocusWhenScrollable` scroll view that is a keyboard stop is a
  control.
- Focus events name what `GetFocus` reports.

**Trees**

- Tree rows are exposed as TreeItem elements of a Tree. A tree's children are the rows in view plus
  the focused row, wherever it is. Another row is not available, as a removed element is.
- A change to those rows (expanding, collapsing, moving the focus, `ScrollItem`) raises one
  `ChildrenInvalidated` on the tree when the dispatcher next runs. `GetSelection` lists only
  selected rows that are children.

**Control types and patterns**

- `LocalizedControlType` is left to UI Automation for standard control types, so clients get the
  system's localized names.
- ScrollView maps to Pane, Toolbar to ToolBar, Tooltip to ToolTip.
- A toggle button is a Button with the Toggle pattern and no Invoke, and raises ToggleState change
  events.
- A window or dialog drawn inside the surface is a Pane; a dialog reports `IsDialog`.

### Theme (`WindowsTheme`, `WindowsSystemColors`)

In high contrast, `ResolveTheme(settings)` and `CreateHighContrastTheme(colors, settings)` now:

- set `SelectionText` and `SelectionTextMuted` to HighlightText. Selected rows and selected text use
  the system highlight pair: 7.0 to 8.0:1 in the Windows 11 contrast themes, up from 1.4 to 1.9:1;
- set `IsHighContrast`;
- apply `settings.TextScale` with `WithTextScale`; a non-positive or non-finite scale counts as 1.0;
- replace a Success, Warning or Danger color below 4.5:1 on Window with WindowText. Of the Windows 11
  themes only Dusk's Danger changes (#FF606A becomes #FFFFFF);
- set `StateFill` to Highlight and `StateText` to HighlightText. A hovered secondary button, a
  hovered spin box arrow, a checked, indeterminate or pressed toggle button and the open toolbar
  overflow button are drawn in the highlight pair (7.00 to 7.96:1 in the four themes, before 1.0 to
  1.9:1). The code editor's matching bracket gets the highlight fill, but Broiler.UI currently paints
  that fill over the bracket's glyph (see open follow-up 18);
- set `AccentText` (the selected tab's label and bar, accent labels, a themed toggle button's label
  at rest and hovered, inline format codes): Highlight where it reads at 4.5:1 on Window, WindowText
  otherwise, and WindowText where HighlightText is WindowText. The four Windows 11 themes keep
  Highlight; only custom contrast themes change;
- set `ScrollbarTrack` to Window and `ScrollbarThumb` to WindowText. The palette drew the same bars
  before, but because the roles are now set, every copy of the palette keeps them.

`WindowsSystemColors.Aquatic`, `Desert`, `Dusk` and `NightSky` are new. They hold the colors of the
Windows 11 contrast themes as Windows ships them (`hcblack`, `hcwhite`, `hc1` and `hc2.theme`), so
`CreateHighContrastTheme(WindowsSystemColors.Dusk, settings)` builds that palette without changing
system settings.

Known gaps are recorded in the `CreateHighContrastTheme` remarks and the README, and in the open
follow-ups. The list's gaps described there (its focus ring across the thumb, its unread dot) no
longer hold with Broiler.UI `fd7657f`; open follow-up 17 asks to rewrite them.

### Input (`WindowsInputBridge`)

**Inline IME composition.** New `DrawsCompositionInline`, on by default, for messages that arrive
through the render-window subclass:

- `WM_IME_SETCONTEXT` goes on without `ISC_SHOWUICOMPOSITIONWINDOW`, so the IME no longer shows a
  second copy of the composition in its own window. Its candidate list and guide still show.
- `WM_IME_STARTCOMPOSITION`, and every `WM_IME_COMPOSITION` except one whose result string the bridge
  could not read, are kept from `DefWindowProc`, so Windows makes no `WM_IME_CHAR` or `WM_CHAR`
  copies of a commit.
- The bridge no longer expects copies of such a commit: a character typed right after a commit is
  typed, even if it matches the commit's first character. Copy suppression is armed only when the
  commit goes on to `DefWindowProc`: with the property off, or for a host that calls
  `ProcessNativeMessage` itself.
- The setting covers the whole render window. While an element that draws no composition has the
  focus (a password field, a list, a tree, a menu, a button), the application must turn the IME off
  for the window.

**Alt chords.** `WM_SYSCHAR` (Alt+F, Alt+Space) is no longer typed. It goes on to `DefWindowProc`,
which turns it into `SC_KEYMENU`, so Alt+Space opens the window menu and an Alt+letter that no menu
takes beeps. AltGr and Alt+numpad characters arrive as `WM_CHAR` and still type; `WM_SYSDEADCHAR`
is unchanged.

## Compatibility notes for consumers

**Packages**

- Take Broiler.UI 0.1.0-preview.18, this release and Broiler.Native 0.1.0-preview.7 (including
  Native.Windows) in one change, to avoid NU1605 downgrade errors. Graphics preview.7 and Input
  preview.5 stay.
- A consumer that skips preview.6, as Broiler.Mail does, also takes preview.6's changes, which moved
  native interop into Broiler.Native: `InputNative` is gone (use `WindowNative` and `ImmNative`,
  including `WindowNative.POINT`), and `UiaNative` and the provider interfaces come from
  `Broiler.Native.Windows.Accessibility`. Files with `using static WindowNative` need a compile check
  against Native preview.7.

**UI Automation**

- Runtime IDs are now two elements long from the provider (`[42, hwnd, 4, n]` as clients see them).
  Code or tests that index `GetRuntimeId()?[2]` throw `IndexOutOfRangeException`; compare peers
  instead (Broiler.Mail changed one test to `Assert.Same(bridge.GetOrCreatePeer(edit), focusedPeer)`).
- Row IDs stay with their item, so scripts no longer need to look rows up again by AutomationId after
  new items arrive.
- Row names come from the presenter (in Broiler.Mail: "Unread, From: …, Subject: …, Received: <full
  date>"), not from the item text. Update name matches.
- Tab items report their header rectangle. Scripts that clicked an offset into the strip can click
  the tab item's bounds instead.
- Unselected rows and tabs report `IsKeyboardFocusable` false. Accessibility Insights (Axe.Windows)
  may warn about this; it is the accepted cost of an honest `SetFocus` contract.
- In-surface dialogs are Pane with `IsDialog`, not Window.
- `LocalizedControlType` is the system's localized name for standard types (for example "Bearbeiten"
  on a German system), not an English string.
- `SetFocus` on a row or tab no longer selects it; use `Select`. A UIA2 `Select` now leaves focus
  where the application's selection handler puts it, but its client first hears the previously
  selected row (see Focus and selection above).
- Structure-change coalescing and the tree's row check need a queued dispatcher. Use
  `StandardQueuedUiDispatcher` in every window that attaches a bridge.

**Input**

- Tests that post a commit through the render window and then post the `WM_CHAR` copies Windows used
  to make must drop those copies. Windows no longer makes them for an inline commit, and the bridge no
  longer suppresses them there, so keeping them types the commit twice (Broiler.Mail's composer test
  would have typed "Hi 日本日本!"). Tests that call `ProcessNativeMessage` directly keep their
  copy-suppression expectations.
- Turn the IME off (for example with `ImmAssociateContextEx` and a null context) whenever the focus
  owner draws no composition, not only for password fields.
- Set `DrawsCompositionInline = false` to keep the preview.6 behavior.
- Alt chords an application handles on key down still reach `DefWindowProc` and beep. Do not add
  Alt+letter shortcuts until Hosting can mark them handled (see the open follow-ups).

**High contrast**

- The palette from `ResolveTheme` and `CreateHighContrastTheme` already applies `TextScale`,
  `ReducedMotion`, `Density` and `IsHighContrast`. Do not call `WithTextScale` on it again.
- The new roles are set explicitly, so `with` copies keep them: a copy that changes `AccentSoft`,
  `SelectionText` or `Text` keeps the state pair, one that changes `Accent` keeps `AccentText`, and
  one that changes `SurfaceDisabled` or `BorderStrong` keeps its scrollbars.
- A copy that clears `IsHighContrast` keeps opaque Window/WindowText scrollbars in the scroll view,
  rich edit and format code view (in Aquatic, Desert, Dusk, and custom themes whose window and window
  text are not near black and white). Before, those controls went back to their translucent overlay
  bars. The rich edit now lays its text out 12 DIP narrower, beside the bar, and the format code view
  ends its text at the bar. No consumer in Hosting or Broiler.Mail makes such a copy today.

## Verification

All results below are from local builds on one Windows 11 Enterprise 26200 machine (German
keyboard layout, no IME installed). Hosted CI has not run: `ci.yml` runs on pull requests and on
pushes to `main`, and no pull request exists.

**Hosting test suite**

| Revision | Built against | Result |
| --- | --- | --- |
| `c388a66` (release candidate) | Broiler.UI `0.1.0-preview.18-local.7` | 200 of 200 (Windows 188, Linux 6, Android 6), run twice; every project restored Broiler.UI* local.7 |
| `b846227` (`claude/uia-semantics-mapping`) | Broiler.UI `0.1.0-preview.18-local.3` | 122 of 122 (Windows 110), run twice; build 0 warnings |
| `bd1799e` (`claude/hc-palette-and-input`) | Broiler.UI `0.1.0-preview.18-local.5` | 155 of 155 (Windows 143), run twice; build 0 warnings |

At each review round, the tests for each review fix were shown to fail without that fix (by stashing
it or by a targeted mutation) and to pass with it restored. Some tests that only pin current
behavior, such as `InlineComposition_KeepsACompositionMessageWithNothingToDeliverFromTheImeWindow`,
were not revert-checked.

`c388a66` was packed as `Broiler.Hosting.Windows`, `.Linux` and `.Android`
`0.1.0-preview.7-local.5` into the local feed `D:\local-packages\roadmap-2026-10-04`. The Windows
nuspec depends on Broiler.UI* `0.1.0-preview.18-local.7` in both target groups. To reproduce the test
run against that feed:

```powershell
dotnet test Broiler.Hosting.slnx -c Release -p:BroilerUiVersion=0.1.0-preview.18-local.7 -p:RestoreAdditionalProjectSources=D:/local-packages/roadmap-2026-10-04
```

**Native checks of this code**

The UIA2 probe (23 of 23) and the event-order and provider-call traces below used scratch hosts and
clients outside this repository; they cannot be rerun from it. The IME, Alt-chord and theme-file
checks are tests in `tests/Broiler.Hosting.Windows.Tests`.

- An external UIA2 client (`System.Windows.Automation`) against a hidden window hosting the bridge
  passed 23 of 23 checks, under JIT and NativeAOT, on `claude/uia-semantics-mapping` before its last
  review round. The checks covered runtime IDs, disclosure state, a row keeping its name and ID after
  a refresh, tab bounds, focus after a UIA tab Select, only the selected tab keyboard focusable, and
  gone elements failing with `ElementNotAvailable`.
- The final event order was checked with a COM client and a managed client against the same kind of
  host at `b846227`. Provider-call traces showed UIAutomationCore calling `SetFocus` and then
  `Select` as two calls for the managed client, with the dispatcher running between them.
- IME and Alt chords were checked at the message level only: `DefWindowProc` hands
  `WM_IME_SETCONTEXT` to the default IME window with lParam `0x4000000F` in inline mode and
  `0xC000000F` otherwise, and answers `WM_SYSCHAR` for 'f' and ' ' with `WM_SYSCOMMAND`
  `SC_KEYMENU`.
- The four named contrast themes match the `.theme` files this Windows 11 machine ships. Windows' own
  `hc2.theme` has a malformed `MenuText=255 255 255e` entry, so the test reads only the keys it needs.

**Consumer: Broiler.Mail**

Broiler.Mail branch `claude/ui-09-upstream-adoption` at `e870135` consumed local.5 together with
Broiler.UI local.7 and Native preview.7:

- Full suite: 934 of 934 (640 shared, 291 Windows, 3 Linux), run twice; each of the 38 commits above
  the published-package stack also passes.
- Probe-Uia (NativeAOT, outside client): 6 of 6. The Cc/Bcc disclosure reports Collapsed, then
  Expanded with `ControllerFor` naming the controlled part; refused To and Email address fields report
  `IsDataValidForForm` false with the error in `DescribedBy` and `FullDescription`; all 50 row names
  carry the received date; 50 rows and 4 tabs report valid and not required; after new mail, the 47
  rows listed before and after keep their runtime IDs.
- Accept-Refresh: 4 of 4. Focus returns to Reply after a UIA Select of the Inbox tab, which failed
  on Hosting preview.5; the Inbox tab item reports its 95 px header instead of 412 px.
- Accept-UI: full matrix 132 of 132, reader 20 of 20, 200 % text 44 of 44, and the simulated
  palettes built with `CreateHighContrastTheme` from `WindowsSystemColors` Aquatic, Desert, Dusk and
  Night sky 10 of 10 each. These palettes were set for the app with `--contrast`; no Windows contrast
  theme was turned on.
- Record-Uia: the recorded focus and selection events match the order described above for COM and
  UIA2 clients.

**Not verified**

- Any build against published packages; Broiler.UI 0.1.0-preview.18 does not exist on nuget.org yet.
- Hosted CI (`ci.yml`), including the Linux runner.
- A real IME composition (Japanese or Chinese), a screen reader (Narrator, NVDA), an Accessibility
  Insights run, and Windows actually switched to a contrast theme.
- Consumers other than Broiler.Mail (Broiler.Code, Broiler.Writer, Broiler.Browser).

## Release steps

1. Wait for Broiler.UI 0.1.0-preview.18 on nuget.org, cut from `fd7657f` or later after its ADRs
   0028–0034 are accepted. It must include every package `Directory.Packages.props` pins,
   including `TreeView` and `TreeView.Standard` (now runtime dependencies of
   `Broiler.Hosting.Windows`) and the test-only `FormatCodeView.Standard`, `Forms.Standard`,
   `ScrollView.Standard`, `SpinBox.Standard`, `ToggleButton.Standard` and `Toolbar.Standard`.
2. Rebuild this branch against it without local overrides. `NuGet.config` lists only nuget.org, so a
   plain restore must succeed:

   ```powershell
   dotnet build Broiler.Hosting.slnx -c Release --no-incremental
   dotnet test Broiler.Hosting.slnx -c Release
   ```

   Run the tests twice, and check that every `project.assets.json` resolves Broiler.UI*
   0.1.0-preview.18. If Broiler.UI was cut after `fd7657f`, review what changed before going on.
3. Open a pull request from the branch (pushing it first if needed); let CI run on `ubuntu-latest`
   and `windows-latest`. The theme-file test returns early on `windows-latest` (Windows Server), so it
   passes there without checking anything.
4. Merge into `main`. The branch history contains `683823d` (scrollbar thumb in ButtonText) followed
   by `bd1799e` (back to WindowText); squashing at merge keeps the log from describing a change that
   was reverted.
5. Publish 0.1.0-preview.7 with the Publish workflow (`.github/workflows/publish.yml`), and check
   that the resolved version is 0.1.0-preview.7. (The workflow has since lost its dry-run mode:
   every run pushes, and CI is the no-push rehearsal.)
6. Consumers: Broiler.Mail rebuilds its adoption branch against the published Broiler.UI
   preview.18 and Hosting preview.7 and reruns its suite, Accept-UI (default, dusk, aquatic),
   Accept-Refresh and Probe-Uia before merging.
7. Update this file: replace the status line with the published commit and date.
