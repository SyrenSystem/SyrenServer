#!/bin/sh
set -eu

server_directory="$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)"
compose_file="$server_directory/compose.yaml"
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
sed \
  -e "s|__SERVER_DIRECTORY__|$escaped_server_directory|g" \
  -e "s|__COMPOSE_FILE__|$escaped_compose_file|g" \
  "$service_template" > "$service_file"

if [ ! -f "$audio_configuration_directory/audio-sender.conf" ]; then
  printf '%s\n%s\n' \
    'SYREN_SERVER_HOST=127.0.0.1' \
    'SYREN_SERVER_PORT=4953' \
    > "$audio_configuration_directory/audio-sender.conf"
fi

podman-compose -f "$compose_file" build
systemctl --user daemon-reload
systemctl --user enable syrensystem-stack.service
systemctl --user restart syrensystem-stack.service
if systemctl --user cat syren-laptop-audio.service >/dev/null 2>&1; then
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
