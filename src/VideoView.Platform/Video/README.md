# Windows Graphics Capture

`CaptureTargetCatalog.List()` reads Win32 metadata only. It lists monitors plus visible,
uncloaked top-level windows outside the current process (excluding the desktop shell).
Titles and native handles are snapshots; refresh the catalog when selecting a target.
Enumeration and `ScreenCaptureSource` construction do not capture anything.

`Start()` creates exactly the selected HWND/HMONITOR through
`IGraphicsCaptureItemInterop.CreateForWindow/CreateForMonitor`. It starts a free-threaded,
two-buffer BGRA8 WGC frame pool with cursor capture enabled. The normal Windows capture
border can be hidden through the explicit Windows borderless-capture permission flow;
denial keeps capture working with a visible border. There is no picker, whole-screen fallback, GDI fallback, or
implicit target substitution. Setup failures throw from `Start()`.

A dedicated MTA thread owns capture resources, copies each acquired D3D11 texture into
a CPU-readable staging texture, and removes GPU row padding. `LatestFrame` publishes one
complete `VideoFrame` atomically. `Pixels.Length == Width * Height * 4`, stride is
`Width * 4`, and channels are B, G, R, A. Each array belongs to its snapshot and is never
modified, pooled, or reused by the producer. Callers must treat the exposed `byte[]` as
read-only; copy before editing. Multiple readers may keep frames after Stop/Dispose.
Only one latest CPU frame is retained by the source; slow readers do not create a queue.
Readers retaining old frames are responsible for their own memory use.

**Recorder integration:** frame dimensions can change during a run. A changed positive
`ContentSize` recreates the existing frame pool after the acquired frame is released.
Valid shrinking-frame content is published before recreation, including for static windows.
Zero-sized frames temporarily retain the last image. The selected item does not change.
If a static window supplies no complete new-size frame after 250ms of inactivity, the same
item is reconnected through a fresh pool/session. The last snapshot and recording clock remain valid.
`RecordingSession` uses `FrameCanvas` to fit new images into the encoder's original dimensions
with bilinear scaling and opaque letterboxing; audio and capture timestamps keep running.

Target close/disconnection, device loss, and readback failures clear `LatestFrame`
and raise `Faulted` once for that run, on a thread-pool thread after native cleanup.
An idle-time native-handle check also detects a destroyed window/disconnected monitor
if Windows delays its `Closed` event.
Marshal UI updates to the UI thread. A handler may call Stop/Dispose. Stopping/restarting
suppresses obsolete queued fault notifications. Start and Stop are serialized and
idempotent while already running/stopped; a disposed source cannot restart. Sequence
numbers increase across successful frames and restarts of the same source.

Limitations: hardware D3D11 and WGC support are required. Protected/excluded content may
be black; minimized windows or inactive desktops may stop delivering frames without a
close event. `Start()` confirms session setup, not first-frame arrival: the recorder must
wait for a non-null `LatestFrame` with its own startup timeout. BGRA8 is SDR; HDR tone
mapping is not implemented. CPU readback and one owned allocation per captured frame
have a bandwidth/GC cost, especially at 4K. Capture does not impose an output frame rate;
the recorder samples `LatestFrame` at its own cadence. Native handles can be reused;
catalog IDs reject cross-process HWND reuse and changed monitor device names, but are
not persistent window identities.

Validation: `VideoView.Tests.VideoTests.NativeSmokeAsync(hwnd, assert)` captures only a
dedicated window owned by the test process. It verifies frame layout, concurrent reads,
ownership after Stop, restart sequence, continuous grow/shrink/restore, and close faults. It resizes and
closes that window; keep its UI message loop running during the test. The optional
`Video/Validation/SmokeTest.ps1 -Capture` host uses an offscreen owned native window.
Without `-Capture`, that script only enumerates metadata. Run outside restricted capture
sandboxes; PowerShell 7.6+ and a built Platform assembly are required.

Interop reference: [Microsoft IGraphicsCaptureItemInterop documentation](https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nn-windows-graphics-capture-interop-igraphicscaptureiteminterop).
