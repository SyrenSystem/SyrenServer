#!/bin/sh
set -eu

default_route="$(ip -4 route get 1.1.1.1)"
interface_ip_address="$(printf '%s\n' "$default_route" | sed -n 's/.* src \([^ ]*\).*/\1/p')"

if [ -z "$interface_ip_address" ]; then
  printf '%s\n' 'Could not detect the IPv4 address for Spotify discovery.' >&2
  exit 1
fi

sed "s/__ZEROCONF_INTERFACE__/$interface_ip_address/g" /etc/snapserver.conf > /tmp/snapserver.conf
exec /usr/bin/snapserver --config /tmp/snapserver.conf --server.datadir=/var/lib/snapserver
