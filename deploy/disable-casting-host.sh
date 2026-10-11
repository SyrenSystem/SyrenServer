#!/bin/sh
# Removes the AirPlay timing helper and the Avahi relay as root; remove the casting overlay lines and restart the stack first.
set -eu

if [ "$(id -u)" -ne 0 ]; then
  printf '%s\n' 'Usage: sudo sh disable-casting-host.sh' >&2
  exit 1
fi

for unit in syren-avahi-relay.service syren-nqptp.service; do
  if [ -f "/etc/systemd/system/$unit" ]; then
    systemctl disable --now "$unit"
  fi
done
rm -f /etc/systemd/system/syren-avahi-relay.service /etc/systemd/system/syren-nqptp.service \
  /etc/syrensystem/avahi-relay.env /etc/sysusers.d/syrensystem-casting.conf /etc/tmpfiles.d/syrensystem-casting.conf
rm -rf /usr/local/libexec/syrensystem
systemctl daemon-reload

# Nothing else runs as these accounts, so they can go once the units are stopped.
for account in syren-nqptp syren-avahi-relay; do
  if id -u "$account" >/dev/null 2>&1; then
    userdel "$account"
  fi
done

# The empty /run directories stay until the next reboot, so a container that still mounts them keeps starting.
printf '%s\n' 'The AirPlay host helpers are removed.'
