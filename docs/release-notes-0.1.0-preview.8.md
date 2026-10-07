# Broiler.Hosting 0.1.0-preview.8 release notes

**Status: not published.** Prepared on 7 October 2026 on branch `nuget-only-publishing`. The next
Publish run resolves to 0.1.0-preview.8 (preview.1 to preview.7 are on nuget.org).

Additive over 0.1.0-preview.7: no public member was removed or changed, and the defaults behave as
before. Dependencies are unchanged (Broiler.UI 0.1.0-preview.18, Broiler.Graphics 0.1.0-preview.7,
Broiler.Native 0.1.0-preview.7, Broiler.Input 0.1.0-preview.5).

## Changes

### `Broiler.Hosting.Linux`

- **`LinuxInputCoordinator.QuitOnEscape`** (`init`, default `false`) and
  **`LinuxInputCoordinator.QuitRequested`**. With `QuitOnEscape` set, an Escape key-down latches
  `QuitRequested`, which a host loop polls to exit; the Escape key event is still dispatched to the
  UI. The startup log names Escape as the exit key only when it is one. This is what Broiler.Browser's
  Linux head needs to drop its local copy of the coordinator:

  ```csharp
  await using LinuxInputCoordinator input = new(enabled, Console.WriteLine,
      externalPointer: true, applicationName: "Broiler Browser") { QuitOnEscape = true };
  // ...
  while (!input.QuitRequested && ...)
  ```

- The real-X-server `LinuxX11Clipboard` tests moved here from the applications' shared test project
  (`tests/Broiler.Hosting.Linux.Tests/LinuxX11ClipboardTests.cs`). They return early without a
  `DISPLAY`; run them under `xvfb-run -a dotnet test tests/Broiler.Hosting.Linux.Tests`.

### Build and release

- Publish always pushes to nuget.org; the `dry-run` input is gone. CI now also runs
  `eng/verify-feed.ps1` after packing, so every push and pull request checks a fresh consumer
  restore from nuget.org without pushing anything.

## Consumers

Applications that carried local copies of the hosting utilities (Browser, Writer, Code, Plate) can
reference the packages instead. Only code that uses `QuitOnEscape`/`QuitRequested` needs preview.8;
everything else is in preview.7.
