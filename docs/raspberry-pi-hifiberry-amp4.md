# Raspberry Pi 4 and HiFiBerry AMP4 setup

This guide covers the Raspberry Pi 4 Model B, HiFiBerry AMP4, and a pair of passive DALI SONIK 1 speakers used for a SyrenSystem player.

## Provisioned SD card

The SD card prepared for this player has the following configuration:

- Raspberry Pi OS Lite 64 bit, Debian 13 Trixie, image dated 2026-06-18
- Hostname `hifiberry`
- User `yme`
- SSH enabled with the user's existing Ed25519 public key
- Timezone `Europe/Amsterdam`
- No saved Wi-Fi network, so use Ethernet for the first boot
- Built in Raspberry Pi and HDMI audio disabled
- HiFiBerry AMP4 enabled with `hifiberry-dacplus-std`

The login password is intentionally not stored in this repository. Change the initial password after the first login:

```bash
passwd
```

The original HiFiBerryOS stopped active development in April 2025. HiFiBerryOS64 is end of life, while HiFiBerryOS NG remains a preview based on Raspberry Pi OS. Raspberry Pi OS is therefore the base for this player. See the [HiFiBerryOS status page](https://www.hifiberry.com/hifiberryos/) and [HiFiBerryOS NG announcement](https://support.hifiberry.com/hc/en-us/community/posts/32487869797277-HiFiBerryOS-Next-Generation).

## Assemble the boards

Disconnect all power before assembly.

1. Fit the supplied spacers to the Raspberry Pi.
2. Align the AMP4 socket with all 40 GPIO pins.
3. Press the AMP4 straight down without bending the pins.
4. Secure the boards using the supplied screws.
5. Insert the prepared SD card.

The AMP4 powers the Raspberry Pi through the GPIO header. Do not connect a USB-C power supply to the Pi while the AMP4 is powered.

## Connect power and speakers

With the GPIO header at the top and the screw terminals facing forward, the six terminals are:

```text
DC+ | DC- | L+ | L- | R- | R+
```

The power supply can be connected to either the round barrel socket or the first two screw terminals. Use only one power input.

Connect the left passive speaker to `L+` and `L-`. Connect the right passive speaker to `R+` and `R-`. Preserve the same polarity at both speakers. The channel outputs are bridge tied and must not be bridged together. Do not join either speaker negative terminal to ground.

The [official AMP4 connector diagram](https://www.hifiberry.com/wp-content/uploads/2023/09/amp4-connections.jpg) shows the power and speaker terminals.

## Choose the power supply

The AMP4 accepts 12 to 20 V DC during normal operation. Its absolute maximum is 24 V, which is not a target operating voltage. HiFiBerry recommends no more than 20 V for 4 ohm speakers and recommends 20 V as the general choice for 8 ohm speakers. See the [AMP4 datasheet](https://www.hifiberry.com/docs/data-sheets/datasheet-amp4/) and [AMP4 product guidance](https://www.hifiberry.com/shop/boards/hifiberry-amp4/).

A regulated 20 V, 4 A, 80 W supply is the general recommendation. An 18 or 19 V regulated supply with sufficient current is also suitable. Higher available current is safe because the amplifier draws only the current it needs. Do not exceed the voltage limit or reverse the polarity.

The available power supply is a 19 V, 9.23 A laptop adapter. Its 175 W current capacity is more than required and does not force that power into the AMP4. It is suitable only when all of the following are confirmed:

- Its output is 19 V DC.
- It is regulated.
- It is centre positive.
- Its plug makes a reliable connection with the AMP4 socket. The expected barrel plug is 5.5 x 2.1 mm.
- The adapter and cable are undamaged and from a reputable manufacturer.

Most modern laptop switching adapters are regulated, but the model's manufacturer datasheet is the authoritative check. Look for `regulated`, `switching power supply`, output voltage tolerance, line regulation, or load regulation. Safety certification by itself does not prove voltage regulation.

### Check the adapter with a multimeter

Do this before connecting the adapter to the AMP4:

1. Put the black probe in `COM` and the red probe in the voltage input.
2. Select DC voltage. Do not use a current measurement mode.
3. Place the black probe against the barrel's outside sleeve.
4. Touch the red probe to the centre contact.
5. Confirm a stable reading close to positive 19 V.

A negative reading means the polarity is reversed. A reading substantially above the label voltage suggests that the adapter is unsuitable or unregulated. A no load measurement is a useful check, but the manufacturer datasheet is still needed to confirm regulation under load.

Connect the equipment in this order:

1. Disconnect the adapter from mains power.
2. Connect both speakers.
3. Connect the low voltage barrel plug or the `DC+` and `DC-` terminals.
4. Check all polarity and exposed wire strands.
5. Connect Ethernet.
6. Apply mains power to the adapter.

HiFiBerry recommends switching at the mains side rather than repeatedly inserting a live barrel plug. The amplifier's input capacitors can cause a small spark and wear the connector. See [HiFiBerry's amplifier power guidance](https://www.hifiberry.com/blog/techtalk-plugging-in-the-power-into-a-power-amplifier/).

## DALI SONIK 1 compatibility

The DALI SONIK 1 is a passive, 6 ohm bookshelf speaker with a recommended amplifier range of 25 to 100 W. The AMP4 supports speakers from 4 to 8 ohms, so this is an electrically compatible load. See the [DALI SONIK 1 specifications](https://dali-speakers.com/en-gb/products/sonik/sonik-1/?sku=200413).

At 19 V, the AMP4 operates near the lower end of DALI's recommended amplifier range. This is suitable for normal use. Avoid driving the amplifier into audible clipping at high volume. Start playback at 10 to 20 percent volume and increase it gradually.

If only one speaker is connected, use one complete channel and leave the other channel disconnected. Never combine the two channels.

## AMP4 boot configuration

Current Raspberry Pi OS mounts the boot partition at `/boot/firmware`. The relevant entries in `/boot/firmware/config.txt` are:

```ini
dtparam=audio=off
dtoverlay=vc4-kms-v3d,noaudio
dtoverlay=hifiberry-dacplus-std
```

`hifiberry-dacplus-std` is the AMP4 overlay for kernels at or newer than 6.1.77. Older kernels use `hifiberry-dacplus`. Do not configure both overlays. See [HiFiBerry's Linux configuration guide](https://www.hifiberry.com/docs/software/configuring-linux-3-18-x/).

## First boot and validation

The first boot can take several minutes while Raspberry Pi OS expands the root filesystem and completes account setup. Connect over Ethernet using:

```bash
ssh yme@hifiberry.local
```

If multicast DNS is unavailable, find the Pi's address in the router and connect with `ssh yme@<ip-address>`.

After login, confirm the operating system and kernel:

```bash
cat /etc/os-release
uname -r
```

Confirm that ALSA detects the HiFiBerry card:

```bash
aplay -l
```

The output should contain a card similar to `snd_rpi_hifiberry_dacplus`. Test the two channels at a low volume:

```bash
speaker-test -c 2 -t wav
```

Stop the test with `Ctrl+C`. If ALSA selects another output, use the card number shown by `aplay -l`:

```bash
speaker-test -D hw:1,0 -c 2 -t wav
```

Replace `1` with the HiFiBerry card number.

Configure Wi-Fi interactively if Ethernet will not be permanent:

```bash
sudo nmtui
```

Select `Activate a connection`, choose the wireless network, enter its password, and confirm that SSH still works before disconnecting Ethernet.
