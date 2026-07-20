#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="${1:-/tmp/aihub-desktop-smoke.png}"
display_number="${AIHUB_TEST_DISPLAY:-:97}"
app="$repo_root/src/AIHubRouter.Desktop/bin/Release/net10.0/AIHubRouter.dll"
config_root="${XDG_CONFIG_HOME:-/tmp/aihub-desktop-smoke-config}"

if [[ ! -f "$app" ]]; then
  echo "Desktop build not found: $app" >&2
  exit 1
fi

Xvfb "$display_number" -screen 0 1280x960x24 -nolisten tcp >/tmp/aihub-xvfb.log 2>&1 &
xvfb_pid=$!
app_pid=""

cleanup() {
  if [[ -n "$app_pid" ]]; then
    kill "$app_pid" 2>/dev/null || true
    wait "$app_pid" 2>/dev/null || true
  fi
  kill "$xvfb_pid" 2>/dev/null || true
  wait "$xvfb_pid" 2>/dev/null || true
}
trap cleanup EXIT

sleep 1
DISPLAY="$display_number" XDG_CONFIG_HOME="$config_root" dotnet "$app" >/tmp/aihub-desktop.log 2>&1 &
app_pid=$!
sleep 3

DISPLAY="$display_number" ffmpeg \
  -hide_banner -loglevel error \
  -f x11grab -video_size 1280x960 -i "$display_number" \
  -frames:v 1 -y "$output"

test -s "$output"
echo "$output"
