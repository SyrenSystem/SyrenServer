# SyrenServer

SyrenServer converts BLE distance measurements received through MQTT into per speaker Snapcast volume changes. It also owns the calibrated speaker positions and persists them across restarts.

## Container stack

The stack contains Mosquitto, Snapserver with librespot, and SyrenServer. All three containers use host networking. Librespot needs LAN multicast DNS and a dynamic Zeroconf port to appear in Spotify Connect, so bridge networking is not suitable for the deployed stack.

Build and validate without starting it:

```bash
podman-compose build
podman-compose config
```

Start it only after stopping any existing services on ports 1883, 1704, 1705, and 1780:

```bash
podman-compose up -d
```

The `syren-server-state` volume contains `/var/lib/syren-server/state.json`. Back up that volume before migration or recovery work. SyrenServer refuses to start with corrupt or unsupported state instead of silently losing calibration.

## Configuration overrides

.NET configuration keys use double underscores in environment variables. Common overrides include:

| Variable | Purpose |
|---|---|
| `Mqtt__Host` | MQTT broker host |
| `Mqtt__Port` | MQTT broker port |
| `SnapCast__ServerHost` | Snapserver JSON RPC host |
| `SnapCast__HttpPort` | Snapserver JSON RPC port |
| `State__FilePath` | Persistent state file |
| `SpeakersOptions__SpeakersInfo__0__SensorId` | First sensor ID |
| `SpeakersOptions__SpeakersInfo__0__SnapClientId` | First Snapclient ID |
| `SpeakersOptions__SpeakersInfo__0__FullVolumeDistance` | First full volume distance in millimetres |
| `SpeakersOptions__SpeakersInfo__0__MuteDistance` | First mute distance in millimetres |

Host networking uses the checked-in localhost defaults. A non-host deployment must override both peer hosts and provide a separate mDNS and Zeroconf design for Spotify discovery.

## Hardware setup

See the SyrenDocs Raspberry Pi and HiFiBerry guides for player provisioning, amplifier wiring, Snapclient discovery, and speaker mapping.
