# Audio completion gate

Before declaring any feature or fix complete, run `dotnet test --configuration Release`. When SyrenApp is available, also run `python3 ../SyrenApp/scripts/audio_gate.py` to check the entire audio path. The app software and signal checks and the server check must pass for changes spanning the system.

Preserve the audio handoff tests: source activity must be published promptly, unchanged settings must not recreate priority streams or client membership, and source ordering must remain consistent with the app. Add a failing regression before fixing another handoff bug. Missing tools or failed checks block completion and must be reported.

See `../SyrenApp/AUDIO_TESTING.md` for the physical acceptance requirements and CI status checks.

Changes to the audio image or dependency versions also require `podman build --tag syren-snapserver-check deploy/snapserver` (or Docker). The build runs the patched librespot workspace tests and the real Snapserver source and stream lifecycle check. Preserve the source revisions and patch checksums in `deploy/snapserver/versions.json`.
