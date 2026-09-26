#!/bin/sh
# Lets one user's desktop apps and services use real time audio priority; run once as root, then log out and in.
set -eu

user_name="${1:-${SUDO_USER:-}}"
if [ "$(id -u)" -ne 0 ] || [ -z "$user_name" ]; then
  printf '%s\n' 'Usage: sudo sh enable-realtime.sh [user]' >&2
  exit 1
fi
user_id="$(id -u "$user_name")"
directory="/etc/systemd/system/user@$user_id.service.d"
mkdir -p "$directory"
cat > "$directory/syrensystem-realtime.conf" <<'LIMITS'
[Service]
# PipeWire takes 88 for its audio threads and SyrenSystem audio runs just below it.
LimitRTPRIO=95
LimitNICE=40
LIMITS
chmod 644 "$directory/syrensystem-realtime.conf"
systemctl daemon-reload
printf 'Real time audio is allowed for %s after a full log out and log in.\n' "$user_name"
