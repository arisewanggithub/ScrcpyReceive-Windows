# Receiver protocol implementation

Interoperable with the ScrcpySend / Android ScrcpyRecv **v3** contract.
This document describes the unreleased hardware H.264/HEVC/AV1/audio preview.

## Discovery and control

The application starts listening on launch and closes its sockets when the
window closes; no manual Start/Stop buttons are required.
Discovery owns one long-lived, source-IP-bound UDP socket per current IPv4
interface. Removing an interface or closing the listener closes its socket;
individual advertisement completion does not close it.

Every two seconds, each active IPv4 interface broadcasts UTF-8 JSON to UDP
27183: `{"protocol":"screencopy","version":3,"name":"ScrcpyReceive-Windows","port":27184,"state":"available"}`.
The label is 1..80 characters; source IP is authoritative. Busy holds the
single negotiation/session slot; it never evicts another session. Discovery
failures are shown explicitly; manual TCP remains possible. A changed set of
interface addresses disconnects a cast. IPv6 discovery/listening is not yet
implemented in the application.

TCP_NODELAY, plain TCP. HELLO role sender/name/versions [3] at session 0,
receiver HELLO reply; positive signed-64 SESSION_REQUEST next. Unknown JSON
fields are ignored, never used as identity. Version arrays have 1..16 integers.
There is no authentication or encryption; session IDs are routing, not identity.
Capture must wait for SESSION_ACCEPT. This preview accepts custom TCP H.264, HEVC Main or AV1 Profile 0,
optional PCM/AAC-LC/Opus audio, auto receiver orientation, 1..60 FPS after
video hardware and audio-output preflight.
Other requests reject with UNSUPPORTED_CONFIG; no substitutions.
Control, configuration and media use the same TCP connection. A present
`mediaTransport` override is rejected with UNSUPPORTED_CONFIG regardless of its
value; it is never ignored or reinterpreted as TCP.

## Framing

Exactly 40 bytes, network byte order:

| Offset | Field |
|---|---|
| 0 | `SCPY` (4 bytes) |
| 4 | u16 version 3 |
| 6 | u16 message type |
| 8 | u32 payload bytes |
| 12 | u32 flags (1 only for video keyframe, otherwise 0) |
| 16 | positive signed-range u64 session (0 during HELLO) |
| 24 | u32 track: 0 control, 1 video, 2 audio |
| 28 | u32 generation: 1..2147483647 on tracked packets, otherwise 0 |
| 32 | i64 PTS microseconds: >=0 media, -1 otherwise |

Types: 1 HELLO, 2 reserved/rejected, 3 REQUEST, 4 ACCEPT, 5 REJECT,
6 STREAM_CONFIG, 7 STREAM_READY, 8 VIDEO_FRAME, 9 AUDIO_PACKET, 10 STATUS,
11 ERROR, 12 PING, 13 PONG, 14 STOP, 15 SESSION_END.
Header validation precedes payload allocation. Magic/version fail after the
first four/six bytes, not after a full header. Media must be nonempty.
Limits: video 4 MiB, audio 256 KiB, everything else 64 KiB.
JSON: strict UTF-8 object, maximum depth 32, no duplicate keys, malformed
surrogates, fractional/out-of-int32 numbers, coercion or nonfinite numbers.
Exact reads preserve segmentation/coalescing and reject truncated payloads.

## Configuration and readiness

Requested video h264/h265/av1; audio raw/aac/opus or disabled.
Video actual dimensions 1..16384; CSD canonical padded Base64 (Annex B for
H.264/HEVC, OBUs for AV1). PCM: 48000 Hz, stereo, s16le, empty CSD and
packet bytes divisible by four. Compressed audio ASC/OpusHead validation and
video bitstream validation remain backend responsibilities, before readiness.
The installed H.264 backend validates actual 8-bit Baseline/Main/High SPS dimensions,
including High-profile chroma format, bit depths and scaling lists,
PPS, hardware decoder configuration and resources before STREAM_READY.
HEVC validates Main profile, 8-bit 4:2:0 dimensions, VPS/SPS/PPS references
and first-slice I/P pictures. Leading/RASL and B pictures are rejected.
AV1 validates sized low-overhead OBUs, Profile 0/8-bit/4:2:0 sequence headers
and exactly one shown new frame; hidden/show-existing and spatial layers fail.
Each generation requires a real random-access picture (H.264 IDR, HEVC IRAP,
AV1 keyframe); key flags must match the bitstream.
Only I/P pictures are supported, and changed in-band parameter sets require
a declared new generation. Output must be D3D11 NV12, never software.

Initial enabled tracks must all be configured and actually initialized before
ordinary media. Each track's generations increment sequentially; no stale media.
PTS are nondecreasing except the exact accepted `opus` codec: Opus retains
nonnegative source PTS reversals and FIFO order. Never reorder or rewrite
timestamps. A future audio backend must map decoded sample positions to a
source-audio presentation accounting without rewriting packet timestamps.

Encoded queue bounds: the listener uses a stricter 4-MiB media budget
(3.75 MiB with audio), including packets held by track workers, reserving the
remaining aggregate/in-progress budget for reader and native packet copies.
Per-track max-minus-min queued PTS <=500 ms,
and stricter explicit 512 total queued entries. Configuration queue <=64
entries/256 KiB. One in-progress packet is additional; OS socket buffering is
separate. Overflow terminates with CONGESTION, never arbitrary media dropping.
Configuration/media preserve FIFO within each independent track, so a video
clock wait cannot prevent audio processing. Old data drains before reconfigure;
PING/PONG/STOP bypass the media queue via a serialized control writer.

## Lifetime

Setup deadline 10 s overall, control writer deadline 10 s, initialized sessions
send PING every 2 s and fail after 10 s without a complete valid incoming
packet. Nonces signed int32. Reader/writer/watchdog independent of media worker.
Configuration/presentation operations have a 10 s cancellation deadline.
The first actual hardware video presentation must occur within the startup
deadline; initialized tracks and PONGs alone do not satisfy it.
For TCP this initial deadline is fixed at ten seconds after acceptance,
regardless of repeated video or audio configurations. Presentation history is
retained across generations, so a later reconfiguration uses its normal
configuration/presentation deadline rather than reapplying initial startup.
STOP acknowledges SESSION_END and closes; local disconnect waits at most 2 s
before closing the socket. EOF/errors cancel owned workers and dispose playback.
At most 32 incoming control connections; excess connections close with a local
diagnostic. No reconnect, saved selection, remote input or automatic playback.
Hardware playback is destroyed and detached before acknowledging remote STOP.
The renderer retains at most one presented hardware frame for resize/redraw
and one latest decoded frame awaiting presentation; at most one
ordinary decoded picture is pending from each progressive VIDEO_FRAME.
The codec requests three extra hardware surfaces for application-held frames;
the actual initialized surface pool is reported and bounded at 64 surfaces.
Viewport redraw reuses the GPU surface without advancing media PTS/readiness.
Resize failures are checked by the independent watchdog, including static video.
The first real presented picture acquires a process-owned display/system power
request. It remains active during the cast, including static video and
fullscreen transitions, and is cleared when rendering stops or fails.
Idle discovery/setup never acquires it. This does not change Windows power
settings or prevent explicit user sleep/lock.

## Low-latency playback

STREAM_READY for track 2 follows validated format and initialized WASAPI/audio
decoder resources. Enabled tracks cannot disappear. Output is stereo s16le
48 kHz into Windows shared-mode mixing. AAC-LC is 48 kHz stereo, raw access
units with validated ASC/1024 or 960 samples. Opus is canonical OpusHead
v1/family 0, 48 kHz mono/stereo; manual pre-skip is applied once and the native
decoder applies header gain once. No ADTS/Ogg wrappers or substitutions.

Video does not wait for the audio clock. All encoded reference pictures are
decoded; superseded decoded pictures may skip presentation when newer video is
already pending. The newest decoded picture is published even when encoded
packets remain queued; presentation never requires that decoding catch up
completely with reception. Source timestamps and codec validation are unchanged.
Both decode tracks use dedicated serial workers. Admission may wait up to
500 ms for consumers to free space during a catch-up burst, but never inserts
above the existing byte/count/source-span limits. Oversized packets fail
immediately and sustained pressure remains CONGESTION.
Presentation runs independently of decoding. DXGI frame readiness is checked
without waiting, and Present uses DO_NOT_WAIT; a busy compositor retains only
the latest pending picture and retries it instead of blocking the track pump.
Resize/redraw requests are coalesced on the render thread, not executed on the
UI thread. A pending static picture is retried when presentation resumes.

Audio follows a scrcpy-style regulator with an adaptive 50-150-ms software
FIFO target, separate from a requested 20-ms WASAPI buffer (actual native buffer
must be <=50 ms). It starts at 50 ms and grows in 10-ms increments on actual
underruns, up to 150 ms. Each ten seconds of uninterrupted source playback
reduces the target by 10 ms toward 50 ms. This adaptation does not change
video pacing or encoded queue limits.
The FIFO keeps target plus 10 ms before playback, and 1.1 times target plus
60 ms while playing (maximum 225 ms), shedding oldest decoded output above
that threshold. Each decoded
access unit is still <=250 ms; temporary resampled output is bounded at 260 ms.
The regulator smooths average buffering, explicitly accounts for inserted
silence and dropped output, and applies FFmpeg resampling compensation within
2% over a four-second horizon, reassessed each second of produced samples.

A dedicated event-driven output thread supplies silence during underruns
instead of stopping the cast or holding video back. Actual presented source
audio is accounted separately: generated silence never satisfies audio
readiness or advances the source-audio counter. FIFO source observations,
including legal Opus PTS reversals, are not sorted or rewritten. This favors
freshness over strict A/V synchronization and may produce brief audible gaps.
Audio generation changes drain the converter, FIFO and native output within
500 ms before initializing the next generation. STOP cancels the output thread
and resets sound before acknowledgment. Device changes, native output stalls,
invalid media, decoder/resampler failures and encoded queue overflow remain
explicit errors; encoded queue limits are not increased.
