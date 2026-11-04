# ScrcpyReceive-Windows privacy - unreleased audio/video preview

Updated 2026-11-03. This statement covers the current unreleased Windows
receiver audio/video preview, not the Windows sender or Android applications.

- No account, analytics, advertising, developer telemetry, automatic update
  check, crash upload or cloud streaming service.
- Listening starts automatically when the app is opened. It exposes TCP 27184 on IPv4 interfaces
  and broadcasts the receiver label, protocol version, port and availability
  every two seconds over local UDP 27183. It displays local IP addresses and,
  when several are available, local network-adapter labels. Adapter labels
  remain in memory and are not included in discovery advertisements.
- The advertised label is the product name, not a computer or user name.
- Incoming peer addresses, HELLO labels and requested stream settings are
  processed in memory. Compatible custom TCP H.264, HEVC or AV1 casts are decoded
  on the GPU and displayed in the window or fullscreen. Video stays in bounded
  memory; the app does not save recordings or upload it to a developer service.
- Optional PCM, AAC-LC or Opus audio is decoded in bounded memory and played
  through the default Windows multimedia output. The app does not record it
  or upload it to a developer service. It does not change system mute/volume;
  output-device changes stop the cast rather than silently rerouting audio.
  Short delivery gaps may produce recovery silence; stale decoded output may
  be discarded to keep buffering bounded. These operations happen in memory
  and do not record or upload media.
- Control and media share the accepted TCP connection. Transport overrides and
  arbitrary media URLs/files are not accepted. No microphone, screen capture,
  remote input or clipboard service.
- Diagnostics, including remote error messages and networking failures, remain
  in a bounded in-memory list; the UI shows actionable error summaries.
  The normal app does not save logs,
  settings, addresses, media, credentials or session trust to disk.
  Explicit developer self-tests may write a report only to the caller's
  requested report file. Review/redact any report before sharing it.
- Discovery/control/media are unauthenticated and unencrypted. Anyone on the LAN can
  observe or forge traffic; no pairing or identity assurance is provided.
  Use a trusted private network only and do not forward ports to the internet.
- Close the app to close its sockets and release its resources.
  Actual playback temporarily requests that Windows keep the display/system
  awake; the request is released when playback ends. No power-plan settings
  are changed and idle listening does not keep the PC awake.
  It does not start with Windows or install a background service. The application does
  not change firewall/security settings or capture the PC's screen/microphone.
- An optional, explicitly owner-invoked administrator helper can correct only
  this executable's local Private/LocalSubnet firewall access. The app never
  invokes it automatically. Public-network rules and unrelated applications
  are not changed, and the firewall is never disabled.

Windows, Microsoft Store and runtime components operate under their own
policies. Website hosting and public GitHub issues are separate services; do
not post private addresses or sensitive diagnostics publicly.

This page must be reviewed and updated when playback scope, storage,
dependencies or logging behavior changes, and before any receiver Store
submission. It is not a declaration that the app is released or certified.
