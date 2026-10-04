# Broiler.Hosting

Shared desktop and mobile hosting runtime, windowing services, input fidelity, and platform integration for the Broiler application suite (**Broiler.Mail**, **Broiler.Code**, **Broiler.Writer**, **Broiler.Browser**).

## Components

### `Broiler.Hosting.Windows`
Provides Windows desktop hosting facilities:
- **`WindowsClipboard`**: Hardened Win32 clipboard integration with 1 MB UTF-16 bounds protection, strict `GlobalSize` validation, direct or lazy HWND owner resolution, and safe OS memory handoff.
- **`WindowsWindowSizing`**: Window sizing, minimum dimension enforcement (DPI-scaled client minimum 640x480) via `WM_GETMINMAXINFO`, and dynamic multi-monitor DPI transition handling via `WM_DPICHANGED`.
- **`WindowsTheme`**: System appearance inspection querying dark/light preference, Windows high contrast mode (`SPI_GETHIGHCONTRAST`), reduced motion preferences (`SPI_GETCLIENTAREAANIMATION`), and the "Make text bigger" text scale (`TextScaleFactor`, 1.0 to 2.25), exposing `UiSystemSettings`. `ResolveTheme(UiSystemSettings)` returns the standard preset, or in high contrast a palette built from the user's actual system colors (`WindowsSystemColors`, `CreateHighContrastTheme`): selected rows and selected text use the system highlight pair (`SelectionText` is `HighlightText`), the palette is flagged `IsHighContrast` and takes the settings' text scale, and a focus ring, link, or status color that blends into the window background falls back to the window text color. `WindowsSystemColors.Aquatic`, `Desert`, `Dusk`, and `NightSky` hold the Windows 11 contrast themes' colors, to build their palettes without changing the system's settings.
- **`WindowsTitleBar`**: Matches the native caption to a dark or light palette (`DWMWA_USE_IMMERSIVE_DARK_MODE`, Windows 10 build 19041 or later). Call it after the native window exists and whenever the palette changes.
- **`WindowsInputBridge`**: High-fidelity native input and scroll integration:
  - Top-level window activation focus handoff (`WM_SETFOCUS` and `WM_ACTIVATE` transferring Win32 focus to the render child HWND).
  - Exactly-once text delivery across `WM_CHAR` and IMM32 IME compositions (`WM_IME_STARTCOMPOSITION`, `WM_IME_COMPOSITION`, synthetic `WM_CHAR` suppression after commit).
  - Surrogate pair assembly (`\uD83D\uDE00`) and stray surrogate cleanup.
  - Shortcut modifier isolation (`Ctrl+C`, `Ctrl+A`, `Ctrl+V`, `Ctrl+Z`, `Ctrl+Backspace`, `Ctrl+Enter` suppressed from text generation) with full `AltGr` and Alt+numpad support. Alt chords (`WM_SYSCHAR`, such as `Alt+F`) are never typed; they go on to `DefWindowProc`, so `Alt+Space` opens the window menu.
  - Sub-notch precision mouse wheel and horizontal tilt wheel handling (`WM_MOUSEWHEEL`, `WM_MOUSEHWHEEL`).
  - Decoupled application-level input filtering hook (`Func<UiInputEvent, bool>?`).
- **`WindowsAutomationBridge` & `WindowsElementAutomationPeer`**: Native Windows UI Automation (UIA) bridge:
  - Implements `IRawElementProviderFragmentRoot`, `IRawElementProviderFragment`, `IRawElementProviderSimple`.
  - Maps Broiler UI controls to standard UIA patterns (`IInvokeProvider`, `IValueProvider`, `ISelectionProvider`, `ISelectionItemProvider`, `IToggleProvider`, `IExpandCollapseProvider`, `IScrollItemProvider`, and `ITextProvider`/`ITextRangeProvider` for Edit and RichEdit text, by character, word, paragraph, and document, with search and selection; never for password fields).
  - Names come from the semantic name, never a type name; a field targeted by a label is named by that label and reports it as `LabeledBy`, with its text as Value and its placeholder as HelpText. Unnamed layout containers are non-control elements, so the Control view shows their children directly.
  - Only the selected tab's content is exposed: inactive tab content is skipped in navigation and hit-testing and reported offscreen.
  - Change events are raised only for real differences (name, enabled state, value and text, text selection, list or tab selection).
  - Create the bridge with the handle of an existing window: with a `Direct2DWindow`, in `OnCreated`. A zero handle attaches nothing and clients see an empty pane. The same applies to `WindowsInputBridge`.
  - Coordinate-free control and item tree navigation.
  - Password protection ensuring sensitive fields never expose plain text through UIA.
  - Point hit-testing down to child elements and virtualized list items.
  - Dynamic peer lifecycle management.
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
