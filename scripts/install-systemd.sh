#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  printf 'Run this installer as root.\n' >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
binary="${1:-$repo_root/artifacts/linux-x64/cli/aihub-router}"

if [[ ! -x "$binary" ]]; then
  printf 'CLI binary not found or not executable: %s\n' "$binary" >&2
  exit 1
fi

if ! getent passwd aihub-router >/dev/null; then
  useradd --system --home-dir /var/lib/aihub-router --shell /usr/sbin/nologin aihub-router
fi

install -d -m 0755 /opt/aihub-router
install -d -o aihub-router -g aihub-router -m 0700 /var/lib/aihub-router
install -d -m 0700 /etc/aihub-router
install -m 0755 "$binary" /opt/aihub-router/aihub-router

if [[ ! -f /etc/aihub-router/environment ]]; then
  master_key="$(openssl rand -base64 32 | tr -d '\n')"
  umask 077
  printf 'AIHUB_ROUTER_MASTER_KEY=%s\n' "$master_key" >/etc/aihub-router/environment
  unset master_key
fi
chmod 0600 /etc/aihub-router/environment

install -m 0644 "$repo_root/deploy/systemd/aihub-router.service" /etc/systemd/system/
install -m 0644 "$repo_root/deploy/systemd/aihub-router-keepalive.service" /etc/systemd/system/
install -m 0644 "$repo_root/deploy/systemd/aihub-router-keepalive.timer" /etc/systemd/system/
systemctl daemon-reload

printf 'Installed. Configure encrypted credentials before starting the service.\n'
