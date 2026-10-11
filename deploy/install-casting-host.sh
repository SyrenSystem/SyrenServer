#!/bin/sh
# Installs the AirPlay host helpers from the production audio image and checks them; sudo asks once.
set -eu

server_directory="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
# The production audio image by default, so nqptp on the host comes from the same build as the shairport-sync that reads it.
image="${1:-localhost/syren-snapserver-profiles}"
relay_socket=/run/syren-avahi-relay/system_bus_socket

fail() {
  printf 'AirPlay host check failed: %s\n' "$1" >&2
  exit 1
}

for required_command in cmp podman ss ps python3 sha256sum stat systemctl sudo tar; do
  if ! command -v "$required_command" >/dev/null 2>&1; then
    printf 'Missing required command: %s\n' "$required_command" >&2
    exit 1
  fi
done

image_id="$(podman image inspect --format '{{.Id}}' "$image" 2>/dev/null)" || fail "there is no image $image; build it first"
printf 'Host helpers come from %s, image %s.\n' "$image" "$image_id"

work="$(mktemp -d "${TMPDIR:-/tmp}/syren-casting-host.XXXXXX")"
trap 'rm -rf "$work"' EXIT

# The host files come out of the same image that gets deployed, and must match its checksums and, in the root step, this checkout.
podman run --rm --network none --entrypoint tar "$image_id" -C /usr/share/syrensystem/host -cf - . | tar -xf - -C "$work" || true
[ -f "$work/SHA256SUMS" ] || fail "$image has no AirPlay host files; build it from this checkout first"
(cd "$work" && sha256sum --check --strict SHA256SUMS)

container_uid="$(podman run --rm --network none --entrypoint id "$image_id" -u)"
host_uid="$(podman unshare awk -v uid="$container_uid" '$1<=uid && uid<$1+$3 {print $2+uid-$1}' /proc/self/uid_map)"
case "$container_uid:$host_uid" in
  *[!0-9:]* | :* | *:) fail "could not map container uid '$container_uid' to a host uid" ;;
esac
printf 'Audio container uid %s runs as host uid %s.\n' "$container_uid" "$host_uid"

printf '%s\n' 'Installing the AirPlay timing helper and the Avahi relay; sudo asks for your password.'
sudo sh "$server_directory/deploy/enable-casting-host.sh" "$work" "$host_uid"

systemctl is-active syren-nqptp.service syren-avahi-relay.service || fail 'a host unit is not active'

attempt=0
until [ -n "$(ss -Hlun 'sport = :319')" ] && [ -n "$(ss -Hlun 'sport = :320')" ] \
    && ss -Hlun 'sport = :9000' | grep -Eq '(127\.0\.0\.1|\[::1\]):9000'; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 50 ] || fail 'nqptp does not listen on UDP 319, 320 and localhost:9000'
  sleep 0.1
done
scheduling="$(ps -o cls=,rtprio= -C nqptp | tr -s ' ' | sed 's/^ //; s/ $//')"
[ "$scheduling" = 'FF 5' ] || fail "nqptp runs at '$scheduling' instead of FF 5"

# Slot 0 is a real slot, so this check never uses up an nqptp client record.
python3 -I -c 'import socket; socket.socket(socket.AF_INET, socket.SOCK_DGRAM).sendto(b"/syren-airplay-0 T\0", ("127.0.0.1", 9000))'
attempt=0
until [ -e /run/syren-airplay/syren-airplay-0 ]; do
  attempt=$((attempt + 1))
  [ "$attempt" -lt 10 ] || fail 'nqptp did not create /run/syren-airplay/syren-airplay-0'
  sleep 0.1
done
region="$(stat -c '%U %a' /run/syren-airplay/syren-airplay-0)"
[ "$region" = 'syren-nqptp 666' ] || fail "the shared memory is '$region' instead of 'syren-nqptp 666'"
[ ! -e /dev/shm/syren-airplay-0 ] || fail 'nqptp wrote its shared memory to the host /dev/shm'

relay_check() {
  podman run --rm --network none --user snapserver -v /run/syren-avahi-relay:/run/syren-avahi-relay:ro \
    --entrypoint python3 "$image_id" -I /usr/local/bin/casting_dbus_relay.py "$1" "$relay_socket"
}
version="$(relay_check --check)" || fail 'the audio container cannot reach Avahi through the relay'
case "$version" in
  'avahi '*) printf 'The audio container reaches %s through the relay.\n' "$version" ;;
  *) fail "the relay answered '$version' instead of an Avahi version" ;;
esac
relay_check --check-denied || fail 'the relay passed a call that is not for Avahi'

busy_ports="$(ss -Hlun | awk '{count = split($4, parts, ":"); port = parts[count] + 0; if (port >= 6001 && port <= 6160) print port}' | sort -un | tr '\n' ' ')"
if [ -n "$busy_ports" ]; then
  printf 'Warning: UDP ports %sare in use; classic AirPlay senders need 6001 to 6160.\n' "$busy_ports" >&2
fi
printf '%s\n' 'The AirPlay host helpers are installed and checked.'
