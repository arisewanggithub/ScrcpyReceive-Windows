# Native SRT interoperability probe

`--metadata-regression` exercises the managed MP4 admission guard without
starting SRT, invoking FFmpeg, or allocating declared decompressed data. It
rejects 22 direct/nested compressed-movie and unsupported-container cases,
including an empty mvex beside cmov, track containers, full-box metadata,
fragments, and all five supported sample-entry extensions. Supported opaque
metadata bytes are not scanned for text signatures or rejected accidentally.
`--metadata-avio-regression` also sends direct/nested compressed metadata over
isolated loopback SRT into the real custom AVIO path. Its declared expansion
is only 64 bytes, even if a future regression accidentally reaches native
decompression; it requires the explicit managed protocol rejection before
any track configuration is emitted.

This optional console project references the existing Windows sender Media project;
no sender implementation is copied. It launches only the supplied published
receiver's isolated `--srt-receiver-self-test` mode. It never captures screen/audio,
changes mute/settings, advertises on the LAN, or accesses Android.

Build, without shipping PDBs:

```powershell
dotnet build tests\ScrcpyReceive.SrtInterop\ScrcpyReceive.SrtInterop.csproj -c Release -p:Platform=ARM64 -p:SenderMediaProject=C:\path\ScrcpySend-Windows\src\ScrcpySend.Media\ScrcpySend.Media.csproj -p:DebugType=None -p:DebugSymbols=false
```

Run the built executable (or `dotnet` followed by its DLL):

```powershell
& .\tests\ScrcpyReceive.SrtInterop\bin\ARM64\Release\net10.0-windows10.0.26100.0\win-arm64\ScrcpyReceive.SrtInterop.exe --app C:\path\published\ScrcpyReceive.App.exe --fixtures C:\path\synthetic-fixtures --reports .\artifacts\srt-native-run
```

The default cases include `h264-video`, `h264-aac`, `h265-aac`, `av1-aac`,
`h264-rotate-aac`, `h265-rotate-aac`, `av1-rotate-aac`,
`h264-partial-rotate-aac`, and `h265-partial-rotate-aac`.
`--case <name>` selects one; `--case rotation` selects the five rotation cases.
Two loopback UDP loss cases are also included: `h265-loss-aac` suppresses one
data sequence and its retransmissions for 400 ms, then requires hardware video
to catch up and source audio to advance. `h265-gap-stop` keeps that sequence
missing and requires control STOP/SESSION_END and full native cleanup while the
SRT peer remains connected. The unpatched 1.5.7 runtime reproduced a bounded
deadline failure in the latter test. No system network settings are changed.
`--case opus` selects five synthetic Opus/fMP4 cases: H.264/Opus,
HEVC/Opus rotation, AV1/Opus, mono Opus with nonzero gain, and HEVC/Opus
packet-loss recovery. These use a test-only FFmpeg MP4 muxer and pinned libsrt
loopback listener, not the Windows sender's AAC-only muxer. They require the
additional labeled `synthetic-av-opus.json` fixture. No sender code is changed.
`--opus-demux-regression --fixtures C:\path\synthetic-fixtures` checks actual dOps
demuxing and native Opus decoding: 312 pre-skip samples removed exactly once,
6-dB gain applied once, stereo/mono channels, and rejection of Opus requested
as AAC. This does not substitute for actual WinUI/WASAPI playback cases.
Each case has a maximum 30-second budget, reserving
two seconds for cleanup. `--timeout-seconds` can only reduce that maximum;
`--play-seconds` defaults to three seconds after actual `srt-playing`.
Rotation cases require at least three post-readiness seconds.

The fixture directory must contain `synthetic-h264-generations.json`,
`synthetic-hevc.json`, and `synthetic-av1.json` for the selected cases. Each file
must explicitly declare `syntheticOnly: true`. Nonrotation cases repeat only
the first validated, fixed-configuration GOP at 30 FPS with monotonic PTS.
Rotation cases send all three generations, switching every 32 frames at a
real keyframe: landscape, portrait, landscape. Their GOP lengths must divide
32. The actual sender muxer keeps the original initialization; new parameter
sets/sequence headers travel in-band with new pictures. Partial-update cases
omit parameter sets unchanged from the previous generation, exercising cached
VPS/PPS/SPS linkage rather than requiring complete replacement configurations.
PCM is generated silence: 48 kHz, stereo, s16le; actual sender `SrtMedia` encodes
it to AAC-LC and muxes the actual fMP4/SRT bytes.

The harness uses the actual sender protocol serializer for HELLO, SESSION_REQUEST,
heartbeats, and STOP. It requires exact SRT acceptance/tracks, rejects TCP
STREAM_READY/media, waits for real receiver `srt-playing`, continues media,
then cancels the source, calls sender Abort, sends STOP immediately, and requires
the matching SESSION_END.

The app must write `<report>.ready.json` with `{ "port": <ephemeral TCP port> }`.
After STOP it must exit and write `<report>` with:

- `passed`, `syntheticOnly`, `displayAwakeDuringPlayback`, `detachedAfterStop`,
  and `displayReleasedAfterStop`: all true.
- `hardwareDecodedFrames >= presentedFrames >= 2`.
- `playedSourceAudioFrames`: zero without audio; at least half the generated
  post-readiness interval with audio (72,000 frames for the default three seconds).
- `failure`: optional failure detail.
- `configuredVideoGenerations`: one for fixed-GOP cases, three for rotation,
  proving real decoder reconfiguration without duplicate or stale-moov generations.
- `presentationClockFrequency` and `presentationSamples`: bounded, monotonic
  samples of actual presentation count and last presented source PTS. The
  harness records its actual `srt-playing` receipt and pre-STOP boundaries
  using the same machine-wide Stopwatch clock. Samples must cover that active
  interval, with both count and source PTS advancing in each approximately
  one-second segment, including the final segment. Startup pictures alone,
  advancing decoding/audio, or old-picture redraws cannot satisfy this check.
  Loss-recovery cases must also present new source pictures after the missing
  sequence has actually been forwarded, with at least half a second of active
  playback left to observe. Only `h265-gap-stop` intentionally permits frozen
  output: its missing sequence is never released, and it tests cancellation,
  not continued playback.

The shared presentation-progress validator is also compiled into the existing
unit-test project. Its ten regressions reject startup-only/late freezes,
unchanged PTS or presentation counts, missing tail samples and unproven loss
recovery; successful progress and the deliberate pending-gap STOP case pass.

Reports are never overwritten. The output directory contains native JSON,
bounded stdout/stderr logs, and `interop-summary.json`. Failures produce exit
code 1. Cleanup terminates only the process/PID launched for that case, never
unrelated receiver or sender instances.

A faster, explicitly non-UI regression exercises actual sender SRT/fMP4 bytes
against the receiver demuxer directly, including both partial-update cases:
`ScrcpyReceive.SrtInterop.exe --demux-regression --fixtures C:\path\synthetic-fixtures`.
It checks three exact dimensions/generations, continued pictures, and AAC packets.
This does not replace the published-app/native-renderer cases above.
