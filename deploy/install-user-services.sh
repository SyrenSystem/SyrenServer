#!/bin/sh
set -eu

server_directory="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
compose_file="$server_directory/compose.yaml"
profile_compose=""
if [ "${1:-}" = "--profile-sessions" ]; then
  profile_compose="-f $server_directory/compose.profiles.yaml"
elif [ "$#" -gt 0 ]; then
  printf '%s\n' 'Usage: install-user-services.sh [--profile-sessions]' >&2
  exit 1
fi
user_service_directory="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
audio_configuration_directory="${XDG_CONFIG_HOME:-$HOME/.config}/syrensystem"
service_template="$server_directory/deploy/systemd/syrensystem-stack.service.in"
service_file="$user_service_directory/syrensystem-stack.service"

for required_command in podman podman-compose systemctl; do
  if ! command -v "$required_command" >/dev/null 2>&1; then
    printf 'Missing required command: %s\n' "$required_command" >&2
    exit 1
  fi
done

mkdir -p "$user_service_directory" "$audio_configuration_directory"

escaped_server_directory="$(printf '%s' "$server_directory" | sed 's/[&|]/\\&/g')"
escaped_compose_file="$(printf '%s' "$compose_file" | sed 's/[&|]/\\&/g')"
escaped_profile_compose="$(printf '%s' "$profile_compose" | sed 's/[&|]/\\&/g')"
sed \
  -e "s|__SERVER_DIRECTORY__|$escaped_server_directory|g" \
  -e "s|__COMPOSE_FILE__|$escaped_compose_file|g" \
  -e "s|__PROFILE_COMPOSE__|$escaped_profile_compose|g" \
  "$service_template" > "$service_file"

if [ ! -f "$audio_configuration_directory/audio-sender.conf" ]; then
  printf '%s\n%s\n' \
    'SYREN_SERVER_HOST=127.0.0.1' \
    'SYREN_SERVER_PORT=4953' \
    > "$audio_configuration_directory/audio-sender.conf"
fi

if [ -n "$profile_compose" ]; then
  podman-compose -f "$compose_file" -f "$server_directory/compose.profiles.yaml" build
else
  podman-compose -f "$compose_file" build
fi
systemctl --user daemon-reload
systemctl --user enable syrensystem-stack.service
systemctl --user restart syrensystem-stack.service
if [ -n "$profile_compose" ]; then
  systemctl --user disable --now syren-laptop-audio.service >/dev/null 2>&1 || true
  if command -v syren-audio-control >/dev/null 2>&1; then
    syren-audio-control disable
  fi
elif systemctl --user cat syren-laptop-audio.service >/dev/null 2>&1; then
  missing_audio_command=""
  for required_command in pactl pw-record nc; do
    if ! command -v "$required_command" >/dev/null 2>&1; then
      missing_audio_command="$required_command"
      break
    fi
  done
  if [ -n "$missing_audio_command" ]; then
    systemctl --user disable --now syren-laptop-audio.service >/dev/null 2>&1 || true
    printf 'Laptop audio is disabled because %s is not installed.\n' "$missing_audio_command" >&2
    printf '%s\n' 'Install the syren-app Debian package to install its audio dependencies.' >&2
  else
    systemctl --user enable --now syren-laptop-audio.service
  fi
else
  printf '%s\n' 'Install the syren-app Debian package before enabling laptop audio.' >&2
fi

systemctl --user --no-pager status syrensystem-stack.service || true

# Real time priority is always the last step, so audio never runs without it.
if [ "$(systemctl show "user@$(id -u).service" -p LimitRTPRIO --value)" -lt 95 ]; then
  printf '%s\n' 'Allowing real time audio priority for this user; sudo asks for your password.'
  sudo sh "$server_directory/deploy/enable-realtime.sh" "$(id -un)"
  printf '%s\n' 'Log out and in again, or reboot, so the server stack starts with real time priority.' >&2
fi
if [ -n "$profile_compose" ]; then
  sleep 5
  for process in snapserver librespot; do
    process_id="$(pgrep -x "$process" | head -n 1 || true)"
    if [ -n "$process_id" ]; then
      printf '%s scheduling: %s\n' "$process" "$(ps -o cls=,rtprio= -p "$process_id")"
    fi
  done
fi
