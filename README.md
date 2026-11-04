# ScrcpyReceive-Windows

Independent native WinUI 3 receiver companion for the existing Android
ScrcpyRecv and Windows/Android ScrcpySend applications. Not an official scrcpy
application. Windows 11 22H2+, native ARM64 first and x64; .NET 10 and Windows
App SDK 2.6.5 (the stable patch reported by the configured NuGet feed; the
inspected Windows sender pins 2.6.4).

## Current milestone: hardware H.264 / HEVC / AV1 and audio preview (unreleased)

**Playback policy:** scrcpy-style low latency replaces strict audio-master
gating. Previous real Android Raw-audio casts stopped after repeated delivery
gaps of about 219 ms emptied the old 100-ms buffer. The new policy tolerates
short audio gaps, sheds stale decoded output and does not hold video behind
audio. Physical stability of the revised policy still needs acceptance testing.
Failure details include per-track arrival ages/gaps, read waits, receiver
handling time, inserted recovery silence and discarded decoded samples.
Peer-reported fatal errors also retain the receiver's own diagnostic snapshot:
partial TCP payload progress, unread socket bytes, queue occupancy/in-flight
tracks, and active/maximum read, admission, video, audio and control-write
durations. Only the eight most recent operations taking at least 100 ms are
retained, in memory, alongside per-worker maxima; no media payloads are logged.
These distinguish local processing/backpressure from waiting for input without
assuming that a sender write stall proves network loss.
When a socket read makes no progress for 250 ms, the watchdog samples read-only
Windows TCP counters at most four times per second, retaining eight snapshots.
These include receive-window/buffer size, kernel bytes received and reordered
bytes. Local retransmission counters describe receiver-to-sender control
traffic, not the sender's video retransmissions; zero local retransmissions
must not be presented as proof of a lossless incoming stream.
No media is retained by diagnostics; encoded queue limits remain unchanged.
The first physical low-latency trial kept audio healthy but still exposed video
backpressure. The current revision also decouples GPU presentation, uses
DO_NOT_WAIT, gives decoding a dedicated worker and bounds burst admission.
GPU stage, decoder cost and native-gate wait diagnostics distinguish any
remaining hardware/presentation pressure. The owner subsequently confirmed
working video and audio, but reported intermittent audio cuts. Adaptive audio
quality acceptance remains pending.
Decoder diagnostics now separate validation, packet copying, native submission,
output retrieval, managed GC pauses and actual hardware surface count.
Three additional hardware surfaces are requested for application-held GPU
frames; they do not add a timed playback buffer.

- Custom TCP **8-bit H.264 Baseline/Main/High, HEVC Main, AV1 Profile 0, optional PCM/AAC-LC/Opus audio, auto presentation, 1..60 FPS**.
  The hardware D3D11 device must support the selected codec with NV12. FFmpeg D3D11VA selects
  only hardware surfaces; a software pixel format fails, never falls back.
  GPU video processing converts NV12 to an aspect-fit WinUI swap chain.
  No WARP device, CPU video readback or software decoding path is selected.
- Audio is 48 kHz: stereo s16le PCM, stereo AAC-LC, or mono/stereo Opus.
  AAC/Opus use the pinned FFmpeg audio decoders; audio sample conversion and
  Windows shared-mode mixing are not software video fallback. Actual audio
  rate/channel configuration is validated, Opus pre-skip is applied once per
  generation and OpusHead gain is applied once. No codec/audio-off substitution.
- WASAPI renders to the default multimedia output without changing system
  volume or mute. An adaptive 50-150-ms software FIFO target is independent from a requested
  20-ms device buffer (actual device buffer must be <=50 ms). A dedicated
  event-driven output thread inserts silence on underrun; the cast continues.
  It starts at 50 ms, grows in 10-ms increments when output underruns, and
  shrinks 10 ms after each ten seconds of uninterrupted source playback.
  This is an adaptive extension of the scrcpy-style regulator, not a fixed
  increase in latency. The FIFO sheds oldest decoded samples above
  1.1 times the current target plus 60 ms (at most 225 ms); startup headroom
  is the target plus 10 ms. FFmpeg resampling regulates the current target within +/-2%.
  Device changes, output-consumption stalls, decoder and resampler failures
  remain explicit. Generated silence does not count as played source audio.
  Opus pre-skip/gain and FIFO source-PTS reversals remain unchanged.
- Video is presented immediately, independently of the audio clock.
  Every encoded reference picture is decoded; superseded decoded pictures
  may skip presentation when newer video is already pending. This favors
  freshness over strict A/V synchronization during network jitter.
  An independent renderer uses nonblocking DXGI presentation and retains only
  the latest pending decoded picture when the compositor is busy. Window
  resize/redraw work does not run on the UI or network/decode thread.
  Presentation continues while encoded packets remain queued; it does not
  wait for decoding to catch reception completely.
  Both track pumps run on dedicated serial workers. Catch-up bursts receive
  at most 500 ms of admission backpressure without enlarging encoded limits;
  an irreducibly oversized packet or sustained overload still fails explicitly.
- Actual SPS dimensions, High-profile chroma/bit-depth/scaling-list syntax, real IDR/key flags, I/P-only slices, parameter sets,
  generations and hardware output are validated. Reconfiguration drains the
  old decoder before creating the next generation; unsupported profiles,
  interlacing, HDR color matrices, B/SP/SI pictures and unannounced CSD changes
  fail explicitly.

- Automatic listening on app launch, actual IPv4 endpoints, UDP v3 discovery on
  27183, TCP control on 27184, one active negotiation and authoritative BUSY.
- Strict 40-byte SCPY framing, bounded allocation/JSON, setup/read/write
  cancellation, PING/PONG and STOP over the same TCP connection.
- Ordered track generations, readiness barriers, PCM alignment and Opus PTS
  exception; receiver queue bounds enforced before insertion.
- Passive WinUI receiver: no start/stop/disconnect/probe/fullscreen buttons.
  Close the window to stop listening and release the cast. During actual video
  playback, **Enter or F toggles fullscreen; Esc exits fullscreen**. Idle,
  text-editing and modified Enter/F keys are not intercepted.
  The waiting page disappears after the first real video frame; both windowed
  and fullscreen playback show only the player. Esc returns to windowed video,
  not to the waiting page. The waiting page returns when casting ends.
  Shortcuts use preview/tunneling key routing so child controls cannot consume
  Enter before the player sees it.
- The borderless waiting screen uses the full page: branding toward the top,
  a compact readiness badge and addresses in the main area, and a footer at the bottom.
  It shows
  a large selectable endpoint for a single address. Multiple addresses use
  separately labeled adapter rows; window width, height and address count adapt
  typography, icon size and spacing. Addresses wrap rather than shrinking
  arbitrarily, and long lists remain vertically scrollable. Friendly states distinguish
  starting, connecting, missing network, stopped playback and listener failure.
  No About or Connection details panels clutter the home page. A small footer
  shows the installed package version, or explicit application build metadata
  when unpackaged; bounded diagnostics
  remain in memory for developer self-test reports. The navy/mint dark palette, light palette and
  high-contrast system colors follow Windows; the native title bar follows
  the current app theme. Fullscreen/player-only behavior remains unchanged.
- Bounded in-memory connection details, visible startup/discovery errors and
  actual hardware decoder preflight before accepting a compatible request.
- DPI-aware logical launch sizing, monitor work-area bounds, minimum sizes,
  scrollable small-window controls, composition-scale-correct GPU surfaces
  and immersive video fullscreen with an Escape/exit control.
- One presented GPU-frame reference is retained for static-video resize/fullscreen
  redraw, plus at most one latest decoded picture awaiting the compositor.
  Redraw does not advance source timestamps or count as a new media frame;
  no CPU readback is used. GPU resize failures are reported by the independent
  session watchdog even when no new video arrives.
- Actual video playback owns a Windows display/system power request, including
  windowed, fullscreen and static-picture playback. Idle listening does not
  keep the PC awake. STOP, disconnect, playback failure and app close release
  the request; no power-plan settings are changed. Explicit user sleep/lock
  actions are not overridden.

## Discovery and Windows Firewall

Opening the app starts TCP 27184 and sends v3 advertisements to UDP 27183 every
two seconds. There is no Start button. The window shows the actual addresses.
Per-interface UDP sockets remain bound for the listener's lifetime, matching
the Android receiver's ownership pattern. Closing a UDP socket immediately
after each send prevented advertisements reaching the Android sender on the
tested Windows device. Sockets are retired on interface removal or shutdown.
The sender must be on the same reachable trusted LAN. Firewall, guest Wi-Fi,
client isolation and VPN routing can still block LAN traffic.

Windows Firewall rules apply to the **exact executable path**. An allow rule
for an old build does not allow a newer executable in another folder, and an
explicit block wins over an allow. Keep a stable install path. The app never
changes firewall rules automatically.

If Windows explicitly blocked this receiver, review its Private-network access
in Windows Security. An optional, owner-invoked administrator helper can remove
only matching local Private TCP/UDP block rules and allow only this executable:

```powershell
.\tools\EnablePrivateNetwork.ps1 -ExecutablePath .\artifacts\receiver-preview-arm64\ScrcpyReceive.App.exe
```

It restricts inbound TCP 27184 and outbound discovery UDP 27183 to
**Private/LocalSubnet**, leaves Public-network and other applications' rules
unchanged, and does not disable the firewall or override organization policy.
- Self-contained, untrimmed ARM64/x64 publication and unsigned MSIX validation
  using an explicit identity. No PDBs, Store credentials or submission.

**Select H.264, HEVC/H.265 or AV1 / custom TCP; audio may be enabled.**
TCP media uses explicit configuration generations. A present `mediaTransport`
override is rejected before accepting capture; external media URLs are not used.
Forced receiver-orientation requests still receive
`UNSUPPORTED_CONFIG` before acceptance. There is no codec, transport or
audio-off substitution. Unsupported actual codec configurations fail explicitly
before STREAM_READY. Hardware initialization failure rejects before capture.
The first actual video presentation has a 10-second startup deadline; a ready
codec and live PONGs do not count as rendering. Repeated video or audio
configurations cannot extend this initial deadline. Later reconfiguration
retains the session's actual presentation history.

Use ONLY on a trusted private LAN: the compatible protocol is unauthenticated
and unencrypted. Do not expose ports to the internet. Firewall permissions are
not changed automatically. See [privacy](PRIVACY.md) and [protocol](PROTOCOL.md).

## Build and validate

Install the .NET 10 SDK on Windows. The app restores pinned WinUI/Windows SDK
build tools from NuGet; a separate C++ installation is not needed.

```powershell
dotnet test .\tests\ScrcpyReceive.Tests\ScrcpyReceive.Tests.csproj -c Release
.\tools\PrepareNative.ps1 -Architecture arm64
dotnet build .\src\ScrcpyReceive.App\ScrcpyReceive.App.csproj -c Release -p:Platform=ARM64 -r win-arm64
dotnet run --project .\src\ScrcpyReceive.App\ScrcpyReceive.App.csproj -c Release -p:Platform=ARM64 -r win-arm64
.\tools\Publish.ps1 -Architecture arm64
.\tools\PrepareNative.ps1 -Architecture x64
.\tools\Publish.ps1 -Architecture x64
```

Use `-p:Platform=x64 -r win-x64` for an x64 build. Publish/package output must
be new or empty to preserve previous work. An unsigned executable may be
blocked by Windows policy; do not disable protections to run it.
The default source build version is set in `Directory.Build.props`.
For a release, pass `-Version MAJOR.MINOR.PATCH` to `tools\Publish.ps1` and
the matching `MAJOR.MINOR.PATCH.0` to `tools\Package.ps1`. Packaging refuses
different assembly/package versions. The footer reads package identity when
installed and build metadata when unpackaged; nonzero package revisions remain visible.
See [Store preparation](STORE.md); this limited preview is not ready for Store
submission. No existing sender Store product is used or modified.

Optional paired-source integration tests reference an existing Windows sender
checkout without copying its source. Set `SCRCPYSEND_CORE_PROJECT` to its
`ScrcpySend.Core.csproj`, then run:

```powershell
dotnet test .\tests\ScrcpyReceive.SenderInterop\ScrcpyReceive.SenderInterop.csproj `
  -p:SenderCoreProject=$env:SCRCPYSEND_CORE_PROJECT -c Release
```

These execute the actual sender's negotiation using test-only video/audio
sources and assert rejection precedes capture and audio suppression for all
three video codecs. They do not capture a real display or exercise real mute.

### Executed locally

- The TCP unit/loopback/UI-policy suite covers bounded activity/kernel-counter history, partial-read progress and receiver evidence on sender fatal errors, adaptive single/multiple-address layout, local-only adapter labels, friendly receiver states, theme/layout structure, transport-override rejection before playback, endpoint presentation, HEVC/AV1 profiles/bitstreams, playback power-request ownership/release, remote STOP retirement, adaptive audio target bounds/growth/shrink, queue dwell timing, bounded burst admission/cancellation, per-track arrival/read diagnostics, audio configuration/output-clock invariants,
  independent track scheduling, High-profile scaling/chroma/bit-depth validation,
  GPU-health failure propagation, UDP socket reuse/shutdown, real loopback
  sockets, keyboard/button-free UI policy, bitstream validation,
  a 4-MiB parser allocation bound, 100%-300% DPI placement, a measured
  10-second trickled-setup deadline and 2-second unacknowledged STOP budget.
- Seven paired-source tests passed against the actual Windows sender:
  H.264/HEVC/AV1, with and without PCM audio; no capture/transmission started.
  The additional test uses its actual UDP discovery implementation to accept
  the receiver's v3 packet and preserve the UDP source address.
- Actual Android sender diagnostics confirmed reception and acceptance of
  this PC's discovery packets (`receivers=1`, no rejections). A control-only
  phone-to-PC HELLO/STOP completed without requesting capture. Local Private
  firewall blocks for the installed receiver were corrected only after
  explicit owner approval; Public-network access was not enabled.
- Native HEVC Main and AV1 Profile 0 checks decoded 24 pictures each across
  three orientation generations. Each codec also decoded 600 full-resolution
  pictures with PCM audio, recurring 250-ms jitter and window occlusion.
  Latest-picture presentation, STOP cleanup and display-power release passed.
  These were hardware-encoded synthetic sources; physical Android HEVC/AV1
  acceptance remains pending.
- Native H.264, HEVC and AV1 backlog regressions each decoded/presented 32
  pictures while one already-observed encoded picture remained pending;
  four intermediate checkpoints confirmed continuing presentation.
- TCP lifecycle regressions enforce the immutable first-video deadline.
  Repeated video/audio configurations
  cannot hold an unstarted cast indefinitely; a previously presented picture
  remains recorded across later generations.
- A clean TCP-only publication passed eleven native checks: UI/listener
  lifecycle, H.264 and High-profile playback, HEVC, AV1, PCM/AAC/Opus audio,
  and encoded-backlog presentation for all three video codecs. ARM64/x64
  packaging permits only the decoder/audio native libraries and their notices;
  extra native runtime assets fail publication or packaging.
- Native UI checks also verify full-page waiting geometry and bottom footer, light/dark
  palette updates, actual build-version footer, friendly rejection text and
  reachable addresses in a small high-DPI window. High-contrast resource
  structure is unit-tested; system high contrast is not enabled automatically.
- Adaptive native checks use synthetic loopback endpoints to verify readable
  multi-adapter rows and scrolling in a compact window, then transition through
  no-network and single-address connecting states without changing network settings.
  Adapter labels are local UI metadata only; discovery wire packets are unchanged.
- Initial address-enumeration failure ends the startup indicator and shows
  network/reopen guidance while preserving the open TCP listener. Transient
  broadcast warnings remain distinct; known addresses can still be entered
  manually after discovery stops. UI-state regressions and a native view check
  cover the terminal failure without changing system network settings.
- The waiting page is arranged directly by its ScrollViewer viewport, without
  rewriting minimum height during resize. Adaptive metrics are applied only
  when they change. Native checks exercise repeated maximize/restore and verify
  restored window size, viewport fit and footer placement without stale overflow.
- Native ARM64 WinUI tests cover automatic listening, real endpoint display,
  loopback HELLO/rejection, playback-gated Enter/F/Esc and owned shutdown.
  Native video-only/audio and one-minute playback checks also verified Windows
  display/system power-request acquisition, retention through fullscreen and
  static redraw, and release after STOP. Idle listening acquires no request.
- A real ARM64 TCP-to-hardware-decoder-to-WinUI test decoded/presented all
  24 generated frames across three landscape/portrait/landscape generations,
  fullscreen transitions, composition scaling and STOP cleanup. Measured
  launch client size: **1040 x 820 DIPs at 250% display scaling**, with
  scrollable overflow. The fixture contains generated gray frames only;
  no desktop, phone or audio was captured.
  Baseline and 8-bit High-compatible synthetic fixtures both pass; tests also
  verify player-only transitions, static-frame resize redraw without media
  advancement, and waiting-page restoration after STOP.
- Real WASAPI PCM/AAC/Opus tests passed with generated low-volume audio.
  Earlier strict-clock builds measured synthetic A/V skew below 32 ms; this
  is not a latency or synchronization claim for independent playback.
  Opus tests verified exactly 312 pre-skip samples, approximately 1.995x gain
  for a 6 dB OpusHead change, nonnegative PTS reversals and generation restart.
- The low-latency revision passed native PCM/AAC/Opus and three-generation
  High-profile checks. Nominal synthetic source-PTS skew was about 54-71 ms.
  A 30-second 1088 x 1920 / 60-FPS hardware check with 32 recurring 250-ms
  delivery pauses decoded all 1800 pictures, presented the latest picture,
  shed 591 superseded presentations and recovered from underruns without
  fatal congestion. Recovery silence was observed while sources were active,
  not merely after EOF. STOP detached the surface and released audio.
  The same check hid the window for one second: 61 hardware pictures were
  decoded while hidden, and the latest picture appeared after restoration.
  These are synthetic results, not physical phone playback validation.
- A one-minute decoder-stage check decoded all 3600 hardware pictures through
  62 quarter-second delivery pauses and window occlusion. The actual hardware
  pool contained 23 surfaces. Maximum measured native submission was about
  39 ms, output retrieval 0.15 ms, validation 4.1 ms and GC pause 2.3 ms.
  The previously observed ~995-ms physical stall was not reproduced by this
  generated fixture; stage and queue-dwell diagnostics are retained for the
  next physical run. No larger encoded queue or admission timeout was applied.
- Adaptive audio stayed at 50 ms on nominal PCM/AAC/Opus tests and reached
  150 ms on the same one-minute jitter fixture. Generated recovery silence
  decreased from 523827 to 196169 frames (about 63%); this is a synthetic
  missing-input metric, not a physical listening-quality claim. All 3600
  hardware pictures decoded, Opus pre-skip/gain/reconfiguration remained
  correct, and STOP detached/reset output without a retirement health error.
- ARM64 and x64 self-contained publications and unsigned MSIX schema,
  identity, primary native-PE architecture and no-PDB checks passed.
- Shared site's four static pages and their local links validated.

GitHub CI builds/tests and validates packages for ARM64 and x64. The x64 UI has not been
launched on an x64 device. The owner confirmed real Android H.264 video and initial Raw audio.
Sustained physical playback of the low-latency revision, receiver Store
reservation/submission and website deployment remain unverified/unperformed.

The reproducible synthetic fixture is in `tests\ScrcpyReceive.Tests\fixtures`.
Run the published app with
`--video-self-test <synthetic-fixture.json> <new-report.json>` to test actual
hardware rendering on an interactive Windows hardware device.
`--self-test <new-report.json>` checks the UI/listener lifecycle.
These self-tests use an isolated loopback listener on an ephemeral port with
LAN advertisement disabled; they do not conflict with a running receiver or
send fake test receivers to other devices.
Reports are explicit developer opt-in output, not normal application logging.
An optional synthetic fixture `networkJitterMs` field (200..400, audio required)
pauses the shared test transport periodically and then sends catch-up bursts.
It verifies underrun recovery while sources are active, decoding of every
encoded reference picture, latest-frame presentation and STOP cleanup.
Reports separate decoded/presented/skipped frames, generated silence and
discarded decoded audio. They do not establish physical sender acceptance.
`--video-backlog-self-test <synthetic-fixture.json> <new-report.json>` tests
actual hardware rendering while holding one already-observed encoded picture
pending throughout decoding. Four checkpoints require the displayed source
timestamp to advance before the encoded backlog empties.
The generator's `--occlusion` option also hides the test window for one second
and verifies that decoding continues and the latest picture appears afterward.
The optional `tests\ScrcpyReceive.SyntheticGenerator` project can regenerate the
fixture using the actual Windows sender's hardware encoder by supplying
`-p:SenderMediaProject` and a new output file. It never captures screen/audio.
Use `--long --audio=raw --jitter=250` for repeated quarter-second stalls and
catch-up bursts during a 30-second, full-resolution 60-FPS hardware check.
Add `--duration=60` for a one-minute run.
Its optional `--high` test mode produces a valid 8-bit High-compatible subset
from those generated pictures by extending synthetic SPS syntax. It never
rewrites production media or changes sender settings.

## Playback phases and acceptance gates

The decoder takes each queued packet immediately on its dedicated worker.
There is no playback-rate sleep in decoding. FFmpeg 7.1.1's
`avcodec_send_packet` can perform decoding itself before returning;
`avcodec_receive_frame` retrieves buffered output and is drained to EAGAIN.
Sending more packets before reading output does not bypass a blocked native
submission, and concurrent calls on the same codec context are not safe.
Received D3D11 frames remain GPU surfaces, not CPU-readback/completion waits.

1. **Foundation (implemented):** strict protocol, listener, bounded lifecycle,
   native shell, tests and packaging.
2. **Custom TCP hardware video (H.264/HEVC/AV1 preview implemented):** hardware-only
   FFmpeg D3D11VA, a hardware D3D11 device and GPU video processor replace
   the initially considered Media Foundation decoder route, because a
   hardware-only MF registration query does not cover all DXVA decoders.
   The get-format callback refuses software formats and every output must
   be a real D3D11 NV12 frame. HEVC requires 8-bit Main/4:2:0; AV1 requires
   8-bit Profile 0/4:2:0. Main10/HDR, scalable/spatial layers and reordered
   pictures are not supported. Continue real Windows/Android sender device
   acceptance. No substitution.
3. **Low-latency audio (preview implemented):** PCM, AAC-LC and Opus with a
   bounded FIFO, independent event-driven WASAPI output, recoverable underruns,
   decoded-output overflow shedding and at most 2% resampling compensation.
   Video remains immediate rather than audio-master gated. Source-order Opus
   observations and pre-skip/gain are preserved. Actual source-audio counters
   exclude generated silence. Continue physical long-duration acceptance.
4. **Device/Store acceptance:** actual Windows and Android sender playback,
   static video with continuing audio, stop/disconnect/recast, no leaked
   workers, hardware failures, both architectures, signed package validation,
   accessibility and owner-reviewed privacy/licensing/Store declarations.

Broader device/codec acceptance, audio synchronization, x64 hardware playback
and Store certification are **not verified**. Basic real Android H.264 video,
player-only presentation, and Enter/F/Esc were confirmed by the owner.
No HEVC capture/Stop safety test should be automated on devices with the
reported Android kernel-panic risk.

## References and source boundaries

Implementation was derived independently from the paired v3 wire contract and
inspection of Windows sender `Protocol`, `SenderSession`, packaging and Android
receiver `ReceiverServer`, `Configuration` and track/readiness contracts.
No sender/Android implementation source or personal configuration is vendored.
Native libraries are downloaded from the same public checksum-pinned LGPL
release used by the sender, not copied from private build directories.
Bundled dependencies and source provenance are in `THIRD_PARTY_NOTICES.txt`.
Android reference remains **ScrcpyRecv**; the Windows
project/product is consistently **ScrcpyReceive-Windows**.

- [Windows sender](https://github.com/arisewanggithub/ScrcpySend-Windows)
- [Android receiver](https://github.com/arisewanggithub/ScrcpyRecv)
- [Shared website](https://screencopy.redrain.ccwu.cc/)

## License

The project's own source is licensed under the [MIT License](LICENSE).
Third-party components retain their respective licenses and redistribution
requirements; see [dependency notices](THIRD_PARTY_NOTICES.txt).
