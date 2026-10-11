#!/bin/sh
# Installs and starts the AirPlay timing helper and the Avahi relay as root; safe to run again for an upgrade.
set -eu

source_directory="${1:-}"
allow_uid="${2:-}"
if [ "$(id -u)" -ne 0 ] || [ ! -d "$source_directory" ]; then
  printf '%s\n' 'Usage: sudo sh enable-casting-host.sh <extracted host files> <host uid of the audio container>' >&2
  exit 1
fi
case "$allow_uid" in
  '' | *[!0-9]*)
    printf 'Invalid host uid: %s\n' "$allow_uid" >&2
    exit 1
    ;;
esac
library_directory=/usr/local/libexec/syrensystem
repository_directory="$(CDPATH= cd -- "$(dirname -- "$0")/snapserver" && pwd)"

# The image checks its own copies, so root only installs files that also match this checkout; only the nqptp binary comes from the image.
same_as_repository() {
  if ! cmp -s "$source_directory/$1" "$repository_directory/$2"; then
    printf 'The image copy of %s differs from %s; build the image from this checkout first.\n' "$1" "$repository_directory/$2" >&2
    exit 1
  fi
}
same_as_repository syren-nqptp.service host/syren-nqptp.service
same_as_repository syren-avahi-relay.service host/syren-avahi-relay.service
same_as_repository syrensystem-casting.sysusers.conf host/syrensystem-casting.sysusers.conf
same_as_repository syrensystem-casting.tmpfiles.conf host/syrensystem-casting.tmpfiles.conf
same_as_repository syren-avahi-relay.py casting_dbus_relay.py

# Only our own nqptp may hold the PTP ports, so a distro nqptp or shairport-sync stops the install.
main_pid="$(systemctl show -p MainPID --value syren-nqptp.service 2>/dev/null || true)"
foreign_owners="$(ss -Hlunp '( sport = :319 or sport = :320 )' | awk -v main="${main_pid:-0}" '{
  rest = $0; foreign = ($0 !~ /pid=/)
  while (match(rest, /pid=[0-9]+/)) {
    if (substr(rest, RSTART + 4, RLENGTH - 4) != main) foreign = 1
    rest = substr(rest, RSTART + RLENGTH)
  }
  if (foreign) print
}')"
if [ -n "$foreign_owners" ]; then
  printf '%s\n%s\n' 'UDP 319 or 320 is already used by another program:' "$foreign_owners" >&2
  exit 1
fi

install -D -m 0644 "$repository_directory/host/syrensystem-casting.sysusers.conf" /etc/sysusers.d/syrensystem-casting.conf
systemd-sysusers /etc/sysusers.d/syrensystem-casting.conf
install -D -m 0644 "$repository_directory/host/syrensystem-casting.tmpfiles.conf" /etc/tmpfiles.d/syrensystem-casting.conf
systemd-tmpfiles --create /etc/tmpfiles.d/syrensystem-casting.conf

install -D -m 0755 -o root -g root "$source_directory/nqptp" "$library_directory/nqptp"
install -D -m 0644 -o root -g root "$repository_directory/casting_dbus_relay.py" "$library_directory/syren-avahi-relay.py"
install -m 0644 "$repository_directory/host/syren-nqptp.service" "$repository_directory/host/syren-avahi-relay.service" /etc/systemd/system/
install -d -m 0755 /etc/syrensystem
printf 'SYREN_RELAY_ALLOW_UID=%s\n' "$allow_uid" > /etc/syrensystem/avahi-relay.env
chmod 0644 /etc/syrensystem/avahi-relay.env
systemctl daemon-reload

# nqptp prints its clock ID before it opens any socket, so this works under the unit's sandbox while the service runs.
# The image binary runs here as well, so these settings follow syren-nqptp.service, without its port capability.
families="$(systemctl show -p RestrictAddressFamilies --value syren-nqptp.service)"
probe="$(systemd-run --wait --pipe --collect --quiet -p User=syren-nqptp -p RuntimeMaxSec=2 \
  -p "RestrictAddressFamilies=$families" -p NoNewPrivileges=yes -p ProtectSystem=strict -p ProtectHome=yes \
  -p PrivateTmp=yes -p CapabilityBoundingSet= -p ProtectKernelTunables=yes -p ProtectKernelModules=yes \
  -p ProtectKernelLogs=yes -p ProtectControlGroups=yes -p ProtectClock=yes -p ProtectHostname=yes \
  -p RestrictNamespaces=yes -p LockPersonality=yes -p MemoryDenyWriteExecute=yes -p SystemCallArchitectures=native \
  -p SystemCallFilter=@system-service "$library_directory/nqptp" -v 2>&1 || true)"
clock_id="$(printf '%s\n' "$probe" | sed -n 's/.*Clock ID: "\([0-9A-Fa-f]*\)".*/\1/p' | head -n 1)"
if [ -z "$clock_id" ] || [ -z "$(printf '%s' "$clock_id" | tr -d 0)" ]; then
  printf '%s\n%s\n' 'nqptp has no clock ID in its sandbox, so senders would never wake it:' "$probe" >&2
  exit 1
fi
printf 'nqptp clock ID: %s\n' "$clock_id"

systemctl enable syren-nqptp.service syren-avahi-relay.service
systemctl restart syren-nqptp.service syren-avahi-relay.service
printf '%s\n' 'Restarting nqptp stops any AirPlay session that is playing; the casting supervisor restarts the targets.'

if command -v nft >/dev/null 2>&1 && nft list ruleset 2>/dev/null | grep -Eq 'hook input .*policy drop'; then
  printf '%s\n' 'Warning: the firewall drops input by default; AirPlay 2 needs inbound TCP 7000 to 7015, UDP 319 and 320, UDP 6001 to 6160 and the ephemeral port range.' >&2
fi
