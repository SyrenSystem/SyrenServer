# SyrenServer

Profile sessions use protocol 3 across the server, SyrenApp and both receivers. See [the profile session guide](../SyrenDocs/ProfileSessions.md) and [delivery evidence](../SyrenDocs/ProfileSessionsDelivery.md) before upgrading. After coordinated migration, always start with `podman compose -f compose.yaml -f compose.profiles.yaml up -d --build`. The base Compose file and legacy source instructions below remain the version 2 rollback configuration.


SyrenServer converts BLE distance measurements received through MQTT into per speaker Snapcast volume changes. It owns speaker setup, playback groups, ordered source priority, calibrated positions, and persistent volume settings.

## Container stack

The stack contains Mosquitto, Snapserver with librespot, and SyrenServer. Snapserver exposes the `spotify` source through librespot and the `laptop` source as raw 48 kHz stereo PCM over TCP port 4953. SyrenServer creates one Snapcast meta stream for each distinct ordered source list used by a playback group. A group with `[spotify, laptop]` therefore uses Spotify while it is active and falls back to laptop audio while Spotify is idle.

All three containers use host networking. Librespot needs LAN multicast DNS and a dynamic Zeroconf port to appear in Spotify Connect, so bridge networking is not suitable for the deployed stack.

With `SYREN_CASTING=1` the audio container also runs one shairport-sync AirPlay 2 receiver for each AirPlay target. shairport-sync registers AirPlay targets with the host Avahi through a host relay that only forwards Avahi calls, and reads AirPlay 2 timing from the host nqptp through a read only `/dev/shm`. Both host services, `syren-nqptp.service` and `syren-avahi-relay.service`, come out of the audio image: `deploy/install-casting-host.sh [image]` installs and checks them with one sudo step, from the production image `localhost/syren-snapserver-profiles` unless another is given, and `sudo sh deploy/disable-casting-host.sh` removes them. The root step takes only the nqptp binary from the image. It installs the units, the account and tmpfiles files and the relay script from this checkout, and stops if the image's copy of any of them differs, so build the image from the same checkout first. See [AirPlay](../SyrenDocs/ProfileSessions.md#airplay) for the overlay lines and the playback rules.

The audio dependencies were checked on 2026-09-09: Snapcast 0.35.0, Mosquitto 2.1.2, and librespot's current development revision with upstream [recovery PR 1692](https://github.com/librespot-org/librespot/pull/1692). The PR is not merged upstream. It preserves playback state during session recovery so playback can resume without selecting the device again; a process restart alone does not provide that behavior. Exact revisions and package checksums are recorded in `deploy/snapserver/versions.json` and shipped inside the image at `/usr/share/syrensystem/audio-versions.json`.

The AirPlay dependencies were checked on 2026-10-08. The released versions lack multi client nqptp and the per instance AirPlay 2 port, so both are pinned by full SHA to the head of their `development` branches: shairport-sync `b45f15b32882409a7d39b01293d43da3d5519416` (5.6-dev, Linux sources identical to 5.6-dev-152) and nqptp `0a88cd7ca9325f023ca5d891714695167672bdb9` (1.2.9-dev, shared memory interface 11). The build requires both to use the same shared memory interface version. `shairport-sync-syren.patch` makes three changes: shairport-sync keeps the higher real time priority it was started with instead of lowering its player threads to FIFO 3 and 2, it sends a `syen` metadata item when the playing sender's connection ends, which upstream never reports, queued while it still holds the play lock so it always comes before the next sender's `conn`, and it sends a `syav` metadata item when it loses Avahi. The casting supervisor restarts an instance only on `syav`, never on log text, because a sender can make shairport-sync echo its own text into the log. Because the patch changes the tree, the version string `b45f15b-dirty-AirPlay2-smi11-OpenSSL-Avahi-pipe-metadata-sysconfdir:/etc` proves it was applied. `nqptp-syren.patch` makes nqptp ignore control messages for any name other than `/syren-airplay-0` to `/syren-airplay-15`, and leave a record empty instead of exiting when its shared memory cannot be made, so one stray local packet cannot stop the timing of every target. nqptp then reports `Version: 0a88cd7-dirty.` When moving to the shairport-sync 5.6 and nqptp 1.2.9 releases, check whether the patch hunks were merged and rerun both gates.

The image build runs the patched librespot workspace tests and a real Snapserver protocol test with synthetic sources. Snapserver 0.35.0 also includes `snapserver-lifecycle.patch`, which fixes a deleted iterator being dereferenced and callbacks reaching deleted priority streams. Listener ownership is retained for each callback without holding the listener list lock during the call. The protocol test caught the crashes and now checks source activity transitions and repeated priority stream creation and removal. Changes to audio dependencies must pass the image build as well as the shared audio regression gate. Updating versions means refreshing the pinned revisions, checking whether the patches have merged, and rerunning both gates.

A separate `airplay-builder` stage builds shairport-sync and nqptp and runs `smoke_casting.py`. Two instances, started with the production command and Landlock rules, run against nqptp and a private system bus behind the Avahi relay. Landlock keeps shairport-sync out of the files under `/var/lib/snapserver`, including the Spotify credentials and journals. It does not limit the network, so a compromised instance can reach Snapserver's control API and the broker, which every LAN device already can; closing that LAN exposure is a separate, existing issue. The smoke checks AirPlay 2 mode, one nqptp shared memory region per instance, RTSP `OPTIONS` and the device ID and name in `/info`, both metadata pipes, that nqptp ignores stray control names and still makes a real slot's region afterwards, that the relay denies `RequestName`, that both instances send `syav` and keep running when the relay stops, that every idle thread runs at the requested FIFO priority, that both instances stop within 2 seconds of SIGTERM, and that an instance without nqptp exits instead of falling back to classic AirPlay. Build steps run as root, where the production command always adds `chrt --fifo 84`; when the build cannot set that priority, as in a Docker build without `CAP_SYS_NICE` or a first install before the real time limit, the smoke drops `chrt` and skips the priority check, and its summary says so. In the same way it first tries `setpriv --landlock-access fs` once, and a builder that blocks the Landlock calls, such as Docker under a seccomp profile without them, runs the instances without the Landlock options and records `landlock_skipped`. shairport-sync waits for a network interface with a MAC address, so do not build with `--network none`. The image's Python unit tests also cover the casting supervisor, the relay, the host unit files and `start-profile-audio.py`. `test_casting_host_scripts.py` checks the root installer with stubbed commands and runs only on the host, because the image build has no `deploy/` directory; `test_casting_e2e.py` also runs only on the host. `deploy/snapserver/casting_e2e.py` is a host side check against the live stack: it finds one target by its advertised AirPlay name, such as `SyrenSystem · Kantoor #2`, maps it to its room with the supervisor's own naming code, streams a test tone to it with pyatv and follows it through the catalogue, the receivers and a Snapcast tap, checking the pitch on both channels.

Build and validate without starting it:

```bash
podman-compose build
podman-compose config
```

Start it only after stopping any existing services on ports 1883, 1704, 1705, and 1780:

```bash
podman-compose up -d
```

For the Debian desktop deployment as user services (`./deploy/install-user-services.sh`), follow the SyrenDocs guide [Laptop audio and playback groups](https://github.com/SyrenSystem/SyrenDocs/blob/main/LaptopAudioAndPlaybackGroups.md).

The `syren-server-state` volume contains `/var/lib/syren-server/state.json`. Back up that volume before migration or recovery work. SyrenServer refuses to start with corrupt or unsupported state instead of silently losing calibration.

The `snapserver-data` volume holds `/var/lib/snapserver`, where Snapserver keeps its `server.json`. Snapcast client volumes and group assignments therefore survive `podman-compose down` and image rebuilds.

## Configuration overrides

.NET configuration keys use double underscores in environment variables. Common overrides include:

| Variable | Purpose |
|---|---|
| `Mqtt__Host` | MQTT broker host |
| `Mqtt__Port` | MQTT broker port |
| `SnapCast__ServerHost` | Snapserver JSON RPC host |
| `SnapCast__HttpPort` | Snapserver JSON RPC port |
| `State__FilePath` | Persistent state file |
| `Playback__ReconcileSeconds` | Seconds between Snapcast group reconciliation passes |
| `Playback__Sources__0__Id` | First source ID exposed in the app |
| `Playback__Sources__0__Name` | First source display name |
| `SpeakersOptions__SpeakersInfo__0__SensorId` | First sensor ID |
| `SpeakersOptions__SpeakersInfo__0__SnapClientId` | First Snapclient ID |
| `SpeakersOptions__SpeakersInfo__0__FullVolumeDistance` | First full volume distance in millimetres |
| `SpeakersOptions__SpeakersInfo__0__MuteDistance` | First mute distance in millimetres |

`Playback__Sources` is merged by index with the list in `appsettings.json`. Overriding index 0 replaces the first entry, entries at other indexes stay in place, and a blank id is rejected at startup. To change the set of sources, edit `appsettings.json` or mount a replacement file rather than relying on environment variables.

Host networking uses the localhost defaults from `appsettings.json`. A deployment without host networking must override both peer hosts and provide a separate mDNS and Zeroconf design for Spotify discovery.

The Snapserver configuration in `deploy/snapserver/snapserver.conf` uses a 2000 ms playback buffer for every Snapcast stream, so playback changes take about 2 seconds to reach the speakers. Low latency laptop audio uses the separate RTP path. The laptop source uses 10 ms input chunks and uncompressed PCM to avoid codec delay, while Spotify uses Snapserver's default 20 ms chunk. A future design that needs a very different total latency for each source will require separate playback pipelines rather than a single Snapserver instance.

Spotify's own volume slider controls librespot with `--volume-ctrl linear`. The receiver applies the requested level directly as audio gain, so an 80% Connect volume produces 80% signal amplitude. This avoids librespot's default 60 dB logarithmic curve, which reduces amplitude to about 25% at the same setting. Group master volume, speaker levels, and source balance remain separate controls. An exact perceptual match to a particular Spotify desktop version requires a playback comparison.

## Setup from the app

The app discovers connected Snapclients through the retained runtime MQTT message. A logical speaker maps a friendly name to one Snapclient and optionally one location sensor. Speakers without sensors support manual volume. Automatic group volume requires a calibrated sensor on every member when a group switches to automatic or gains a member; members that lose calibration later stay in the group and are silent until they are recalibrated.

Every speaker belongs to at most one playback group, and a speaker in no group is silent. Each group stores an ordered source priority list, manual or automatic volume mode, master volume, mute state, and member speakers. Speaker level and group master volume are applied together. Sources are selected by priority and are never mixed within one group.

`SpeakersOptions` seeds speakers only into a new state file or while migrating a version 1 file. An existing version 2 state file is never changed by `SpeakersOptions`, so speakers deleted from the app stay deleted. Migration creates one group named `default` that contains every migrated speaker; its volume mode is manual unless every speaker was calibrated. Group source ids that are not in `Playback__Sources` are dropped when the state loads, with a warning in the log.

## Hardware setup

See the SyrenDocs Raspberry Pi and HiFiBerry guides for player provisioning, amplifier wiring, Snapclient discovery, and speaker mapping.
