# Broiler.Hosting

Shared desktop and mobile hosting runtime, windowing services, input fidelity, and platform integration for the Broiler application suite (**Broiler.Mail**, **Broiler.Code**, **Broiler.Writer**, **Broiler.Browser**).

## Components

### `Broiler.Hosting.Windows`
Provides Windows desktop hosting facilities:
- **`WindowsClipboard`**: Hardened Win32 clipboard integration with 1 MB UTF-16 bounds protection, strict `GlobalSize` validation, direct or lazy HWND owner resolution, and safe OS memory handoff.
- **`WindowsWindowSizing`**: Window sizing, minimum dimension enforcement (DPI-scaled client minimum 640x480) via `WM_GETMINMAXINFO`, and dynamic multi-monitor DPI transition handling via `WM_DPICHANGED`.
- **`WindowsTheme`**: System appearance inspection querying dark/light preference, Windows high contrast mode (`SPI_GETHIGHCONTRAST`), reduced motion preferences (`SPI_GETCLIENTAREAANIMATION`), and the "Make text bigger" text scale (`TextScaleFactor`, 1.0 to 2.25), exposing `UiSystemSettings`. `ResolveTheme(UiSystemSettings)` returns the standard preset, or in high contrast a palette built from the user's actual system colors (`WindowsSystemColors`, `CreateHighContrastTheme`), with readable fallbacks for a focus ring or link color that blends into the window background.
- **`WindowsTitleBar`**: Matches the native caption to a dark or light palette (`DWMWA_USE_IMMERSIVE_DARK_MODE`, Windows 10 build 19041 or later). Call it after the native window exists and whenever the palette changes.
- **`WindowsInputBridge`**: High-fidelity native input and scroll integration:
  - Top-level window activation focus handoff (`WM_SETFOCUS` and `WM_ACTIVATE` transferring Win32 focus to the render child HWND).
  - Exactly-once text delivery across `WM_CHAR` and IMM32 IME compositions (`WM_IME_STARTCOMPOSITION`, `WM_IME_COMPOSITION`, synthetic `WM_CHAR` suppression after commit).
  - Surrogate pair assembly (`\uD83D\uDE00`) and stray surrogate cleanup.
  - Shortcut modifier isolation (`Ctrl+C`, `Ctrl+A`, `Ctrl+V`, `Ctrl+Z`, `Ctrl+Backspace`, `Ctrl+Enter` suppressed from text generation) with full `AltGr` support.
  - Sub-notch precision mouse wheel and horizontal tilt wheel handling (`WM_MOUSEWHEEL`, `WM_MOUSEHWHEEL`).
  - Decoupled application-level input filtering hook (`Func<UiInputEvent, bool>?`).
- **`WindowsAutomationBridge` & `WindowsElementAutomationPeer`**: Native Windows UI Automation (UIA) bridge:
  - Implements `IRawElementProviderFragmentRoot`, `IRawElementProviderFragment`, `IRawElementProviderSimple`.
  - Maps Broiler UI controls to standard UIA patterns (`IInvokeProvider`, `IValueProvider`, `ISelectionProvider`, `ISelectionItemProvider`, `IToggleProvider`, `IExpandCollapseProvider`, `IScrollItemProvider`, and `ITextProvider`/`ITextRangeProvider` for Edit and RichEdit text, by character, word, paragraph, and document, with search and selection; never for password fields).
  - A toggle button is a Button with the Toggle pattern and no Invoke, as its click is its toggle; a check box reports its state through Toggle as well. A window or dialog drawn inside the surface is a Pane, since the Window control type requires the Window and Transform patterns only the host window has, and a dialog reports `IsDialog`.
  - Names come from the semantic name, never a type name; a field targeted by a label is named by that label and reports it as `LabeledBy`, with its text as Value and its placeholder as HelpText. Unnamed layout containers are non-control elements, so the Control view shows their children directly.
  - Only the selected tab's content is exposed: inactive tab content is skipped in navigation and hit-testing and reported offscreen.
  - Disclosure and validation (Broiler.UI ADR 0028): ExpandCollapse wherever the semantic state reports Expanded or Collapsed, acting through `IUiExpandable` or the element's `Discloses` target (the Cc/Bcc toggle of a `FormSection`), and through `UiTreeView.Expand`/`Collapse` for tree rows, which are exposed as tree items. `ControllerFor` is `Controls` while it is shown; `IsDataValidForForm`, `IsRequiredForForm`, `DescribedBy` (the shown error message first, then the description) and `FullDescription` come from the field's control. Rows, tabs and tree rows answer both form properties as well, from the node their container describes them with (a row's presenter, the view's children): valid and not required unless that node says otherwise. UIA reports `IsDataValidForForm` left unanswered as false, so clients read every row and tab as not valid. A row's form properties are read when a client asks and raise no change event: nothing in Broiler.UI marks a row, tab or tree row invalid or required, and watching them would describe every row a client holds on each change of its container. The window's pane is no form field and answers valid and not required.
  - Trees: a tree's children are the rows Broiler.UI describes, those in view, and the focused row wherever it is, so the focus and its events always name a child of the tree; another row is not available, as a removed element is, and the selection lists only the selected rows that are children. A change to those rows (expanding, collapsing, moving the focus, `ScrollItem`) raises one `ChildrenInvalidated` on the tree when the dispatcher next runs. A wheel scroll that leaves the focus where it is reaches the bridge only with the tree's next state change, because Broiler.UI reports it as a render change alone.
  - Geometry: bounding rectangles are what can be seen, clipped to scroll viewports, list content and the window, and an element, row or tab with nothing in view is offscreen. List rows take their name and bounds from `UiListView.GetItemSemanticNode`, tabs their header from `GetTabHeaderBounds`, and `ElementProviderFromPoint` uses `UiSession.HitTest` (overlays such as an open drop-down win) with the same row and tab geometry; a point over another root of the session is the window's.
  - Runtime IDs are `[UiaAppendRuntimeId, n]`, allocated per bridge and never reused: an element keeps its ID for its lifetime, and a list row, tab or tree row keeps its ID by its item id, so rows inserted above a message never make its ID name another one. The hosted root leaves its ID to the window.
  - Focus: `SetFocus` on a row or tab focuses its container without selecting; `Select` does what a click does (focus the list or tab view, then select), so an application that moves focus when the selection changes keeps that decision under UI Automation too. `AddToSelection` adds a row to a multiple-selection list and leaves the focus alone. Focus events name what `GetFocus` reports, the selected row or tab of a focused container, and `ElementSelected` comes once, from the container's change detection. `Select` holds focus events until it returns, so a client hears `ElementSelected` on the new row or tab and then one `FocusChanged`, on it or on whatever the application's selection handler focused (none if the handler put the focus back where it was), never on the row selected before. That is what a COM client hears, whose `Select` reaches the provider as `Select` alone. For a UIA2 client's `Select` (`System.Windows.Automation` and the tools built on it), UIAutomationCore first calls `SetFocus` on the item, as a call of its own, and since `SetFocus` does not select, that client hears the row selected before first. `IsKeyboardFocusable` is `CanFocus`, or having the focus; a row, tab or tree row reports it only while it is the item the focus is on (the selected one, or a tree's focused node) in a container that can take the focus, because Broiler.UI has no focused but unselected item. `ProviderOwnsSetFocus` is not declared, so UIA gives the host window the Win32 focus first.
  - This departs from Win32 and WinUI, whose list and tab items can hold the focus unselected and whose `Select` leaves the keyboard focus alone: here `SetFocus` on an unselected item leaves the focus on the selected one, and `Select` moves the focus to the container first, in the order a click does, so that focus never stays inside tab content the new selection hides.
  - Change events are raised only for real differences (name, enabled state, value and text, text selection, list, tab or tree selection, expand state, toggle state, an element's validity, description). Structure changes are collected until the session's dispatcher next runs and raised once per changed parent a client has reached. That takes a dispatcher whose `Post` runs later, such as `StandardQueuedUiDispatcher`: with `ImmediateUiDispatcher` each change is raised at once, coalesced only as far as Broiler.UI batches the changes made during `DispatchInput` and `RenderFrame`.
  - Lifetime: a peer whose element is removed or disposed, or whose row or tab is gone, answers native calls with `UIA_E_ELEMENTNOTAVAILABLE`, as does every provider once the bridge is disposed or its window destroyed, at once rather than after a timeout. `WM_DESTROY` and `Dispose` disconnect every provider handed to UIA and release the window's providers.
  - Create the bridge with the handle of an existing window: with a `Direct2DWindow`, in `OnCreated`. A zero handle attaches nothing and clients see an empty pane. The same applies to `WindowsInputBridge`.
  - Coordinate-free control and item tree navigation.
  - Password protection ensuring sensitive fields never expose plain text through UIA.
  - `UiSession.AnnounceStatus` raises a UIA notification carrying the announced text, so clients read the status rather than the source element's name. Each source element is one activity: a newer status replaces one still queued, and an error (`Invalid` state) is read before other speech. Status elements report `LiveSetting` Polite, or Assertive while reporting an error. Without text, or on Windows before 10 1709, a live-region change is raised instead.

### `Broiler.Hosting.Linux`
Provides Linux desktop hosting facilities:
- **`LinuxUiHost`**: Reusable `IUiHost` implementation adapting `IBroilerRenderer` and `IBroilerSurface` to Broiler UI sessions, tracking viewport dimensions, DPI scale, frame indexing, and invalidations without double-scaling.
- **`LinuxX11Clipboard`**: Hardened X11 clipboard and primary selection host (`IUiClipboardHost`, `IDisposable`) with 1 MB payload limits, `INCR` streaming, UTF-8/Latin-1 conversion, timeout bounds, and safe non-Linux execution guards.
- **`LinuxBackendDiagnostics`**: Preflight verification for X11/EGL/OpenGL dependencies (`libEGL.so.1`, `libGL.so.1`, `libOpenGL.so.0`, `libX11.so.6`, `DISPLAY`, architecture).
- **`LinuxInputCoordinator` & `LinuxInputSnapshot`**: Evdev input stream coordination unifying keyboard and mouse devices, merging modifier keys across separate evdev nodes, pointer clamping, and non-blocking event dispatch.

### `Broiler.Hosting.Android`
Provides Android hosting facilities with multi-targeting support (`net10.0` for headless testing and `net10.0-android36.0` for full runtime integration):
- **`AndroidUiHost`**: Reusable `IUiHost`, `IUiClipboardHost`, and `IUiTextInputHost` adapter supporting `AndroidBroilerView`, direct delegates, or headless surface/renderer.
- **`AndroidBackendDiagnostics`**: Preflight check for Android runtime, architecture, and native graphic libraries (`libEGL.so`, `libGLESv3.so`, `libandroid.so`), with safe degradation on non-Android hosts.
- **`AndroidBroilerView`**: Hardware-accelerated `SurfaceView` with Choreographer vsync loop, device lost recovery, density/viewport tracking, touch/hover/key routing, IME lifecycle, and clipboard glue.
- **`AndroidCanvasRenderer`**: Hardware-accelerated `IBroilerRenderer` replaying display lists onto Android `Canvas` and `Paint` with typeface caching.
- **`AndroidInputCoordinator`**: Unified coordinator routing touch contacts, pen contacts, key events, and IME text events into `UiInputEvent`s.
- **`AndroidInsetLayout`**: `FrameLayout` handling display cutouts, navigation bars, and status bar insets.
- **`BroilerInputConnection`**: `BaseInputConnection` bridging Android IME to `AndroidTextInputDevice`.

## Architecture & Dependency Separation

`Broiler.Hosting` follows strict unidirectional dependency layering:
```
UI Abstractions + Graphics + Native Platform
                   │
                   ▼
       Broiler.Hosting.<Platform>
                   │
                   ▼
  Consumer Heads (Mail, Code, Writer, Browser)
```
Application-specific concerns (close/save guards, draft persistence, credential vaults, application commands) remain strictly outside the hosting component in the respective application layers.

## Testing

```powershell
dotnet test Broiler.Hosting.slnx
```
All unit tests in `tests/Broiler.Hosting.Windows.Tests`, `tests/Broiler.Hosting.Linux.Tests`, and `tests/Broiler.Hosting.Android.Tests` run headlessly and validate clipboard safety, sizing/DPI, system theme queries, input fidelity, backend diagnostics, and accessibility trees across platforms.
