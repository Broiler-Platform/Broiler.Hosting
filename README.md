# Broiler.Hosting

Shared desktop hosting runtime, windowing services, input fidelity, and platform integration for the Broiler application suite (**Broiler.Mail**, **Broiler.Code**, **Broiler.Writer**, **Broiler.Browser**).

## Components

### `Broiler.Hosting.Windows`
Provides Windows desktop hosting facilities:
- **`WindowsClipboard`**: Hardened Win32 clipboard integration with 1 MB UTF-16 bounds protection, strict `GlobalSize` validation, direct or lazy HWND owner resolution, and safe OS memory handoff.
- **`WindowsWindowSizing`**: Window sizing, minimum dimension enforcement (DPI-scaled client minimum 640x480) via `WM_GETMINMAXINFO`, and dynamic multi-monitor DPI transition handling via `WM_DPICHANGED`.
- **`WindowsTheme`**: System appearance inspection querying dark/light preference, Windows high contrast mode (`SPI_GETHIGHCONTRAST`), and reduced motion preferences (`SPI_GETCLIENTAREAANIMATION`), exposing `UiSystemSettings`.
- **`WindowsInputBridge`**: High-fidelity native input and scroll integration:
  - Top-level window activation focus handoff (`WM_SETFOCUS` and `WM_ACTIVATE` transferring Win32 focus to the render child HWND).
  - Exactly-once text delivery across `WM_CHAR` and IMM32 IME compositions (`WM_IME_STARTCOMPOSITION`, `WM_IME_COMPOSITION`, synthetic `WM_CHAR` suppression after commit).
  - Surrogate pair assembly (`\uD83D\uDE00`) and stray surrogate cleanup.
  - Shortcut modifier isolation (`Ctrl+C`, `Ctrl+A`, `Ctrl+V`, `Ctrl+Z`, `Ctrl+Backspace`, `Ctrl+Enter` suppressed from text generation) with full `AltGr` support.
  - Sub-notch precision mouse wheel and horizontal tilt wheel handling (`WM_MOUSEWHEEL`, `WM_MOUSEHWHEEL`).
  - Decoupled application-level input filtering hook (`Func<UiInputEvent, bool>?`).
- **`WindowsAutomationBridge` & `WindowsElementAutomationPeer`**: Native Windows UI Automation (UIA) bridge:
  - Implements `IRawElementProviderFragmentRoot`, `IRawElementProviderFragment`, `IRawElementProviderSimple`.
  - Maps Broiler UI controls to standard UIA patterns (`IInvokeProvider`, `IValueProvider`, `ISelectionProvider`, `ISelectionItemProvider`, `IToggleProvider`, `IExpandCollapseProvider`, `IScrollItemProvider`).
  - Coordinate-free control and item tree navigation.
  - Password protection ensuring sensitive fields never expose plain text through UIA.
  - Point hit-testing down to child elements and virtualized list items.
  - Dynamic peer lifecycle management and live region status announcements.

## Architecture & Dependency Separation

`Broiler.Hosting.Windows` follows strict unidirectional dependency layering:
```
UI Abstractions + Graphics + Native Platform
                   │
                   ▼
         Broiler.Hosting.Windows
                   │
                   ▼
  Consumer Heads (Mail, Code, Writer, Browser)
```
Application-specific concerns (close/save guards, draft persistence, credential vaults, application commands) remain strictly outside the hosting component in the respective application layers.

## Testing

```powershell
dotnet test Broiler.Hosting.slnx
```
All unit tests in `tests/Broiler.Hosting.Windows.Tests` run headlessly and validate clipboard safety, sizing/DPI, system theme queries, input fidelity, and accessibility trees.
