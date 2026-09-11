# Audio update, 2026-09-09

Deployed at approximately 16:42 CEST:

- Librespot development revision `a1b66d3c8a14e55a9572a9e17467150dca618c9a` with recovery PR 1692 at `b08a5f77df17dddfa7fb7869c56a6c5b59c8512b`, built with Rust 1.98.1.
- Snapserver 0.35.0 with the local stream lifecycle patch, image `b1a7f93d6bd6ccab87ffe5ef311c65cf4cc35b14c5590d83a86d7ff56bcd73f1`.
- Snapclient 0.35.0 with PulseAudio support on HiFiBerry. Its configured host ID, ALSA output, and server address were preserved. The package's new service account owns its existing state directory.
- Mosquitto 2.1.2 and the current SyrenServer build on .NET 10.0.12.

The live image's version manifest matched `versions.json` and both patch hashes. All three containers and the receiver were running without restarts during the deployment check. The speaker reconnected with the same ID. Snapcast group and stream IDs were preserved, and the saved Syren configuration matched the backup exactly. Laptop audio and the RTP broker remained available.

Spotify's discovery endpoint returned status 101 and the name SyrenSystem, and its multicast advertisement was visible. The new session had no active Spotify user and was waiting for selection. Neither source was playing during the deployment check. This verifies installation and discovery, not audible playback recovery after a disconnect.

## Regression evidence

The image build passed all 26 librespot workspace tests. The native Snapserver smoke passed 12 source activity transitions and 31 priority stream removals with continuing synthetic PCM. Unpatched 0.35.0 crashed when removing a stream. Fixing the deleted iterator alone exposed another crash when subsequent PCM or activity reached a deleted meta stream. The final patch retains listener ownership during callbacks and removes listeners when the meta stream stops.

The full sibling audio gate passed at `SyrenApp/build/audio-gate/20260909T144120522473Z/report.json`: 109 Flutter tests, Flutter analysis, 70 receiver tests, 33 measurement tests, 43 routing assertions, 79 server tests, and 24 real PipeWire handoffs. Maximum measured branch control time was 56.45 ms and maximum captured digital silence was 29.33 ms. The PipeWire check uses synthetic sources and a virtual sink, not the physical speaker or Spotify.

The original Spotify remote disconnect has no reliable reproduction. Automatic audible recovery without reselection or pressing Play is still unverified. The upstream recovery PR remains unmerged. HTTP status polling also produces Snapserver socket shutdown error 107 after successful replies; no Spotify session close was observed during this deployment check.

## Local rollback material

Previous images, stopped volume archives, and the previous configuration are retained under `/home/yme/.local/state/syrensystem/backups/20260909-spotify-update`. Receiver files are backed up under `/var/backups/syrensystem/20260909-audio-update` on HiFiBerry. These directories contain private state and must not be committed.

The rollback Compose file must use project name `syrenserver` to reuse the existing volumes. Stop the user stack before any rollback, and preserve any settings changed since this backup. Restoring an older image does not require discarding current state.
