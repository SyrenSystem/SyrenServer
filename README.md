# SyrenServer

SyrenServer converts BLE distance measurements received through MQTT into per speaker Snapcast volume changes. It owns speaker setup, playback groups, ordered source priority, calibrated positions, and persistent volume settings.

## Container stack

The stack contains Mosquitto, Snapserver with librespot, and SyrenServer. Snapserver exposes the `spotify` source through librespot and the `laptop` source as raw 48 kHz stereo PCM over TCP port 4953. SyrenServer creates one Snapcast meta stream for each distinct ordered source list used by a playback group. A group with `[spotify, laptop]` therefore uses Spotify while it is active and falls back to laptop audio while Spotify is idle.

All three containers use host networking. Librespot needs LAN multicast DNS and a dynamic Zeroconf port to appear in Spotify Connect, so bridge networking is not suitable for the deployed stack.

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

The Snapserver configuration in `deploy/snapserver/snapserver.conf` uses a 200 ms playback buffer for every Snapcast stream. The laptop source uses 10 ms input chunks and uncompressed PCM to avoid codec delay, while Spotify uses Snapserver's default 20 ms chunk. A future design that needs a very different total latency for each source will require separate playback pipelines rather than a single Snapserver instance.

## Setup from the app

The app discovers connected Snapclients through the retained runtime MQTT message. A logical speaker maps a friendly name to one Snapclient and optionally one location sensor. Speakers without sensors support manual volume. Automatic group volume requires a calibrated sensor on every member when a group switches to automatic or gains a member; members that lose calibration later stay in the group and are silent until they are recalibrated.

Every speaker belongs to at most one playback group, and a speaker in no group is silent. Each group stores an ordered source priority list, manual or automatic volume mode, master volume, mute state, and member speakers. Speaker level and group master volume are applied together. Sources are selected by priority and are never mixed within one group.

`SpeakersOptions` seeds speakers only into a new state file or while migrating a version 1 file. An existing version 2 state file is never changed by `SpeakersOptions`, so speakers deleted from the app stay deleted. Migration creates one group named `default` that contains every migrated speaker; its volume mode is manual unless every speaker was calibrated. Group source ids that are not in `Playback__Sources` are dropped when the state loads, with a warning in the log.

## Hardware setup

See the SyrenDocs Raspberry Pi and HiFiBerry guides for player provisioning, amplifier wiring, Snapclient discovery, and speaker mapping.
