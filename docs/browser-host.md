# Browser host contracts (protocol 1)

`Broiler.Hosting.Contracts` supplies `IDocumentPort`, `DocumentContent`, explicit
operation outcomes, a frame dispatcher and transient trusted clipboard scope.
It has no UI, Graphics, native-host or process dependency.
`Broiler.Hosting.WebAssembly` supplies the validated document transport seam and
the package-owned `broiler.hosting.webassembly.js` module. Both projects are in
the normal Hosting solution, test discovery and package workflow.

## Documents

`IDocumentPort.OpenAsync` returns bytes, MIME type and a display filename, or a
cancelled/failed result. `ExportAsync` accepts the same content shape. Neither
requires a local path. Native implementations may use paths internally. Byte
buffers must remain stable while an operation is in flight.

The browser adapter receives host-supplied interop delegates so it does not force
JSON serialization, base64 or a particular JS marshalling strategy on products.
`BrowserDocumentPort` enforces cancellation, maximum input bytes and leaf-name
normalization. Its JS counterpart bounds the selected file before reading,
rejects concurrent pickers, removes picker elements on cancellation and exports
a Blob with a safe filename. Aborting while bytes are being read discards the
result. The native picker uses the modern input
[`cancel` event](https://developer.mozilla.org/en-US/docs/Web/API/HTMLInputElement/cancel_event).

`DownloadInitiated` means the host requested a browser download. It must not
clear an application's dirty state as though a durable write had been confirmed.
`Saved` is reserved for providers that actually confirm their write. Cancelling
an application operation through its cancellation token uses normal .NET
cancellation; closing a picker returns `Cancelled`.

## Canvas event adapter

Import `createCanvasHost` from the package module and supply `canvas`, the hidden
`input` textarea, its `container`, `render` and a `dispatch` object. Dispatch
callbacks are synchronous and receive these shapes:

| Callback | Data |
| --- | --- |
| `resize` | `{width,height,dpr}`; dimensions in logical CSS pixels |
| `pointer` | `{kind,id,x,y,buttons,button,time}`; kind down/move/up |
| `pointerCancel` | `{id}`; capture loss, pointer cancel, blur or disposal |
| `wheel` | `{x,y,deltaX,deltaY,deltaMode,time}`; adapter preserves browser units |
| `key` | `{key,code,down,repeat,shift,ctrl,alt,meta,time}` |
| `text` | `{text,time}`; committed text including spaces |
| `composition` | `{phase,text,start,time}`; start/update/commit/cancel |
| `clipboard` | `{kind,text}`; return `{handled,text}` synchronously |

Map these to the product's existing managed exports. In particular, the
composition **commit** must be handled once through the composition callback;
it is not emitted again through `text`. Clipboard callbacks run only for trusted
browser events; Ctrl/Cmd+C/X/V remain available to those events even if included
in the caller's consumed shortcut list. Plain-text clipboard scope in managed
code is synchronous, may contain an empty string, and always clears on exit,
including exceptions. It is not a permission to read clipboard contents later.

Use `publishCaret({active,x,y,width,height})` after the managed frame. The caret
is in CSS pixels, independent of DPR. Active text controls focus the textarea
only while the canvas/textarea already owns DOM focus. Other page controls and
pickers retain focus. Publishing an inactive caret returns textarea focus to the
canvas. `invalidate()` coalesces animation-frame requests. Work queued during a
managed dispatcher drain belongs to the next frame. `dispose()` releases input,
cancels the pending frame and removes observers/listeners.

Container resize and DPR changes are observed separately. The application sizes
its renderer's backing surface using DPR; it must not multiply pointer or caret
coordinates by DPR again. Map wheel delta modes to the product's own scrolling
policy. The host adapter does not pretend pixels, lines and pages are equivalent.

## Adoption and validation

The Office W1 contract sample exercises this module with real .NET exports and
`BrowserDocumentPort`, including file bytes across interop. Its sample-specific
base64 wire format is bounded to 64 bytes and is **not** a production large-file
transport. Products should select a bounded marshalling strategy for their limits.

Publish both new Hosting packages using the existing preview-release workflow,
then pin those released versions in each consumer. The transitive targets supply
the JS module to browser publish output. The Office contract sample explicitly
uses source references for pre-release verification; Writer/Cell production heads
retain their released NuGet dependencies until the new packages are published.
W3/W4 will replace their duplicated page glue with this adapter and map their
document commands to `IDocumentPort`. Desktop adapters need no forced rewrite.

Synthetic composition/pointer cancellation cases verify the event contract, not
physical IME or touchscreen usability. Those remain W7 acceptance work, along
with real Safari, accessibility and device/network performance testing.
